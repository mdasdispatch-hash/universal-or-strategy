---
# TICKET EPIC-4-02: Sticky State Persistence Layer
# Epic: EPIC-4-STICKY-STATE-IPC
# Sequence: 2 of 3
# Depends on: ticket-01 (P1 fixes must be stable before adding persistence)
---

## Objective
Implement cross-session state recovery with atomic snapshots, corruption detection, and rollback capability.

## Scope
IN scope:
- Create new file: `src/V12_002.StickyState.cs`
- Modify: `src/V12_002.Lifecycle.cs` (OnStateChange integration)
- Modify: `src/V12_002.cs` (state initialization)
- Add state fields: `_stickyStateEnabled`, `_stickyStateFilePath`, `_lastSnapshotTicks`
- Implement: Snapshot serialization, atomic file writes, corruption detection, rollback

OUT of scope:
- IPC Hardening features (Ticket 03)
- Automated tests (deferred to Phase 4)
- Performance optimization (deferred to Phase 5)
- Cloud backup (future epic)

## Context References
- Epic 4 Backlog: `docs/brain/EPIC-4-BACKLOG.md` -- Section "Sticky State (Persistence Layer)"
- Jane Street Intel: Lock-free atomic file operations

## Implementation Instructions

### Step 1: Create V12_002.StickyState.cs

**State Snapshot Structure**:
```csharp
[Serializable]
public class StateSnapshot
{
    public long SnapshotTicks { get; set; }
    public string StrategyVersion { get; set; }
    public int PositionSize { get; set; }
    public bool EnableSIMA { get; set; }
    public bool EnableREAPER { get; set; }
    public Dictionary<string, int> AccountPositions { get; set; }
    public string ChecksumSHA256 { get; set; }
    
    public StateSnapshot()
    {
        AccountPositions = new Dictionary<string, int>();
    }
}
```

**Core Methods**:

| Method | Responsibility | LOC | CYC Target |
|--------|---------------|-----|------------|
| CaptureStateSnapshot | Serialize current state to snapshot object | 40 | ≤ 4 |
| WriteSnapshotAtomic | Atomic file write with temp + rename pattern | 50 | ≤ 5 |
| LoadStateSnapshot | Deserialize snapshot with corruption check | 45 | ≤ 5 |
| ValidateSnapshotIntegrity | SHA256 checksum verification | 30 | ≤ 3 |
| RollbackToLastGoodState | Restore from backup on corruption | 35 | ≤ 4 |

### Step 2: Atomic File Write Pattern

**Jane Street Principle**: Never write directly to target file. Use temp + atomic rename.

```csharp
private bool WriteSnapshotAtomic(StateSnapshot snapshot)
{
    string tempPath = _stickyStateFilePath + ".tmp";
    string backupPath = _stickyStateFilePath + ".bak";
    
    try
    {
        // 1. Serialize to temp file
        string json = JsonConvert.SerializeObject(snapshot, Formatting.Indented);
        File.WriteAllText(tempPath, json);
        
        // 2. Compute checksum
        snapshot.ChecksumSHA256 = ComputeSHA256(json);
        
        // 3. Backup existing file (if exists)
        if (File.Exists(_stickyStateFilePath))
        {
            File.Copy(_stickyStateFilePath, backupPath, overwrite: true);
        }
        
        // 4. Atomic rename (POSIX atomic operation)
        File.Move(tempPath, _stickyStateFilePath, overwrite: true);
        
        Interlocked.Exchange(ref _lastSnapshotTicks, DateTime.UtcNow.Ticks);
        return true;
    }
    catch (Exception ex)
    {
        Print(string.Format("[STICKY] Snapshot write failed: {0}", ex.Message));
        
        // Cleanup temp file
        if (File.Exists(tempPath))
            File.Delete(tempPath);
        
        return false;
    }
}
```

### Step 3: Corruption Detection

**Checksum Validation**:
```csharp
private bool ValidateSnapshotIntegrity(StateSnapshot snapshot, string json)
{
    string computedChecksum = ComputeSHA256(json);
    
    if (snapshot.ChecksumSHA256 != computedChecksum)
    {
        Print(string.Format(
            "[STICKY] Checksum mismatch! Expected: {0}, Got: {1}",
            snapshot.ChecksumSHA256, computedChecksum));
        return false;
    }
    
    // Version compatibility check
    if (snapshot.StrategyVersion != BUILD_TAG)
    {
        Print(string.Format(
            "[STICKY] Version mismatch! Snapshot: {0}, Current: {1}",
            snapshot.StrategyVersion, BUILD_TAG));
        return false;
    }
    
    return true;
}

private string ComputeSHA256(string input)
{
    using (var sha256 = System.Security.Cryptography.SHA256.Create())
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(input);
        byte[] hash = sha256.ComputeHash(bytes);
        return BitConverter.ToString(hash).Replace("-", "").ToLower();
    }
}
```

### Step 4: Rollback on Corruption

