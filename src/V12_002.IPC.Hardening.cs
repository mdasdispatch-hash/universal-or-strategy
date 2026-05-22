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

            /// <summary>
            /// Attempt to acquire a rate limit slot. Returns true if under limit.
            /// CYC: 3
            /// </summary>
            public bool TryAcquire()
            {
                long nowTicks = DateTime.UtcNow.Ticks;
                long oneSecondAgo = nowTicks - TimeSpan.TicksPerSecond;

                CleanupOldTimestamps(oneSecondAgo);

                if (_requestTimestamps.Count >= _maxRequestsPerSecond)
                {
                    return false;
                }

                _requestTimestamps.Enqueue(nowTicks);
                return true;
            }

            /// <summary>
            /// Remove timestamps older than cutoff. Lock-free cleanup using ConcurrentQueue atomics.
            /// CYC: 2
            /// </summary>
            private void CleanupOldTimestamps(long cutoffTicks)
            {
                while (_requestTimestamps.TryPeek(out long oldestTicks))
                {
                    if (oldestTicks < cutoffTicks)
                    {
                        _requestTimestamps.TryDequeue(out _);
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
            private volatile bool _isOpen = false;

            public CircuitBreaker(int failureThreshold, TimeSpan resetTimeout)
            {
                _failureThreshold = failureThreshold;
                _resetTimeout = resetTimeout;
            }

            public bool IsOpen => _isOpen;

            /// <summary>
            /// Record successful operation. Resets failure count atomically.
            /// CYC: 2
            /// </summary>
            public void RecordSuccess()
            {
                Interlocked.Exchange(ref _failureCount, 0);
                _isOpen = false;
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
                    _isOpen = true;
                }
            }

            /// <summary>
            /// Attempt to reset circuit breaker after timeout. Returns true if reset.
            /// CYC: 3
            /// </summary>
            public bool TryReset()
            {
                if (!_isOpen)
                    return false;

                long lastFailure = Interlocked.Read(ref _lastFailureTicks);
                long elapsed = DateTime.UtcNow.Ticks - lastFailure;

                if (elapsed >= _resetTimeout.Ticks)
                {
                    Interlocked.Exchange(ref _failureCount, 0);
                    _isOpen = false;
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

            if (!_ipcCommandRateLimiter.TryAcquire())
            {
                Print(string.Format("[IPC][HARDENING] Rate limit exceeded for: {0}", action));
                return ValidationResult.RateLimitExceeded;
            }

            if (_ipcMalformedCircuitBreaker.IsOpen)
            {
                Print("[IPC][HARDENING] Circuit breaker OPEN - rejecting command");
                return ValidationResult.CircuitBreakerOpen;
            }

            if (IsAllowlistBypassAttempt(action, parts))
            {
                _ipcAllowlistBypassDetector.RecordFailure();
                return ValidationResult.AllowlistBypass;
            }

            _ipcMalformedCircuitBreaker.RecordSuccess();
            return ValidationResult.Valid;
        }

        /// <summary>
        /// Validate command format and parameters against allowlist.
        /// CYC: 4
        /// </summary>
        private bool CheckCommandSyntax(string action, string[] parts)
        {
            if (string.IsNullOrWhiteSpace(action))
            {
                Print("[IPC][HARDENING] Empty action rejected");
                return false;
            }

            string[] validActions = new string[]
            {
                "ENABLE_SIMA",
                "DISABLE_SIMA",
                "ENABLE_REAPER",
                "DISABLE_REAPER",
                "SET_POSITION_SIZE",
                "FLATTEN_ALL",
                "EMERGENCY_STOP",
            };

            if (!validActions.Contains(action))
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
        /// CYC: 3
        /// </summary>
        private int GetExpectedParameterCount(string action)
        {
            switch (action)
            {
                case "SET_POSITION_SIZE":
                    return 1;
                case "ENABLE_SIMA":
                case "DISABLE_SIMA":
                case "ENABLE_REAPER":
                case "DISABLE_REAPER":
                case "FLATTEN_ALL":
                case "EMERGENCY_STOP":
                    return 0;
                default:
                    return 0;
            }
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

        /// <summary>
        /// Detect SQL injection and path traversal attempts.
        /// CYC: 4
        /// </summary>
        private bool IsAllowlistBypassAttempt(string action, string[] parts)
        {
            string combined = action + string.Join("", parts);

            string[] sqlPatterns = new string[]
            {
                "SELECT",
                "INSERT",
                "UPDATE",
                "DELETE",
                "DROP",
                "--",
                "/*",
                "*/",
                "xp_",
                "sp_",
            };

            foreach (string pattern in sqlPatterns)
            {
                if (combined.Contains(pattern))
                {
                    Print(string.Format("[IPC][HARDENING] SQL injection attempt detected: {0}", pattern));
                    return true;
                }
            }

            string[] pathPatterns = new string[] { "..", "~", "/etc/", "C:\\" };
            foreach (string pattern in pathPatterns)
            {
                if (combined.Contains(pattern))
                {
                    Print(string.Format("[IPC][HARDENING] Path traversal attempt detected: {0}", pattern));
                    return true;
                }
            }

            return false;
        }

        #endregion
    }
}

// Made with Bob
