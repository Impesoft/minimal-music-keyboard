# Session Log: VST3 Load Race Condition Fix

**Timestamp:** 2026-03-12T09:10:43Z  
**Topic:** VST3 Backend Load Race Condition  
**Agents:** Faye (diagnosis), Jet (implementation)  
**Status:** Complete

## Summary

Fixed critical race condition in VST3 backend loading where `_activeBackend` was assigned before async `LoadAsync()` completed. When user clicked "Open Editor" during the loading window, the backend showed unavailable despite being present and functional.

## Fixes Implemented

1. **Deferred Backend Assignment:** `Volatile.Write(_activeBackend, _vst3Backend)` now executes only after `LoadAsync()` completes and `IsReady == true`
2. **Missing Bridge Exe Handling:** Now fires `BridgeFaulted` event instead of silent return
3. **Load Failure Surfacing:** New `InstrumentLoadFailed` event propagates errors to UI
4. **Button Feedback:** Editor button shows "VST3 Plugin Still Loading" during load phase

## Build Status
✓ 0 errors

## Decision Records
- `.squad/decisions/inbox/faye-vst3-editor-race-condition.md` → merged to decisions.md
- `.squad/decisions/inbox/jet-vst3-race-fix.md` → merged to decisions.md
