# Faye: OB-Xd VST3 host-side compatibility fix

**Date:** 2026-03-12  
**Status:** IMPLEMENTED  
**Requested by:** Ward Impe

## Decision

Treat missing controller state synchronization as the most likely remaining host-side compatibility gap for `OB-Xd 3.vst3`, and fix that in the native bridge before chasing lower-signal host features.

## Why

- The bridge already provides a real `IHostApplication`, separate-controller discovery, controller initialization, connection-point wiring, bus activation, and editor diagnostics.
- What it still lacked was the standard VST3 host step for split component/controller plugins: after `IEditController::initialize()`, copy component state into the controller with `setComponentState()`.
- Plugins that rely on that synchronization can start with mismatched controller state even though they load in other hosts that implement the full pattern.

## Implementation

- Added component→controller state sync in `src/mmk-vst3-bridge/src/audio_renderer.cpp` using `Steinberg::MemoryStream`.
- Re-synchronized controller state after successful `.vstpreset` loads so the editor/controller reflects the preset-applied component state.
- Added `public.sdk/source/common/memorystream.cpp` to `src/mmk-vst3-bridge/CMakeLists.txt` so the native bridge links successfully.

## Verification

- Native bridge Release build: succeeded
- Managed app Release build: succeeded (2 pre-existing warnings only)
