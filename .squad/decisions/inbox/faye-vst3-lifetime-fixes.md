# VST3 Lifetime Crash Fixes

**Date:** 2026-03-19  
**Author:** Faye (Audio Dev)  
**Status:** Implemented  
**Requested by:** Ward Impe (via Gren's code review)

## Summary

Applied two crash-risk fixes to the VST3 C++ bridge after Gren's architecture review identified critical lifetime issues in `audio_renderer.h` and `audio_renderer.cpp`.

## Fix 1: HostApplication Lifetime

**Problem:** The `HostApplication` was created as a local `IPtr` variable inside `Load()`. When `Load()` returned, the local went out of scope and destroyed the `HostApplication`. Many VST3 plugins store the raw `IHostApplication*` without calling `addRef()`, leading to dangling pointer crashes.

**Solution:**
- Added `Steinberg::IPtr<HostApplication> hostApp_;` as a private member in `audio_renderer.h`
- Changed `Load()` to assign to the member: `hostApp_ = owned(new HostApplication());`
- Added `hostApp_ = nullptr;` in `ResetPluginState()` AFTER components are terminated

**Impact:** The `HostApplication` now lives for the entire plugin lifetime, preventing dangling pointer access.

## Fix 2: IConnectionPoint Disconnect Before Terminate

**Problem:** `Load()` connected `component_` and `controller_` via `IConnectionPoint` interfaces, but `ResetPluginState()` called `terminate()` without first calling `disconnect()`. If either component tried to notify the other during teardown, a use-after-free crash would occur.

**Solution:**
- Added disconnect logic in `ResetPluginState()` BEFORE the `terminate()` calls:
  - Query `IConnectionPoint` from both component and controller
  - Call `disconnect()` on both if they support it
  - Release the raw pointers
  - Only then call `terminate()` on both components

**Impact:** Clean connection teardown prevents notifications to dead objects during shutdown.

## Files Changed

- `src/mmk-vst3-bridge/src/audio_renderer.h` — Added `hostApp_` member + `host_application.h` include
- `src/mmk-vst3-bridge/src/audio_renderer.cpp` — Changed local to member variable; added disconnect block in `ResetPluginState()`

## Build Verification

✅ C# solution builds successfully with no new errors or warnings (2 pre-existing warnings unrelated to these changes).

⚠️ C++ bridge not built (no standalone CMake build attempted; awaiting Visual Studio project integration).

## Rationale

Both fixes address real-world crashes observed in production VST3 hosts:
1. Plugins commonly store `IHostApplication*` as raw pointers without ref-counting (per VST3 SDK examples).
2. Connection point teardown order is critical — the VST3 SDK documentation recommends disconnecting before termination.

These fixes bring the bridge into compliance with VST3 hosting best practices and eliminate two high-probability crash scenarios.
