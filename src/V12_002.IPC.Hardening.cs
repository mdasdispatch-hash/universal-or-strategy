// <copyright file="V12_002.IPC.Hardening.cs" company="BMad">
// Copyright (c) BMad. All rights reserved.
// </copyright>
// V12.44 MODULAR: IPC Hardening Module (EPIC-4 Ticket 03)
// Contains: Rate limiting, circuit breakers, command validation, anomaly detection
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using NinjaTrader.NinjaScript.Strategies;

namespace NinjaTrader.NinjaScript.Strategies
{
    public partial class V12_002 : Strategy
    {
        #region IPC Hardening (EPIC-4 Ticket 03)

        // State fields for hardening layer
        private RateLimiter _ipcCommandRateLimiter;
        private CircuitBreaker _ipcMalformedCircuitBreaker;
        private CircuitBreaker _ipcAllowlistBypassDetector;
        private int _ipcBackpressureNackCount = 0;

        /// <summary>
        /// Validation result enum for IPC command validation pipeline.
        /// </summary>
        public enum ValidationResult
        {
            Valid,
            InvalidSyntax,
            RateLimitExceeded,
            CircuitBreakerOpen,
            AllowlistBypass,
        }

        /// <summary>
        /// Rate limiter using sliding window algorithm with lock-free queue.
        /// CYC: 3 (TryAcquire), 2 (CleanupOldTimestamps)
        /// </summary>
        public class RateLimiter
        {
            private readonly int _maxRequestsPerSecond;
            private readonly ConcurrentQueue<long> _requestTimestamps;

            public RateLimiter(int maxRequestsPerSecond)
            {
                _maxRequestsPerSecond = maxRequestsPerSecond;
                _requestTimestamps = new ConcurrentQueue<long>();
            }

            // EPIC-4 P1 Fix #7: Atomic counter to prevent TOCTOU race
            private int _atomicCount = 0;

            /// <summary>
            /// Attempt to acquire a rate limit slot. Returns true if under limit.
            /// EPIC-4 P1 Fix #7: Uses atomic counter to prevent TOCTOU race between Count check and Enqueue.
            /// CYC: 3
            /// </summary>
            public bool TryAcquire()
            {
                long nowTicks = DateTime.UtcNow.Ticks;
                long oneSecondAgo = nowTicks - TimeSpan.TicksPerSecond;

                CleanupOldTimestamps(oneSecondAgo);

                // Atomic increment-and-check to prevent TOCTOU race
                int newCount = Interlocked.Increment(ref _atomicCount);
                if (newCount > _maxRequestsPerSecond)
                {
                    Interlocked.Decrement(ref _atomicCount);
                    return false;
                }

                _requestTimestamps.Enqueue(nowTicks);
                return true;
            }

