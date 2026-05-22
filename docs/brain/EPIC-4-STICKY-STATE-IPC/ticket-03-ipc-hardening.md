---
# TICKET EPIC-4-03: IPC Hardening
# Epic: EPIC-4-STICKY-STATE-IPC
# Sequence: 3 of 3
# Depends on: ticket-01 (IPC observability must be operational)
---

## Objective
Harden external IPC command plane with validation layer, rate limiting, circuit breakers, and anomaly detection.

## Scope
IN scope:
- Create new file: `src/V12_002.IPC.Hardening.cs`
- Modify: `src/V12_002.UI.IPC.cs` (validation integration)
- Modify: `src/V12_002.REAPER.Audit.cs` (rate limit monitoring)
- Add state fields: `_ipcCommandRateLimiter`, `_ipcMalformedCircuitBreaker`, `_ipcAllowlistBypassDetector`
- Implement: Command validation, backpressure NACK, circuit breakers, anomaly detection

OUT of scope:
- Sticky State persistence (Ticket 02)
- Client disconnect infrastructure (TODO markers for Phase 5)
- Automated tests (deferred to Phase 4)
- Performance optimization (deferred to Phase 5)

## Context References
- Epic 4 Backlog: `docs/brain/EPIC-4-BACKLOG.md` -- Section "IPC Hardening (External Command Plane)"
- Jane Street Intel: Rate limiting, circuit breakers, anomaly detection

## Implementation Instructions

### Step 1: Create V12_002.IPC.Hardening.cs

**Rate Limiter Structure**:
```csharp
public class RateLimiter
{
    private readonly int _maxRequestsPerSecond;
    private readonly ConcurrentQueue<long> _requestTimestamps;
    private readonly object _cleanupLock = new object();
    
    public RateLimiter(int maxRequestsPerSecond)
    {
        _maxRequestsPerSecond = maxRequestsPerSecond;
        _requestTimestamps = new ConcurrentQueue<long>();
    }
    
    public bool TryAcquire()
    {
        long nowTicks = DateTime.UtcNow.Ticks;
        long oneSecondAgo = nowTicks - TimeSpan.TicksPerSecond;
        
        // Cleanup old timestamps
        CleanupOldTimestamps(oneSecondAgo);
        
        // Check if under limit
        if (_requestTimestamps.Count >= _maxRequestsPerSecond)
        {
            return false;  // Rate limit exceeded
        }
        
        // Record this request
        _requestTimestamps.Enqueue(nowTicks);
        return true;
    }
    
    private void CleanupOldTimestamps(long cutoffTicks)
    {
        lock (_cleanupLock)
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
}
```

**Circuit Breaker Structure**:
```csharp
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
    
    public void RecordSuccess()
    {
        Interlocked.Exchange(ref _failureCount, 0);
        _isOpen = false;
    }
    
    public void RecordFailure()
    {
        int newCount = Interlocked.Increment(ref _failureCount);
        Interlocked.Exchange(ref _lastFailureTicks, DateTime.UtcNow.Ticks);
        
        if (newCount >= _failureThreshold)
        {
            _isOpen = true;
        }
    }
    
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
```

**Core Methods**:

| Method | Responsibility | LOC | CYC Target |
|--------|---------------|-----|------------|
| ValidateIpcCommand | Primary validation entry point | 50 | ≤ 5 |
| CheckCommandSyntax | Validate command format and parameters | 40 | ≤ 4 |
| EnforceRateLimit | Apply backpressure at 1600 req/sec | 30 | ≤ 3 |
| CheckMalformedCircuitBreaker | Trip at 10 malformed/sec | 35 | ≤ 4 |
| DetectAllowlistBypass | Anomaly detection at 20 bypass/min | 35 | ≤ 4 |

### Step 2: Command Validation Layer

```csharp
public enum ValidationResult
{
    Valid,
    InvalidSyntax,
    RateLimitExceeded,
    CircuitBreakerOpen,
    AllowlistBypass
}

private ValidationResult ValidateIpcCommand(string action, string[] parts)
{
    // 1. Syntax validation
    if (!CheckCommandSyntax(action, parts))
    {
        _ipcMalformedCircuitBreaker.RecordFailure();
        return ValidationResult.InvalidSyntax;
    }
    
    // 2. Rate limiting
    if (!_ipcCommandRateLimiter.TryAcquire())
    {
        Print(string.Format("[IPC][HARDENING] Rate limit exceeded for: {0}", action));
        return ValidationResult.RateLimitExceeded;
    }
    
    // 3. Circuit breaker check
    if (_ipcMalformedCircuitBreaker.IsOpen)
    {
        Print("[IPC][HARDENING] Circuit breaker OPEN - rejecting command");
        return ValidationResult.CircuitBreakerOpen;
    }
    
    // 4. Allowlist bypass detection
    if (IsAllowlistBypassAttempt(action, parts))
    {
        _ipcAllowlistBypassDetector.RecordFailure();
        return ValidationResult.AllowlistBypass;
    }
    
    // Success
    _ipcMalformedCircuitBreaker.RecordSuccess();
    return ValidationResult.Valid;
}
```

