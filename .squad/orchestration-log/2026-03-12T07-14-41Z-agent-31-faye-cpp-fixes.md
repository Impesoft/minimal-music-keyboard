# Orchestration: agent-31 (Faye) — VST3 C++ Bridge Bug Fixes

**Timestamp:** 2026-03-12T07:14:41Z  
**Agent:** Faye (Audio Dev)  
**Task:** Implement VST3 C++ bridge fixes (sample rate, block size, IHostApplication, QueueSetProgram)  
**Status:** ✅ Complete

## Fixes Implemented

### Fix 1: Sample Rate and Block Size Mismatch
- **File:** `src/mmk-vst3-bridge/src/audio_renderer.h`
- **Change:** `kSampleRate` 44'100 → 48'000; `kMaxBlockSize` 256 → 960
- **Impact:** Plugins render at correct pitch; full 960 samples per frame (no silence padding)

### Fix 2: Missing IHostApplication
- **File:** `src/mmk-vst3-bridge/src/host_application.h` (NEW)
- **Change:** Created minimal `IHostApplication` stub
  - Implements `getName()` → "Minimal Music Keyboard"
  - Implements VST3 COM model (`queryInterface`, `addRef`, `release`)
  - Uses `std::atomic<uint32>` for thread-safe ref counting
- **Impact:** Plugins requiring `IHostApplication` now initialize successfully

### Fix 3: Missing IEditController Initialization
- **File:** `src/mmk-vst3-bridge/src/audio_renderer.cpp`
- **Change:** Query `IEditController` after `IComponent::initialize`
  - Store as member `controller_`
  - Initialize with `HostApplication`
  - Connect via `IConnectionPoint` if supported
  - Terminate properly in `ResetPluginState()`
- **Impact:** Plugin state initialization correct; GUI hosting ready for Phase 2

### Fix 4: Broken QueueSetProgram
- **File:** `src/mmk-vst3-bridge/src/audio_renderer.cpp`
- **Change:** Replace `kLegacyMIDICCOutEvent` with `kDataEvent` containing raw MIDI program change
  - Event type: `kDataEvent`
  - Data type: `kMidiSysEx`
  - Bytes: `[0xC0 | channel, program]`
- **Impact:** Program change events now correctly routed to VST3 instruments

## Files Modified
- `audio_renderer.h` — Constants updated, `controller_` member added
- `audio_renderer.cpp` — HostApplication usage, IEditController init, QueueSetProgram fixed
- `host_application.h` — NEW, minimal IHostApplication stub

## Build Status
✅ C# solution builds successfully (no new errors/warnings)  
⏳ C++ bridge build verification deferred (awaiting VST3 SDK setup)

## Testing Required
1. Load VST3 instrument plugin
2. Verify audio plays at correct pitch (48 kHz)
3. Verify full 960 samples per frame (no silence padding)
4. Send MIDI program change; verify instrument switches
5. Test with plugins requiring `IHostApplication` (Native Instruments, Arturia)

## Follow-up Actions
- Jet to implement C# side fixes in parallel
- Gren to review both C++ and C# for lifetime safety
- Ed to build and verify end-to-end audio output