            /// <summary>
            /// Remove timestamps older than cutoff. Lock-free cleanup using ConcurrentQueue atomics.
            /// EPIC-4 P1 Fix #7: Decrements atomic counter when removing old timestamps.
            /// CYC: 2
            /// </summary>
            private void CleanupOldTimestamps(long cutoffTicks)
            {
                while (_requestTimestamps.TryPeek(out long oldestTicks))
                {
                    if (oldestTicks < cutoffTicks)
                    {
                        if (_requestTimestamps.TryDequeue(out _))
                        {
                            Interlocked.Decrement(ref _atomicCount);
                        }
                    }
                    else
                    {
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Circuit breaker using atomic primitives for lock-free state management.
        /// CYC: 2 (RecordSuccess), 2 (RecordFailure), 3 (TryReset)
        /// </summary>
        public class CircuitBreaker
        {
            private readonly int _failureThreshold;
            private readonly TimeSpan _resetTimeout;
            private int _failureCount = 0;
            private long _lastFailureTicks = 0;
            private int _isOpen = 0;

            public CircuitBreaker(int failureThreshold, TimeSpan resetTimeout)
            {
                _failureThreshold = failureThreshold;
                _resetTimeout = resetTimeout;
            }

            public bool IsOpen => Interlocked.CompareExchange(ref _isOpen, 0, 0) == 1;

            /// <summary>
            /// Record successful operation. Resets failure count atomically.
            /// CYC: 2
            /// </summary>
            public void RecordSuccess()
            {
                Interlocked.Exchange(ref _failureCount, 0);
                Interlocked.Exchange(ref _isOpen, 0);
            }

            /// <summary>
            /// Record failed operation. Opens circuit if threshold exceeded.
            /// CYC: 2
            /// </summary>
            public void RecordFailure()
            {
                int newCount = Interlocked.Increment(ref _failureCount);
                Interlocked.Exchange(ref _lastFailureTicks, DateTime.UtcNow.Ticks);

                if (newCount >= _failureThreshold)
                {
                    Interlocked.Exchange(ref _isOpen, 1);
                }
            }

            /// <summary>
            /// Attempt to reset circuit breaker after timeout. Returns true if reset.
            /// CYC: 3
            /// </summary>
            public bool TryReset()
            {
                if (Interlocked.CompareExchange(ref _isOpen, 0, 0) == 0)
                    return false;

                long lastFailure = Interlocked.Read(ref _lastFailureTicks);
                long elapsed = DateTime.UtcNow.Ticks - lastFailure;

                if (elapsed >= _resetTimeout.Ticks)
                {
                    Interlocked.Exchange(ref _failureCount, 0);
                    Interlocked.Exchange(ref _isOpen, 0);
                    return true;
                }

                return false;
            }
        }

        /// <summary>
        /// Initialize IPC hardening layer. Called from OnStateChange.
        /// CYC: 1
        /// </summary>
        private void InitializeIpcHardening()
        {
            _ipcCommandRateLimiter = new RateLimiter(1600);
            _ipcMalformedCircuitBreaker = new CircuitBreaker(10, TimeSpan.FromSeconds(1));
            _ipcAllowlistBypassDetector = new CircuitBreaker(20, TimeSpan.FromMinutes(1));
            _ipcBackpressureNackCount = 0;
        }

        /// <summary>
        /// Primary validation entry point for IPC commands.
        /// CYC: 5
        /// </summary>
        private ValidationResult ValidateIpcCommand(string action, string[] parts)
        {
            if (!CheckCommandSyntax(action, parts))
            {
                _ipcMalformedCircuitBreaker.RecordFailure();
                return ValidationResult.InvalidSyntax;
            }

            // EPIC-4 P0 Fix #5: Check circuit breaker BEFORE consuming rate limiter slot
            if (_ipcMalformedCircuitBreaker.IsOpen)
            {
                Print("[IPC][HARDENING] Circuit breaker OPEN - rejecting command");
                return ValidationResult.CircuitBreakerOpen;
            }

            if (!_ipcCommandRateLimiter.TryAcquire())
            {
                Print(string.Format("[IPC][HARDENING] Rate limit exceeded for: {0}", action));
                return ValidationResult.RateLimitExceeded;
            }

            if (IsAllowlistBypassAttempt(action, parts))
            {
                _ipcAllowlistBypassDetector.RecordFailure();
                return ValidationResult.AllowlistBypass;
            }

            _ipcMalformedCircuitBreaker.RecordSuccess();
            return ValidationResult.Valid;
        }

        // EPIC-4 P0-1 Fix: Aligned ValidIpcActions with AllowedIpcActions HashSet from V12_002.UI.IPC.cs
        // This prevents circuit breaker trips from legitimate commands being rejected as "unknown"
        // Static readonly array eliminates hot-path heap allocations (1600 req/sec)
        private static readonly string[] ValidIpcActions = new string[]
        {
            "TRIM_25",
            "TRIM_50",
            "CONFIG",
            "SET_TRAIL",
            "SET_CIT",
            "LOCK_50",
            "BE",
            "BE_CUSTOM",
            "BE_PLUS_2",
            "BE_PLUS_1",
            "FLATTEN_ONLY",
            "FLATTEN",
            "CANCEL_ALL",
            "RESET_MEMORY",
            "LONG",
            "SHORT",
            "OR_LONG",
            "OR_SHORT",
            "SET_SIMA",
            "DIAG_FLEET",
            "SET_RMA_MODE",
            "SYNC_MODE",
            "SET_TARGETS",
            "MKT_SYNC",
            "SYNC_ALL",
            "SET_MODE",
            "SET_LEADER_ACCOUNT",
            "REQUEST_FLEET_STATE",
            "SET_MANUAL_PRICE",
            "TREND_MANUAL_LIMIT",
            "RETEST_MANUAL_LIMIT",
            "FFMA_MANUAL_LIMIT",
            "FFMA_MANUAL_MARKET",
            "FFMA_DISARM",
            "GET_LAYOUT",
            "DIAG_IPC",
        };

        /// <summary>
        /// Validate command format and parameters against allowlist.
        /// EPIC-4 P0 Fix #1: Aligned with AllowedIpcActions to prevent circuit breaker trips.
        /// EPIC-4 P0 Fix #2: Uses static readonly array to eliminate hot-path allocations.
        /// CYC: 4
        /// </summary>
        private bool CheckCommandSyntax(string action, string[] parts)
        {
            if (string.IsNullOrWhiteSpace(action))
            {
                Print("[IPC][HARDENING] Empty action rejected");
                return false;
            }

            if (!ValidIpcActions.Contains(action))
            {
                Print(string.Format("[IPC][HARDENING] Unknown action: {0}", action));
                return false;
            }

            int expectedParams = GetExpectedParameterCount(action);
            if (parts.Length != expectedParams)
            {
                Print(
                    string.Format(
                        "[IPC][HARDENING] Parameter count mismatch for {0}: expected {1}, got {2}",
                        action,
                        expectedParams,
                        parts.Length
                    )
                );
                return false;
            }

            return true;
        }

        /// <summary>
        /// Get expected parameter count for a given action.
        /// EPIC-4 P0-1 Fix: Removed phantom commands (ENABLE_SIMA, FLATTEN_ALL, etc.) that don't exist in ValidIpcActions.
        /// All commands in ValidIpcActions have variable parameter counts handled by dispatcher, so default to 0.
        /// CYC: 1
        /// </summary>
        private int GetExpectedParameterCount(string action)
        {
            // All valid IPC actions have their parameter validation handled by the dispatcher
            // This method now serves as a placeholder for future parameter count enforcement
            return 0;
        }

        /// <summary>
        /// Send backpressure NACK response to client.
        /// CYC: 2
        /// </summary>
        private void SendBackpressureNack(string action)
        {
            Print(string.Format("[IPC][HARDENING] NACK sent for: {0}", action));
            Interlocked.Increment(ref _ipcBackpressureNackCount);
        }

        // EPIC-4 P0 Fix #2: Static readonly arrays to eliminate hot-path heap allocations
        private static readonly string[] SqlInjectionPatterns = new string[]
        {
            "SELECT",
            "INSERT",
            "UPDATE",
            "DELETE",
            "DROP",
            "--",
            "/*",
            "*/",
            "XP_",
            "SP_",
        };

        private static readonly string[] PathTraversalPatterns = new string[] { "..", "~", "/etc/", "C:\\" };

        /// <summary>
        /// Detect SQL injection and path traversal attempts.
        /// EPIC-4 P0 Fix #2: Uses static readonly arrays to eliminate hot-path allocations.
        /// CYC: 4
        /// </summary>
        private bool IsAllowlistBypassAttempt(string action, string[] parts)
        {
            // Check action first (avoid Join allocation)
            foreach (string pattern in SqlInjectionPatterns)
            {
                if (action.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            // Check each part separately (avoid Join allocation)
            foreach (string part in parts)
            {
                foreach (string pattern in SqlInjectionPatterns)
                {
                    if (part.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        Print(string.Format("[IPC][HARDENING] SQL injection attempt detected: {0}", pattern));
                        return true;
                    }
                }
            }

            // Check path traversal patterns
            foreach (string pattern in PathTraversalPatterns)
            {
                if (action.IndexOf(pattern, StringComparison.Ordinal) >= 0)
                {
                    Print(string.Format("[IPC][HARDENING] Path traversal attempt detected: {0}", pattern));
                    return true;
                }
            }

            foreach (string part in parts)
            {
                foreach (string pattern in PathTraversalPatterns)
                {
                    if (part.IndexOf(pattern, StringComparison.Ordinal) >= 0)
                    {
                        Print(string.Format("[IPC][HARDENING] Path traversal attempt detected: {0}", pattern));
                        return true;
                    }
                }
            }

            return false;
        }

        #endregion
    }
}

// Made with Bob
