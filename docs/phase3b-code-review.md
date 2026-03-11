# Phase 3b Code Review: VST3 SDK Integration in mmk-vst3-bridge

**Date:** 2026-03-12
**Reviewer:** Gren
**Author:** Faye

## Phase 3b Review: APPROVED ✅

The VST3 SDK integration is correct, safe, and compatible with the approved Phase 2 managed side and Phase 3 scaffold. All 20 review criteria pass. The implementation properly loads VST3 modules, initializes the component/processor lifecycle, routes MIDI events through the SDK's EventList, and renders stereo float32 audio into the MMF ring buffer. Resource management uses `IPtr<T>` and `Module::Ptr` smart pointers throughout, the shutdown sequence follows the correct `setProcessing(false)` → `setActive(false)` → `terminate()` order, and the bridge degrades gracefully to silence when no plugin is loaded.

## Files Reviewed

- `src/mmk-vst3-bridge/CMakeLists.txt`
- `src/mmk-vst3-bridge/src/audio_renderer.h`
- `src/mmk-vst3-bridge/src/audio_renderer.cpp`
- `src/mmk-vst3-bridge/src/bridge.h` / `bridge.cpp`
- `src/mmk-vst3-bridge/src/mmf_writer.h` / `mmf_writer.cpp` (cross-referenced)
- `src/mmk-vst3-bridge/README.md`
- `src/MinimalMusicKeyboard/Services/Vst3BridgeBackend.cs` (wire-format cross-reference)
- `.squad/decisions/inbox/faye-phase3b-vst3-sdk.md`

## Checklist Results

### VST3 Correctness ✅

| # | Criterion | Result |
|---|-----------|--------|
| 1 | `VST3::Hosting::Module::create()` with path + error string | ✅ `audio_renderer.cpp:40` — `Module::create(pluginPath, loadError)` |
| 2 | `IPluginFactory` scanned for `kVstAudioEffectClass` | ✅ `audio_renderer.cpp:47–55` — `module_->getFactory()`, `classInfos()`, `find_if` on category |
| 3 | `IComponent` + `IAudioProcessor` init sequence | ✅ `audio_renderer.cpp:64–109` — `createInstance`, `initialize(nullptr)`, `setupProcessing`, `setActive(true)`, `setProcessing(true)` |
| 4 | `ProcessSetup` fields correct | ✅ `audio_renderer.cpp:89–93` — kRealtime, kSample32, 44100.0, 256 |
| 5 | `ProcessData` built correctly | ✅ `audio_renderer.cpp:327–338` — numInputs=0, numOutputs=1, outputBus.numChannels=2 |
| 6 | MIDI events via `IEventList` | ✅ `audio_renderer.cpp:311–334` — `Steinberg::Vst::EventList`, set to `processData.inputEvents` |
| 7 | NoteOn: kNoteOnEvent, velocity 0–127 → 0.0–1.0 | ✅ `audio_renderer.cpp:199,202` — `velocity / 127.0f` clamped |
| 8 | NoteOff: kNoteOffEvent | ✅ `audio_renderer.cpp:219` |
| 9 | NoteOffAll: queues NoteOff for all 128 pitches | ✅ `audio_renderer.cpp:236–249` — loop 0..127 |
| 10 | Stereo interleaved into MMF | ✅ `audio_renderer.cpp:344–348` — `output[i*2]=left[i], output[i*2+1]=right[i]` |

### Thread Safety ✅

| # | Criterion | Result |
|---|-----------|--------|
| 11 | Event queue synchronized | ✅ `eventsMutex_` guards all `pendingEvents_` access; `pluginMutex_` guards plugin state. Lock order always `pluginMutex_` → `eventsMutex_` — no deadlock risk |
| 12 | No `std::exception` thrown in render path | ✅ `FillBuffer`: empty vector default-construct + swap (no alloc), `thread_local EventList` pre-allocated, stack-allocated `std::array` buffers, SDK `process()` is noexcept by convention |

### Resource Management ✅

