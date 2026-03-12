# Decision: Preserve exact VST3 editor diagnostics in managed UI

**Author:** Jet (Windows Dev)  
**Date:** 2026-03-12  
**Status:** IMPLEMENTED

## Context

When a VST3 plugin reported editor-discovery or editor-open failures, the WinUI settings page could still fall back to a generic "Editor Not Available" message. This happened because the UI only trusted `GetActiveBackend()`, while VST3 loading intentionally keeps the previous backend active until the bridge is ready.

## Decision

Expose the latest VST3 editor availability diagnostic through `IAudioEngine`, and have the settings UI use that value for both inline status text and editor-unavailable dialogs.

## Why

1. The bridge already computes the exact reason (`editorDiagnostics`, bridge faults, open-editor ACK errors).
2. Managed/UI code should not replace that with a generic fallback just because the active backend is still SoundFont.
3. The editor button must restore its enabled state based on current capability, not unconditionally in a `finally` block.

## Implementation

- Added `IAudioEngine.GetVst3EditorAvailabilityDescription()`
- Implemented it in `AudioEngine` by forwarding `Vst3BridgeBackend.EditorAvailabilityDescription`
- Updated `SettingsWindow` to:
  - show the stored VST3 diagnostic after load success with no editor support
  - show the stored VST3 diagnostic in the "Editor Not Available" dialog
  - keep the explicit "still loading" message keyed off `_loadingVst3SlotIndex`
  - stop unconditionally re-enabling the editor button after failures

## Consequences

- OB-Xd-style failures now surface the exact managed bridge diagnostic instead of a generic fallback.
- The UI remains accurate even while the previous backend is still active during VST3 load transitions.
