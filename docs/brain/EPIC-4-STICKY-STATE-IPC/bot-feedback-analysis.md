# PR #2 Bot Feedback Analysis

**Date**: 2026-05-22  
**Epic**: EPIC-4-STICKY-STATE-IPC  
**PR**: #2  
**Status**: ANALYSIS COMPLETE - READY FOR FIXES

---

## Executive Summary

**Total Issues**: 33 findings across 7 bots  
**P0 Blocking**: 2 (StyleCop, CodeFactor)  
**P1 Critical**: 8 (cubic, CodeRabbit)  
**P2 Major**: 15 (CodeRabbit, cubic)  
**Infra Noise**: 8 (quota limits, paused reviews)

**Fix Strategy**: Address P0 → P1 → P2 in batches with build verification after each batch.

---

## Bot Review Summary

### ✅ ACTIVE REVIEWS
1. **CodeRabbit** - 27 comments (P1: 5, P2: 15, Nitpick: 7)
2. **cubic** - 6 issues (P1: 2, P2: 4)
3. **Sourcery** - Sequence diagram + file-level changes (informational)
4. **PR Insights Tagger** - Metrics only (Low Risk, 4.38/10 complexity)

### ❌ BLOCKED/PAUSED REVIEWS
5. **Amazon Q (Qodo)** - Paused (requires paid seat)
6. **CodeSlick** - Blocked (monthly limit reached)
7. **Greptile** - Timeout (90s limit exceeded)
8. **Codacy** - Timeout (90s limit exceeded)
9. **CodeQL** - Timeout (90s limit exceeded)

---

## P0 BLOCKING ISSUES (CI Failures)

### P0-1: StyleCop Lint Failures
**Source**: CI Check (StyleCop)  
**Status**: [VALID] - Requires manual fix  
**Files**: Multiple (TBD via lint.ps1)  
**Fix**: Run `powershell -File .\scripts\lint.ps1` to identify violations, then fix manually

### P0-2: CodeFactor Failures
**Source**: CI Check (CodeFactor)  
**Status**: [VALID] - Requires manual fix  
**Protocol**: NEVER use CodeFactor auto-fix (per CODEFACTOR_PROTOCOL.md)  
**Fix**: Manual fixes only with build verification

---

## P1 CRITICAL ISSUES (Must Fix Before Merge)

### P1-1: Duplicate Using Directives (cubic)
**File**: `src/V12_002.SIMA.Dispatch.cs:22`  
**Issue**: All 10 added lines are duplicate `using` directives  
**Category**: [VALID] - Merge artifact  
**Fix**: Remove duplicate using statements

### P1-2: IPC Hot Path Allocations (cubic)
**File**: `src/V12_002.IPC.Hardening.cs:291`  
**Issue**: `.ToUpperInvariant()` + `string.Join` = 3 allocations per IPC command (4800/sec at 1600 req/sec limit)  
**Category**: [VALID] - Jane Street violation (microsecond latency)  
**Fix**: Use pre-uppercased static patterns + `IndexOf(..., StringComparison.OrdinalIgnoreCase)`

### P1-3: FSM Enqueue Pattern Violation (cubic)
**File**: `src/V12_002.Entries.Trend.cs:444`  
**Issue**: Direct synchronous call bypasses FSM Enqueue pattern (atomicity asymmetry)  
**Category**: [HALLUCINATION] - CodeRabbit approved this as LGTM (closes tracking window)  
**Resolution**: Keep as-is (synchronous call is correct per Build 981 Protocol)

### P1-4: Unreachable Phase 6 (cubic)
**File**: `.bob/commands/epic-run.md:268`  
**Issue**: Step G says "advance to EPIC COMPLETE" without routing through Phase 6  
**Category**: [VALID] - Workflow gap  
**Fix**: Update Step G to route to Phase 6

### P1-5: E2 Expected Position Deferred (CodeRabbit)
**File**: `src/V12_002.Entries.Trend.cs:509-515`  
**Issue**: E2 still uses `Enqueue(ctx => ctx.AddExpectedPositionDeltaLocked(...))` instead of synchronous call  
**Category**: [VALID] - Inconsistent with E1 fix  
**Fix**: Apply same synchronous pattern as E1

### P1-6: TRENDManual Expected Position Deferred (CodeRabbit)
**File**: `src/V12_002.Entries.Trend.cs:934-940`  
**Issue**: Same as P1-5 for manual TREND entries  
**Category**: [VALID]  
**Fix**: Apply synchronous pattern

### P1-7: Account Positions Not Restored (CodeRabbit)
**File**: `src/V12_002.StickyState.cs:248-255`  
**Issue**: `RestoreFromSnapshot` only prints positions, doesn't populate `expectedPositions` dictionary  
**Category**: [VALID] - Critical runtime bug  
**Fix**: Populate `expectedPositions` + use BMad aliases in logs

### P1-8: DateTime UTC Kind Missing (CodeRabbit)
**File**: `src/V12_002.StickyState.cs:216`  
**Issue**: `new DateTime(backup.SnapshotTicks)` without `DateTimeKind.Utc`  
**Category**: [VALID] - Timezone bug  
**Fix**: Add `DateTimeKind.Utc` parameter

---

## P2 MAJOR ISSUES (Fix If Time Permits)

### P2-1: Rebase Conflict Handling (cubic)
**File**: `.bob/commands/pr-loop.md:33`  
**Issue**: No error handling for rebase conflicts  
**Category**: [VALID] - Workflow gap  
**Fix**: Add HALT instruction for rebase failures

### P2-2: Stale Local Branch Check (cubic)
**File**: `scripts/verify_pr_hygiene.ps1:14`  
**Issue**: `--is-ancestor` fallback uses local `$BaseBranch` instead of `origin/$BaseBranch`  
**Category**: [VALID] - CI inconsistency  
**Fix**: Use `origin/$BaseBranch` consistently