| # | Criterion | Result |
|---|-----------|--------|
| 13 | COM references properly managed | ✅ `IPtr<IComponent>`, `IPtr<IAudioProcessor>`, `Module::Ptr` — all smart pointers. `Steinberg::owned()` for QI result (`audio_renderer.cpp:87`) |
| 14 | Shutdown order correct | ✅ `ResetPluginState()` at `audio_renderer.cpp:146–166`: `setProcessing(false)` → `setActive(false)` → `terminate()` → release pointers → reset module |
| 15 | Module held alive while plugin in use | ✅ `module_` is a member `Module::Ptr`, released only in `ResetPluginState()` after component/processor cleanup |

### Build System ✅

| # | Criterion | Result |
|---|-----------|--------|
| 16 | `CMakeLists.txt` references `extern/vst3sdk` | ✅ Line 7: `VST3_SDK_ROOT` = `${CMAKE_SOURCE_DIR}/../../../extern/vst3sdk` as `CACHE PATH` |
| 17 | Links VST3 SDK targets | ✅ Line 23: `sdk_hosting pluginterfaces` — `base` and `public.sdk` linked transitively |
| 18 | No hardcoded absolute paths | ✅ All paths relative to `CMAKE_SOURCE_DIR` or overridable via CACHE |

### Graceful Degradation ✅

| # | Criterion | Result |
|---|-----------|--------|
| 19 | Plugin load failure → no crash | ✅ `Load()` returns `false` + error string; `bridge.cpp:67–74` sends error ack over IPC |
| 20 | `FillBuffer` → silence when no plugin | ✅ `audio_renderer.cpp:295` fills zeros first; line 298 returns early if `!processor_` |

### Wire-Protocol Compatibility ✅

The `"ack": "load_ack"` value (`bridge.cpp:69`) is accepted by the managed side's `ParseLoadAck` (`Vst3BridgeBackend.cs:501`), which checks for both `"load"` and `"load_ack"`. No wire-protocol break.

## Notes (non-blocking)

### NOTE 1 — `initialize(nullptr)` passes null host context

`audio_renderer.cpp:72` — `component_->initialize(nullptr)`. The VST3 `IPluginBase::initialize` expects an `IHostApplication` context. Many plugins tolerate nullptr, but some (particularly those querying host name or capabilities) may behave unexpectedly. For production hardening, provide a minimal `IHostApplication` implementation. Not a correctness issue for the bridge's current scope.

### NOTE 2 — `std::mutex` on the render thread

Both `pluginMutex_` and `eventsMutex_` are `std::mutex` (blocking locks) acquired in `FillBuffer`, which runs on the render thread. Priority inversion is possible if the MIDI-sending thread holds `eventsMutex_` while the render thread is blocked. For this bridge's workload (256-sample blocks, low-frequency MIDI), contention is negligible. For production latency targets, replace the event queue with a lock-free SPSC ring buffer.

### NOTE 3 — `frameSize_` vs `kMaxBlockSize` alignment assumption

`RenderLoop` allocates `frameSize_ * 2` floats and calls `FillBuffer(frameSize_)`, but the VST3 `process()` always renders exactly `kMaxBlockSize` (256) samples. If `frameSize_ != kMaxBlockSize`, audio is either truncated or zero-padded. Currently aligned because the host's MMF frame size is 256. Adding an assertion `frameSize_ == kMaxBlockSize` or dynamically matching `numSamples` to `frameSize_` would make this robust against future changes.

### NOTE 4 — Preset load failure is silent on stderr

`audio_renderer.cpp:127–133` — Failed `.vstpreset` loads are logged to `std::cerr` but not reported in the IPC `load_ack`. The `Load()` method still returns `true` (plugin loaded, preset failed). This is the correct non-fatal behavior per the decision doc. Consider adding a `"warning"` field to the ack for UI feedback in a future iteration.

## Verdict: ✅ APPROVED

All 20 review criteria pass. The VST3 SDK integration correctly implements the full plugin hosting lifecycle — module loading, factory scanning, component initialization, real-time audio processing, MIDI event routing, and orderly shutdown. Resource management via smart pointers is consistent and leak-free. Thread safety is achieved through consistent mutex lock ordering. Graceful degradation to silence on load failure prevents crashes. The build system correctly references the external SDK with no absolute paths. Four non-blocking notes filed for future hardening; none affect correctness of the current implementation.
