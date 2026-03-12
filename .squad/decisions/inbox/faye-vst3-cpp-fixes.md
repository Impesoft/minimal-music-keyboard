# VST3 C++ Bridge Bug Fixes

**Date:** 2026-03-19  
**Agent:** Faye (Audio Dev)  
**Status:** Implemented

## Summary

Fixed four critical bugs in the VST3 bridge C++ code (`src/mmk-vst3-bridge/`) identified during VST3 pipeline audit.

## Bugs Fixed

### 1. Sample Rate and Block Size Mismatch

**Problem:** C++ bridge used 44,100 Hz / 256 samples while C# host expects 48,000 Hz / 960 samples.

**Impact:**
- Plugins rendered at wrong pitch/speed
- Only 256 of 960 required samples rendered per frame (last 704 were silence)
- Render thread fired ~3.5x too often

**Fix:** Updated `audio_renderer.h` constants:
```cpp
static constexpr int kSampleRate    = 48'000;  // was 44'100
static constexpr int kMaxBlockSize  = 960;     // was 256
```

All dependent code (buffer arrays, EventList, frameDuration calculation) automatically uses the new values via the constants.

### 2. Missing IHostApplication

**Problem:** `component_->initialize(nullptr)` violated VST3 spec. Some plugins silently accept null, but others fail to load.

**Fix:** Created `src/mmk-vst3-bridge/src/host_application.h` with minimal `IHostApplication` stub:
- Implements `getName()` returning "Minimal Music Keyboard"
- Implements `queryInterface()` / `addRef()` / `release()` for VST3 COM model
- Uses `std::atomic<uint32>` for thread-safe reference counting

Updated `audio_renderer.cpp` to pass `HostApplication` instance to both `component_->initialize()` and `controller_->initialize()`.

### 3. Missing IEditController Initialization

**Problem:** `IEditController` was never queried or initialized. This caused:
- Plugins with separate controller cannot show GUI (Tier 4 requirement)
- Plugins requiring controller initialization for default state have wrong state

**Fix:**
- Added `controller_` member to `AudioRenderer` class
- Query `IEditController` from component after initialization
- Initialize controller with `HostApplication` instance
- Connect component and controller via `IConnectionPoint` if both support it
- Properly terminate controller in `ResetPluginState()`

### 4. QueueSetProgram Used Wrong Event Type

**Problem:** Used `kLegacyMIDICCOutEvent` (output event from plugin to host) as an input event. VST3 plugins silently ignore this.

**Fix:** Changed to `kDataEvent` with raw MIDI program change message:
```cpp
evt.type = Event::kDataEvent;
evt.data.type = DataEvent::kMidiSysEx;
// Raw MIDI: [0xC0 | channel, program]
evt.data.bytes = midiBytes;
evt.data.size = 2;
```

This is the standard VST3 approach for MIDI program change on most instrument plugins.

## Files Modified

- `src/mmk-vst3-bridge/src/audio_renderer.h` — Updated constants, added `controller_` member
- `src/mmk-vst3-bridge/src/audio_renderer.cpp` — HostApplication usage, IEditController init, fixed QueueSetProgram
- `src/mmk-vst3-bridge/src/host_application.h` — **NEW** minimal IHostApplication stub

## Testing Required (by Jet)

After C++ build succeeds:
1. Load a VST3 instrument plugin
2. Verify audio plays at correct pitch (48 kHz)
3. Verify full 960 samples rendered per frame (no silence padding)
4. Send MIDI program change and verify instrument switches
5. Test with plugins that require `IHostApplication` (e.g., Native Instruments, Arturia)

## Notes

- No CMakeLists.txt changes required (`host_application.h` is header-only)
- C# side fixes being handled by Jet in parallel
- VST3 SDK must be cloned to `extern/vst3sdk` before C++ build (per existing README)