```csharp
private bool RollbackToLastGoodState()
{
    string backupPath = _stickyStateFilePath + ".bak";
    
    if (!File.Exists(backupPath))
    {
        Print("[STICKY] No backup available for rollback");
        return false;
    }
    
    try
    {
        // Load backup
        string json = File.ReadAllText(backupPath);
        StateSnapshot backup = JsonConvert.DeserializeObject<StateSnapshot>(json);
        
        // Validate backup integrity
        if (!ValidateSnapshotIntegrity(backup, json))
        {
            Print("[STICKY] Backup also corrupted. Cannot rollback.");
            return false;
        }
        
        // Restore backup to primary
        File.Copy(backupPath, _stickyStateFilePath, overwrite: true);
        
        Print(string.Format(
            "[STICKY] Rolled back to snapshot from {0}",
            new DateTime(backup.SnapshotTicks).ToString("yyyy-MM-dd HH:mm:ss")));
        
        return true;
    }
    catch (Exception ex)
    {
        Print(string.Format("[STICKY] Rollback failed: {0}", ex.Message));
        return false;
    }
}
```

### Step 5: Integration - OnStateChange

**File**: `src/V12_002.Lifecycle.cs`

**Trigger Points**:
- State.Realtime → Capture snapshot every 5 minutes
- State.Terminated → Final snapshot before shutdown

```csharp
protected override void OnStateChange()
{
    if (State == State.SetDefaults)
    {
        // ... existing defaults ...
        _stickyStateEnabled = true;
        _stickyStateFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "NinjaTrader 8", "strategies", "V12_002_state.json");
    }
    else if (State == State.Configure)
    {
        // ... existing config ...
    }
    else if (State == State.DataLoaded)
    {
        // NEW: Load persisted state
        if (_stickyStateEnabled)
        {
            StateSnapshot snapshot = LoadStateSnapshot();
            if (snapshot != null)
            {
                RestoreFromSnapshot(snapshot);
            }
        }
    }
    else if (State == State.Realtime)
    {
        // NEW: Schedule periodic snapshots
        if (_stickyStateEnabled)
        {
            // Snapshot every 5 minutes (300 seconds)
            // TODO: Use timer-based approach in Phase 5
        }
    }
    else if (State == State.Terminated)
    {
        // NEW: Final snapshot before shutdown
        if (_stickyStateEnabled)
        {
            StateSnapshot snapshot = CaptureStateSnapshot();
            WriteSnapshotAtomic(snapshot);
        }
    }
}
```

### Step 6: State Restoration

```csharp
private void RestoreFromSnapshot(StateSnapshot snapshot)
{
    Print(string.Format(
        "[STICKY] Restoring state from {0}",
        new DateTime(snapshot.SnapshotTicks).ToString("yyyy-MM-dd HH:mm:ss")));
    
    // Restore configuration
    PositionSize = snapshot.PositionSize;
    EnableSIMA = snapshot.EnableSIMA;
    EnableREAPER = snapshot.EnableREAPER;
    
    // Restore account positions (read-only verification)
    foreach (var kvp in snapshot.AccountPositions)
    {
        Print(string.Format(
            "[STICKY] Snapshot position: {0} = {1}",
            kvp.Key, kvp.Value));
    }
    
    Print("[STICKY] State restoration complete");
}
```

## V12 DNA Guardrails
- [ ] Zero new lock() statements
- [ ] Zero non-ASCII characters in string literals
- [ ] All methods >= 15 LOC (extraction floor)
- [ ] CaptureStateSnapshot CYC ≤ 4
- [ ] WriteSnapshotAtomic CYC ≤ 5
- [ ] LoadStateSnapshot CYC ≤ 5
- [ ] ValidateSnapshotIntegrity CYC ≤ 3
- [ ] RollbackToLastGoodState CYC ≤ 4
- [ ] No logic drift -- pure structural additions only
- [ ] Jane Street Compliance: 100% (Atomic file ops, Checksums, Rollback)

## Post-Edit Verification (Mandatory)
```powershell
# 1. Re-establish hard links (MANDATORY after every src/ edit)
powershell -File .\deploy-sync.ps1

# 2. Complexity verification
python scripts/complexity_audit.py

# 3. Lock regression (must return ZERO)
grep -r "lock(" src/

# 4. ASCII gate (must return ZERO)
grep -Prn "[^\x00-\x7F]" src/
```

## Target Metrics
- **LOC Added**: ~250
  - V12_002.StickyState.cs: ~200
  - V12_002.Lifecycle.cs: ~50 (integration)
- **Files Created**: 1
  - `src/V12_002.StickyState.cs`
- **Files Modified**: 2
  - `src/V12_002.Lifecycle.cs`
  - `src/V12_002.cs` (state field declarations)
- **Methods Added**: 6
  - CaptureStateSnapshot (CYC: ≤4)
  - WriteSnapshotAtomic (CYC: ≤5)
  - LoadStateSnapshot (CYC: ≤5)
  - ValidateSnapshotIntegrity (CYC: ≤3)
  - RollbackToLastGoodState (CYC: ≤4)
  - RestoreFromSnapshot (CYC: ≤3)
- **CYC Reduction**: N/A (new module)

## Acceptance Criteria
- [ ] State snapshots persist to disk atomically
- [ ] Corruption detection via SHA256 checksums
- [ ] Rollback to last good state on corruption
- [ ] Cross-session state recovery operational
- [ ] Version compatibility checks prevent mismatched restores
- [ ] Temp + rename pattern ensures atomic writes
- [ ] Backup file created before overwrite
- [ ] deploy-sync.ps1 ASCII gate: PASS
- [ ] complexity_audit.py shows all methods ≤ target CYC
- [ ] lock() audit: ZERO matches
- [ ] Director presses F5 in NinjaTrader -- BUILD_TAG banner visible
- [ ] BUILD_TAG: `1111.009-epic4-sticky-state`
- [ ] Manual test: Restart NinjaTrader, verify state restored