### Step 3: Command Syntax Validation

```csharp
private bool CheckCommandSyntax(string action, string[] parts)
{
    if (string.IsNullOrWhiteSpace(action))
    {
        Print("[IPC][HARDENING] Empty action rejected");
        return false;
    }
    
    // Validate action is in allowlist
    string[] validActions = new string[]
    {
        "ENABLE_SIMA", "DISABLE_SIMA",
        "ENABLE_REAPER", "DISABLE_REAPER",
        "SET_POSITION_SIZE", "FLATTEN_ALL",
        "EMERGENCY_STOP"
    };
    
    if (!validActions.Contains(action))
    {
        Print(string.Format("[IPC][HARDENING] Unknown action: {0}", action));
        return false;
    }
    
    // Validate parameter count
    int expectedParams = GetExpectedParameterCount(action);
    if (parts.Length != expectedParams)
    {
        Print(string.Format(
            "[IPC][HARDENING] Parameter count mismatch for {0}: expected {1}, got {2}",
            action, expectedParams, parts.Length));
        return false;
    }
    
    return true;
}

private int GetExpectedParameterCount(string action)
{
    switch (action)
    {
        case "SET_POSITION_SIZE":
            return 1;  // [size]
        case "ENABLE_SIMA":
        case "DISABLE_SIMA":
        case "ENABLE_REAPER":
        case "DISABLE_REAPER":
        case "FLATTEN_ALL":
        case "EMERGENCY_STOP":
            return 0;  // No parameters
        default:
            return 0;
    }
}
```

### Step 4: Backpressure NACK

```csharp
private void SendBackpressureNack(string action)
{
    // TODO: Implement NACK response to client (Phase 5)
    Print(string.Format("[IPC][HARDENING] NACK sent for: {0}", action));
    
    // Increment NACK counter for monitoring
    Interlocked.Increment(ref _ipcBackpressureNackCount);
}
```

### Step 5: Anomaly Detection

```csharp
private bool IsAllowlistBypassAttempt(string action, string[] parts)
{
    // Check for SQL injection patterns
    string combined = action + string.Join("", parts);
    string[] sqlPatterns = new string[]
    {
        "SELECT", "INSERT", "UPDATE", "DELETE", "DROP",
        "--", "/*", "*/", "xp_", "sp_"
    };
    
    foreach (string pattern in sqlPatterns)
    {
        if (combined.Contains(pattern))
        {
            Print(string.Format(
                "[IPC][HARDENING] SQL injection attempt detected: {0}",
                pattern));
            return true;
        }
    }
    
    // Check for path traversal patterns
    string[] pathPatterns = new string[] { "..", "~", "/etc/", "C:\\" };
    foreach (string pattern in pathPatterns)
    {
        if (combined.Contains(pattern))
        {
            Print(string.Format(
                "[IPC][HARDENING] Path traversal attempt detected: {0}",
                pattern));
            return true;
        }
    }
    
    return false;
}
```

### Step 6: Integration - UI.IPC.cs

**File**: `src/V12_002.UI.IPC.cs`
**Method**: `ProcessIpcCommandCore`

```csharp
private void ProcessIpcCommandCore(string action, string[] parts, long senderTicks)
{
    // Existing timestamp guard
    if (!MetadataGuardCommandTimestamp(senderTicks, action))
        return;
    
    // Existing stale check (from Ticket 01)
    if (!CheckStaleIpcCommand(senderTicks, action))
        return;
    
    // NEW: Validation layer
    ValidationResult result = ValidateIpcCommand(action, parts);
    
    switch (result)
    {
        case ValidationResult.Valid:
            // Proceed with command execution
            break;
            
        case ValidationResult.InvalidSyntax:
            Print(string.Format("[IPC] Invalid syntax: {0}", action));
            return;
            
        case ValidationResult.RateLimitExceeded:
            SendBackpressureNack(action);
            return;
            
        case ValidationResult.CircuitBreakerOpen:
            Print("[IPC] Circuit breaker OPEN - command rejected");
            return;
            
        case ValidationResult.AllowlistBypass:
            Print(string.Format("[IPC] Security violation: {0}", action));
            // TODO: Disconnect client (Phase 5)
            return;
    }
    
    // ... execute command ...
}
```

