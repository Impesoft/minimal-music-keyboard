# Decision: Phase 2 Code Review Verdict

**Date:** 2026-03-11
**Author:** Gren
**Type:** Code Review
**Status:** REJECTED

## Summary

Phase 2 (`Vst3BridgeBackend.cs` + `BridgeFaultedEventArgs.cs`) by Faye is **rejected** with 1 blocking + 2 required issues.

## Issues

1. **🔴 BLOCKING: IPC resource ownership reversed** — Implementation has bridge-as-server with bridge PID in names. Spec requires host-as-server with host PID. This is a deliberate architectural decision for crash recovery that cannot be deferred.

2. **🟡 REQUIRED: String allocation on audio thread** — NoteOn/NoteOff/SetProgram use string interpolation (allocates). Fix: struct-based Channel, serialize in writer task.

3. **🟡 REQUIRED: Dispose race** — Cancel() + Kill() preempts shutdown command. Fix: wait for writer drain before kill.

## Action

- **Fixes assigned to:** Jet (Faye locked out)
- **Phase 3 may NOT begin** until re-review passes
- Full review: `docs/phase2-code-review.md`
