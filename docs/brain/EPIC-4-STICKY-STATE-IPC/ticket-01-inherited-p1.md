---
# TICKET EPIC-4-01: Inherited P1 Issues
# Epic: EPIC-4-STICKY-STATE-IPC
# Sequence: 1 of 3
# Depends on: NONE
---

## Objective
Address 2 P1 issues inherited from Epic 3: IPC Queue Observability and Entries Quantity Validation.

## Scope
IN scope:
- **P1-A**: IPC Queue Observability
  - Modify: `src/V12_002.UI.IPC.cs` (add _photonDispatchRing.Count monitoring)
  - Modify: `src/V12_002.REAPER.Audit.cs` (integrate queue depth alerts)
- **P1-B**: Entries Quantity Validation
  - Modify: `src/V12_002.Entries.Trend.cs` (ExecuteTREND_DispatchSima, ExecuteTRENDManual_DispatchSima)
  - Add quantity clamping before SIMA dispatch

OUT of scope:
- Sticky State persistence layer (Ticket 02)
- IPC Hardening features (Ticket 03)
- Automated tests (deferred to Phase 4)

## Context References
- Epic 3 Completion: `docs/brain/EPIC-3-COMPLETE.md` -- Section "Known Issues Deferred"
- Epic 4 Backlog: `docs/brain/EPIC-4-BACKLOG.md` -- Section "Inherited from Epic 3"

## Implementation Instructions

### P1-A: IPC Queue Observability

**Problem**: REAPER audit monitors legacy queue, not `_photonDispatchRing`.

**File**: `src/V12_002.UI.IPC.cs`

**Step 1**: Add queue depth accessor method
```csharp
/// <summary>
/// Get current photon dispatch ring depth for monitoring.
/// </summary>
internal int GetPhotonDispatchRingDepth()
{
    return _photonDispatchRing?.Count ?? 0;
}
```

**File**: `src/V12_002.REAPER.Audit.cs`

**Step 2**: Add IPC queue monitoring to audit cycle
```csharp
private void AuditIpcCommandQueue(bool shouldLog)
{
    int queueDepth = GetPhotonDispatchRingDepth();
    int threshold = 1600;  // 80% of 2000 capacity
    
    if (queueDepth >= threshold)
    {
        string msg = string.Format(
            "[REAPER][IPC] Queue depth critical: {0}/{1} (threshold: {2})",
            queueDepth, 2000, threshold);
        Print(msg);
        
        // TODO: Trigger backpressure NACK (Epic 4 Ticket 03)
    }
    else if (shouldLog && queueDepth > 0)
    {
        Print(string.Format("[REAPER][IPC] Queue depth: {0}", queueDepth));
    }
}
```

**Step 3**: Integrate into existing audit cycle
```csharp
// In AuditApexPositions method, add after existing audits:
AuditIpcCommandQueue(shouldLog);
```

**Acceptance**:
- [ ] Queue depth alerts trigger at 80% threshold (1600/2000)
- [ ] Monitoring uses `_photonDispatchRing.Count`, not legacy queue
- [ ] Audit cycle logs queue depth when > 0

---

### P1-B: Entries Quantity Validation

**Problem**: Secondary dispatch methods lack quantity clamping.

**File**: `src/V12_002.Entries.Trend.cs`

**Step 1**: Add quantity validation helper
```csharp
/// <summary>
/// Clamp quantity to PositionSize limit.
/// </summary>
private int ClampEntryQuantity(int requestedQuantity, string entryName)
{
    if (requestedQuantity <= 0)
    {
        Print(string.Format("[ENTRIES] Invalid quantity {0} for {1}. Using PositionSize.", 
            requestedQuantity, entryName));
        return PositionSize;
    }
    
    if (requestedQuantity > PositionSize)
    {
        Print(string.Format("[ENTRIES] Clamping {0} from {1} to PositionSize {2}",
            entryName, requestedQuantity, PositionSize));
        return PositionSize;
    }
    
    return requestedQuantity;
}
```

**Step 2**: Apply clamping to ExecuteTREND_DispatchSima
```csharp
private void ExecuteTREND_DispatchSima(int contracts, MarketPosition direction)
{
    // NEW: Quantity validation
    contracts = ClampEntryQuantity(contracts, "TREND_DispatchSima");
    
    // ... existing dispatch logic ...
}
```

**Step 3**: Apply clamping to ExecuteTRENDManual_DispatchSima
```csharp
private void ExecuteTRENDManual_DispatchSima(int contracts, MarketPosition direction)
{
    // NEW: Quantity validation
    contracts = ClampEntryQuantity(contracts, "TRENDManual_DispatchSima");
    
    // ... existing dispatch logic ...
}
```

**Acceptance**:
- [ ] No orders exceed PositionSize limit
- [ ] Invalid quantities (<=0) default to PositionSize
- [ ] Clamping logs violations for audit trail

---

## V12 DNA Guardrails
- [ ] Zero new lock() statements
- [ ] Zero non-ASCII characters in string literals
- [ ] All new methods >= 15 LOC (extraction floor)
- [ ] GetPhotonDispatchRingDepth CYC = 1
- [ ] AuditIpcCommandQueue CYC ≤ 4
- [ ] ClampEntryQuantity CYC ≤ 3
- [ ] No logic drift -- pure structural additions only
- [ ] Jane Street Compliance: 100% (Atomic, Wait-Free, Bounded, Deterministic)

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
- **LOC Added**: ~80 (40 IPC + 40 Entries)
- **Files Modified**: 3
  - `src/V12_002.UI.IPC.cs`
  - `src/V12_002.REAPER.Audit.cs`
  - `src/V12_002.Entries.Trend.cs`
- **Methods Added**: 3
  - GetPhotonDispatchRingDepth (CYC: 1)
  - AuditIpcCommandQueue (CYC: ≤4)
  - ClampEntryQuantity (CYC: ≤3)
- **CYC Reduction**: N/A (no existing methods modified)

## Acceptance Criteria
- [ ] IPC queue depth monitoring operational
- [ ] Queue alerts trigger at 80% threshold (1600/2000)
- [ ] Entries quantity clamping prevents oversized orders
- [ ] All 2 P1 issues resolved
- [ ] deploy-sync.ps1 ASCII gate: PASS
- [ ] complexity_audit.py shows all methods ≤ target CYC
- [ ] lock() audit: ZERO matches
- [ ] Director presses F5 in NinjaTrader -- BUILD_TAG banner visible
- [ ] BUILD_TAG: `1111.009-epic4-p1-fixes`