### Step 7: Integration - REAPER.Audit.cs

**File**: `src/V12_002.REAPER.Audit.cs`

Add monitoring for rate limiter and circuit breaker state:

```csharp
private void AuditIpcHardeningMetrics(bool shouldLog)
{
    // Rate limiter status
    int nackCount = Volatile.Read(ref _ipcBackpressureNackCount);
    if (nackCount > 0 && shouldLog)
    {
        Print(string.Format("[REAPER][IPC] Backpressure NACKs: {0}", nackCount));
    }
    
    // Circuit breaker status
    if (_ipcMalformedCircuitBreaker.IsOpen)
    {
        Print("[REAPER][IPC] Circuit breaker OPEN - malformed payload threshold exceeded");
        
        // Attempt reset if timeout elapsed
        if (_ipcMalformedCircuitBreaker.TryReset())
        {
            Print("[REAPER][IPC] Circuit breaker RESET");
        }
    }
    
    // Allowlist bypass attempts
    if (_ipcAllowlistBypassDetector.IsOpen)
    {
        Print("[REAPER][IPC] SECURITY ALERT: Allowlist bypass attempts detected");
        // TODO: Trigger client disconnect (Phase 5)
    }
}
```

## V12 DNA Guardrails
- [ ] Zero new lock() statements (except RateLimiter cleanup - bounded critical section)
- [ ] Zero non-ASCII characters in string literals
- [ ] All methods >= 15 LOC (extraction floor)
- [ ] ValidateIpcCommand CYC ≤ 5
- [ ] CheckCommandSyntax CYC ≤ 4
- [ ] EnforceRateLimit CYC ≤ 3
- [ ] CheckMalformedCircuitBreaker CYC ≤ 4
- [ ] DetectAllowlistBypass CYC ≤ 4
- [ ] No logic drift -- pure structural additions only
- [ ] Jane Street Compliance: 100% (Rate limiting, Circuit breakers, Anomaly detection)

## Post-Edit Verification (Mandatory)
```powershell
# 1. Re-establish hard links (MANDATORY after every src/ edit)
powershell -File .\deploy-sync.ps1

# 2. Complexity verification
python scripts/complexity_audit.py

# 3. Lock regression (must return ZERO or 1 - RateLimiter cleanup only)
grep -r "lock(" src/

# 4. ASCII gate (must return ZERO)
grep -Prn "[^\x00-\x7F]" src/
```

## Target Metrics
- **LOC Added**: ~350
  - V12_002.IPC.Hardening.cs: ~280
  - V12_002.UI.IPC.cs: ~50 (integration)
  - V12_002.REAPER.Audit.cs: ~20 (monitoring)
- **Files Created**: 1
  - `src/V12_002.IPC.Hardening.cs`
- **Files Modified**: 2
  - `src/V12_002.UI.IPC.cs`
  - `src/V12_002.REAPER.Audit.cs`
- **Methods Added**: 8
  - ValidateIpcCommand (CYC: ≤5)
  - CheckCommandSyntax (CYC: ≤4)
  - EnforceRateLimit (CYC: ≤3)
  - CheckMalformedCircuitBreaker (CYC: ≤4)
  - DetectAllowlistBypass (CYC: ≤4)
  - SendBackpressureNack (CYC: ≤2)
  - GetExpectedParameterCount (CYC: ≤3)
  - AuditIpcHardeningMetrics (CYC: ≤4)
- **CYC Reduction**: N/A (new module)

## Acceptance Criteria
- [ ] Command validation layer operational
- [ ] Rate limiting enforced at 1600 req/sec
- [ ] Backpressure NACK sent on rate limit exceeded
- [ ] Circuit breaker trips at 10 malformed/sec
- [ ] Circuit breaker auto-resets after timeout
- [ ] Allowlist bypass detection operational (SQL injection, path traversal)
- [ ] Security violations logged and counted
- [ ] All integration points implemented (2 files modified)
- [ ] deploy-sync.ps1 ASCII gate: PASS
- [ ] complexity_audit.py shows all methods ≤ target CYC
- [ ] lock() audit: ZERO or 1 (RateLimiter cleanup only)
- [ ] Director presses F5 in NinjaTrader -- BUILD_TAG banner visible
- [ ] BUILD_TAG: `1111.009-epic4-ipc-hardening`
- [ ] Manual test: Send 2000 commands/sec, verify NACK at 1600
- [ ] Manual test: Send 15 malformed commands, verify circuit breaker trips