### P2-3: IPC Command Length Unbounded (CodeRabbit)
**File**: `src/V12_002.IPC.Hardening.cs:210-217`  
**Issue**: No max length validation before concatenation  
**Category**: [VALID] - DoS vector  
**Fix**: Add `MAX_ACTION_LEN` and `MAX_PAYLOAD_LEN` checks

### P2-4: SQL Pattern Case Mismatch (CodeRabbit)
**File**: `src/V12_002.IPC.Hardening.cs:290-305`  
**Issue**: `ToUpperInvariant()` but patterns are lowercase ("xp_", "sp_")  
**Category**: [VALID] - Logic bug  
**Fix**: Uppercase patterns or remove ToUpperInvariant()

### P2-5: Missing IPC Reject Marker (CodeRabbit)
**File**: `src/V12_002.IPC.Hardening.cs:212-216`  
**Issue**: Rejection logs missing "V12 IPC REJECT" marker  
**Category**: [VALID] - Audit compliance  
**Fix**: Add marker to all rejection branches

### P2-6: Empty Catch Block (CodeRabbit)
**File**: `src/V12_002.StickyState.cs:100`  
**Issue**: Swallows cleanup errors silently  
**Category**: [VALID] - Observability gap  
**Fix**: Add minimal logging

### P2-7: Non-Atomic File Replace (CodeRabbit)
**File**: `src/V12_002.StickyState.cs:75-85`  
**Issue**: Delete-then-move leaves window where no snapshot exists  
**Category**: [VALID] - Crash safety  
**Fix**: Use `File.Replace()` for atomic operation

### P2-8: MODE_M Flat Bar Position Sizing (CodeRabbit)
**File**: `src/V12_002.Lifecycle.cs:735-756`  
**Issue**: `tickSize * 2` fallback may be too aggressive for flat bars  
**Category**: [VALID] - Risk management  
**Fix**: Add conservative minimum stop distance

### P2-9: Raw Account Name in Compliance JSON (CodeRabbit)
**File**: `src/V12_002.UI.Compliance.cs:791`  
**Issue**: `acct.Name` not obscured with BMad alias  
**Category**: [VALID] - Security/privacy  
**Fix**: Use BMad alias utility

### P2-10: Markdown Lint Violations (CodeRabbit)
**File**: `.bob/commands/epic-run.md:273-293`  
**Issue**: Fenced code blocks missing language identifier + blank lines  
**Category**: [VALID] - Style  
**Fix**: Add `text` language identifier + blank lines

### P2-11-15: Additional CodeRabbit Nitpicks
**Status**: Deferred to post-merge cleanup

---

## INFRA NOISE (No Action Required)

1. **Amazon Q (Qodo)** - Paused (requires paid seat + Git link)
2. **CodeSlick** - Monthly limit (20/20 used, resets May 31)
3. **Greptile** - Timeout (90s limit)
4. **Codacy** - Timeout (90s limit)
5. **CodeQL** - Timeout (90s limit)
6. **SonarCloud** - Duplicate using warning (covered by P1-1)
7. **CodeAnt AI** - "Reviewing PR" (no findings yet)
8. **PR Insights Tagger** - Metrics only (informational)

---

## FIX EXECUTION PLAN

### Batch 1: P0 Blocking (StyleCop + CodeFactor)
1. Run `powershell -File .\scripts\lint.ps1`
2. Fix StyleCop violations manually
3. Fix CodeFactor issues manually (NO AUTO-FIX)
4. Run `powershell -File .\deploy-sync.ps1` to verify build

### Batch 2: P1 Critical (Logic Bugs)
1. Fix P1-1: Remove duplicate usings in SIMA.Dispatch.cs
2. Fix P1-5: E2 synchronous expected position
3. Fix P1-6: TRENDManual synchronous expected position
4. Fix P1-7: Restore account positions in StickyState
5. Fix P1-8: Add DateTimeKind.Utc to DateTime constructor
6. Run `powershell -File .\deploy-sync.ps1` to verify build

### Batch 3: P1 Critical (Performance)
1. Fix P1-2: Remove IPC hot path allocations
2. Run `powershell -File .\deploy-sync.ps1` to verify build

### Batch 4: P1 Critical (Workflow)
1. Fix P1-4: Update epic-run.md Phase 6 routing
2. No build verification needed (docs only)

### Batch 5: P2 Major (If Time Permits)
1. Fix P2-1 through P2-10 in order
2. Run `powershell -File .\deploy-sync.ps1` after each file change

### Final Verification
1. Run `powershell -File .\scripts\calculate_fleet_score.ps1`
2. Target: 15/15 local score
3. Emit: [LOCAL-READY] PHS 15/15

---

## NOTES

- **CodeRabbit LGTM on P1-3**: The synchronous call in Entries.Trend.cs:444 is CORRECT per Build 981 Protocol. cubic's "FSM violation" is a false positive.
- **Sourcery Review**: Informational only (sequence diagram + file changes). No actionable findings.
- **Timeout Issues**: Greptile, Codacy, CodeQL all timed out at 90s. Not a code issue.
- **CODEFACTOR_PROTOCOL**: NEVER use "Apply fixes" button. Manual fixes only with build verification.

---

## REFERENCES

- PR: https://github.com/mdasdispatch-hash/universal-or-strategy/pull/2
- CODEFACTOR_PROTOCOL: `docs/protocol/CODEFACTOR_PROTOCOL.md`
- Build 981 Protocol: Direct writes to stopOrders MANDATORY (no Enqueue)
- Jane Street Intel: `docs/intel/jane-street/` (microsecond latency requirements)