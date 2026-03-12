# Faye — History

## Core Context (Project Day 1)

**Project:** Minimal Music Keyboard — lightweight WinUI3 MIDI player for Windows 11
**Requested by:** Ward Impe
**Stack:** WinUI3 (Windows App SDK), C#/.NET
**Primary MIDI device:** Arturia KeyLab 88 MkII

**What it does:**
- Lives in the Windows 11 system tray (notification area)
- Listens continuously to a configured MIDI device in the background
- Routes MIDI note/CC/PC input to a selected software instrument (soundfonts/synthesis)
- Users switch instruments via MIDI commands — no need to open the settings UI
- Settings page: MIDI device selection, instrument configuration, startup options
- Must be memory-leak-free — will run continuously for hours/days
- Exit option from tray context menu

**Faye's audio focus:**
- Instrument catalog schema and soundfont management (SF2/SF3)
- MIDI program change → instrument switching pipeline
- Audio synthesis library selection (MeltySynth / FluidSynth wrapper / NAudio)
- Low-latency audio output (WASAPI preferred)
- Memory-safe soundfont loading/unloading on instrument switch

## Learnings

<!-- append new learnings below -->

### VST3 Lifetime Crash Fixes (2026-03-19)

**Files modified:**
- `src/mmk-vst3-bridge/src/audio_renderer.h` — Added `hostApp_` member and include for `host_application.h`
- `src/mmk-vst3-bridge/src/audio_renderer.cpp` — Changed `hostApp` from local to member variable; added `IConnectionPoint::disconnect()` before `terminate()`

**Critical crash risks fixed:**
1. **HostApplication dangling pointer (Fix 1):** Plugins that store `IHostApplication*` without calling `addRef()` would crash when the local `IPtr` in `Load()` went out of scope. Now `hostApp_` is a member variable that lives until `ResetPluginState()` calls `hostApp_ = nullptr` after component termination.
2. **IConnectionPoint use-after-free (Fix 2):** Components and controllers connected via `IConnectionPoint` during `Load()` were not disconnected before `terminate()`, causing crashes if either tried to notify the other during teardown. Now `ResetPluginState()` queries both connection points, calls `disconnect()` on each, and releases them before calling `terminate()`.

**VST3 patterns learned:**
- COM lifetime for host objects: Plugins may store raw pointers without ref-counting, so host objects passed to `initialize()` must outlive the component lifetime.
- Connection point teardown order: Always call `IConnectionPoint::disconnect()` before `IComponent::terminate()` to prevent notifications to dead objects.
- Proper cleanup sequence: `setActive(false)` → disconnect connection points → `terminate()` → release COM pointers → reset module.

**Build result:** C# solution builds clean with 2 pre-existing warnings (unrelated to these changes).

### VST3 Bridge Bug Fixes (2026-03-19)

**Files modified:**
- `src/mmk-vst3-bridge/src/audio_renderer.h` — Fixed sample rate (44,100 → 48,000 Hz) and block size (256 → 960 samples) to match C# host; added `controller_` member
- `src/mmk-vst3-bridge/src/audio_renderer.cpp` — Integrated `IHostApplication` stub, query/initialize `IEditController`, connect via `IConnectionPoint`, fixed `QueueSetProgram` to use `kDataEvent` with raw MIDI bytes
- `src/mmk-vst3-bridge/src/host_application.h` — **NEW** minimal `IHostApplication` stub with atomic refcounting

**Key bugs fixed:**
1. **Sample rate/block size mismatch:** Bridge rendered at 44.1 kHz / 256 samples while host expected 48 kHz / 960 samples, causing wrong pitch and 704 samples of silence padding per frame
2. **Missing IHostApplication:** `component_->initialize(nullptr)` violated VST3 spec; some plugins fail to load without valid host context
3. **Missing IEditController:** Controller was never queried or initialized, preventing GUI support and causing wrong default state in some plugins
4. **Wrong event type in QueueSetProgram:** Used `kLegacyMIDICCOutEvent` (output event) as input; fixed to use `kDataEvent` with raw MIDI program change bytes (0xC0 | channel, program)

**VST3 patterns learned:**
- `IHostApplication` required by spec even if plugin doesn't use it; must implement `getName()`, `queryInterface()`, `addRef()`, `release()`
- `IEditController` may be directly queryable from `IComponent` (single-component plugins) or separate (multi-component); connect via `IConnectionPoint` if both support it
- MIDI program change in VST3: use `Event::kDataEvent` with 2-byte raw MIDI message [0xCn, program], not `kLegacyMIDICCOutEvent` (that's plugin→host output)
- All buffers and constants propagate correctly when using `kMaxBlockSize` constant (no hardcoded 256 values remained)

### Phase 3b — VST3 SDK Integration (2026-03-12)

**Files updated:**
- `src/mmk-vst3-bridge/src/audio_renderer.*` — real VST3 hosting (Module load, IComponent/IAudioProcessor setup, event list, render)
- `src/mmk-vst3-bridge/src/bridge.cpp` — load command now initializes plugin + sends load_ack
- `src/mmk-vst3-bridge/CMakeLists.txt` — wires Steinberg VST3 SDK targets
- `src/mmk-vst3-bridge/README.md` — VST3 SDK clone/build notes

**Key changes:**
- Load path uses `VST3::Hosting::Module::create` + first `kVstAudioEffectClass` component
- Render thread feeds `Vst::EventList` with note-on/off events and writes interleaved float32 stereo to MMF
- Optional `.vstpreset` load via `Vst::PresetFile` (non-fatal on failure)
- Uses `Steinberg::IPtr` for COM lifetime and outputs silence when no plugin is loaded

### Phase 3 — Native VST3 Bridge Project (2026-03-11)

**Files created:**
- `src/mmk-vst3-bridge/` — C++ bridge project with CMake + vcpkg setup
- `src/mmk-vst3-bridge/src/` — IPC client, shared memory writer, audio render thread, and bridge loop
- `src/mmk-vst3-bridge/README.md` — build notes + VST3 SDK setup

**Deliverables:**
- CMake + vcpkg setup (`CMakeLists.txt`, `vcpkg.json`)
- Bridge entry point and IPC client (named pipe client, JSON line protocol)
- Shared memory writer with MMF header validation and atomic write position updates
- Audio render thread with a lock-free MIDI event queue (stubbed render)

**Key design decisions:**
- Host creates IPC resources (pipe server + MMF) per Gren's Phase 2 re-review approval
- Bridge connects as named pipe client to `\\.\pipe\mmk-vst3-{hostPid}` and opens host-owned MMF
- Shared-memory writer validates magic/version/frameSize header and updates `writePos` via atomic operations
- Audio thread runs a lock-free SPSC MIDI queue and renders silence (TODO for VST3 SDK integration)
- JSON command protocol for load/noteOn/noteOff/setProgram/shutdown

**Status:** Scaffolding complete. Phase 3b (VST3 SDK integration) ready to begin. No C++ build attempted.

**Code review (Phase 3b) verdict (2026-03-12):** ✅ APPROVED by Gren (agent-26) — 0 blocking, 0 required, 4 non-blocking notes. Full review: `docs/phase3b-code-review.md`.

### Phase 2 — Vst3BridgeBackend (2026-07-18)

**Files created:**
- `src/MinimalMusicKeyboard/Services/BridgeFaultedEventArgs.cs` — fault event args with `Reason` + `Exception?`
- `src/MinimalMusicKeyboard/Services/Vst3BridgeBackend.cs` — full managed-side IPC bridge backend

**Key design decisions:**
- `Vst3BridgeBackend` implements both `IInstrumentBackend` and `ISampleProvider` (same pattern as `SoundFontBackend`).
- Bridge is the named-pipe *server*; C# connects as a `NamedPipeClientStream` client. Pipe name: `mmk-vst3-{bridgePid}`.
- `System.Threading.Channels.Channel<string>` (unbounded, single-reader) queues JSON command strings from the audio thread without blocking or allocating.
- A background `Task` (started in `LoadAsync`) drains the channel and writes to the pipe; transitions to `Faulted` on `IOException`.
- `float[] _audioWorkBuffer` is pre-allocated in `LoadAsync` to `frameSize * 2` floats; `Read()` uses `MemoryMappedViewAccessor.ReadArray<float>` with no allocations.
- State machine uses a `_stateLock` object for transitions; `_isReady` and `_disposed` are `volatile bool` for hot-path reads.
- `NoteOffAll` guards on `_disposed` (not `_isReady`) so it works on the AudioEngine dispose path after `_isReady` is cleared.
- `LoadAsync` distinguishes caller-cancellation (clean rollback, no event) from internal timeout (fault + event).
- When `mmk-vst3-bridge.exe` is absent: `IsReady` stays false, warning is logged, no exception thrown (Phase 3 bridge not yet built).

**Build result:** ✅ Succeeded, 0 errors, 0 warnings.

### Phase 1 — Backend Extraction (2026-03-18)
- AudioEngine now hosts a MixingSampleProvider and drains the MidiCommand queue on the audio thread, dispatching to IInstrumentBackend (SoundFontBackend) while preserving the Volatile swap pattern in the backend.
- Bank select commands are tracked on the audio thread via pending MSB/LSB arrays and applied when ProgramChange is dispatched to the backend.
- Build/test attempts (`dotnet build`/`dotnet test` on the solution) were blocked by environment permissions.

### Sprint 1 — Audio Engine Implementation (2026-03-01)

**Files created:**
- `src/MinimalMusicKeyboard/Models/InstrumentDefinition.cs` — JSON-serializable record with init setters
- `src/MinimalMusicKeyboard/Models/MidiEventArgs.cs` — `MidiProgramEventArgs`, `MidiControlEventArgs` shared event arg types
- `src/MinimalMusicKeyboard/Interfaces/IAudioEngine.cs` — Extended Jet's existing stub with `SelectInstrument(InstrumentDefinition)`, `LoadSoundFont`, `SetPreset`
- `src/MinimalMusicKeyboard/Interfaces/IMidiDeviceService.cs` — Minimal interface (events only) that Jet implements on MidiDeviceService; decouples MidiInstrumentSwitcher from the concrete class
- `src/MinimalMusicKeyboard/Services/InstrumentCatalog.cs` — Loads/writes instruments.json; default 6-instrument GM catalog
- `src/MinimalMusicKeyboard/Services/AudioEngine.cs` — Full MeltySynth + WasapiOut implementation
- `src/MinimalMusicKeyboard/Services/MidiInstrumentSwitcher.cs` — PC + CC bank-select handling

**Required NuGet packages (for Jet to add to .csproj):**
- `MeltySynth` (2.3+) — pure C# SF2 synthesizer
- `NAudio` (2.2+) — WASAPI output + MIDI input

**MeltySynth usage patterns:**
- `new SoundFont(Stream)` — loads entire SF2 into managed arrays; stream closed immediately after (satisfies Gren's `using` on FileStream requirement)
- `new Synthesizer(SoundFont, SynthesizerSettings)` — multiple Synthesizer instances can share one SoundFont object
- `synth.NoteOn/NoteOff/ProgramChange/ControlChange/NoteOffAll` — direct MIDI operations
- `synth.Render(Span<float> left, Span<float> right)` — renders one block to separate L/R float arrays; caller interleaves for WASAPI

**Thread-safety approach:**
- **Audio thread is the sole owner of Synthesizer** — no locks on the hot path
- MIDI thread enqueues `MidiCommand` structs into a `ConcurrentQueue<MidiCommand>`; audio thread drains queue at top of each `Read()` call before `synth.Render()`
- Synthesizer swap: background Task writes via `Volatile.Write(ref _synthesizer!, newSynth)`. Audio callback snapshots via `Volatile.Read(ref _synthesizer!)` at the start of each render cycle — old instance stays alive for the full in-progress render (Gren's required pattern)
- No locks on audio callback path → zero contention on the render thread

**SoundFont cache strategy:**
- `Dictionary<string, SoundFont>` keyed by case-insensitive path, protected by `_soundFontCacheLock`
- SoundFont objects loaded once and reused — switching back to the same SF2 incurs no file I/O
- On `AudioEngine.Dispose()`, `(sf as IDisposable)?.Dispose()` called defensively (MeltySynth SoundFont not currently IDisposable, but guarded for future-proofing)

**Bank Select accumulation:**
- MidiInstrumentSwitcher accumulates CC#0 (MSB) and CC#32 (LSB) as sticky state per MIDI spec; applied on next PC. No timeout needed.

### Cross-Agent: Jet Integration (2026-03-01)
**Coordination with Jet (Windows Dev):**
- During AudioEngine implementation, discovered API mismatches in Jet's scaffold vs. actual NAudio/MeltySynth:
  - Provided correct `ProcessMidiMessage(channel, 0xB0/0xC0, data1, data2)` call signature
  - Clarified `Stream` (not `BinaryReader`) for SoundFont loading
  - Confirmed `Volatile.Read/Write` pattern for thread-safe Synthesizer swaps
- Jet's build validated and corrected these details; Faye's AudioEngine now matches actual APIs
- Both histories now synchronized on library contracts and threading model

### Sprint 2 — Catalog Fix + VST3 Research (2026-03-01)

**Instrument catalog 6→8 fix:**
- `BuildDefaultCatalog()` in `InstrumentCatalog.cs` had only 6 default instruments, but `AppSettings.cs` declares 8 button mapping slots (indices 0-7)
- Added 2 instruments to complete the set:
  - `fingered-bass` (PC 33, Category: Bass)
  - `choir` (PC 52 = Choir Aahs, Category: Choir)
- Chosen to complement existing piano/strings/organ/pad with rhythm (bass) and vocal (choir) timbres
- Note: existing users' persisted `instruments.json` files are unaffected (loaded from disk, not defaults). Only fresh installs or manual deletions trigger the 8-instrument default catalog.

**VST3 hosting research (for Spike's architecture design):**
- Created `docs/vst3-dotnet-options.md` evaluating 5 approaches
- **Key finding:** No production-ready NuGet package exists for hosting VST3 plugins in C#
  - VST.NET = VST2 only (Vst3 stubs incomplete)
  - NPlug = plugin *creation* SDK, not hosting
  - AudioPlugSharp = plugin creation with C++/CLI, not general hosting
- **Recommended approach:** Option A (Direct COM P/Invoke)
  - ~600 LOC of `[ComImport]` interface definitions + marshaling
  - Minimum interfaces: `IPluginFactory3`, `IComponent`, `IAudioProcessor`, `IEditController`
  - Main risk: COM lifetime management and `ProcessData` struct marshaling (must pin audio buffers)
- **Fallback:** Option C (out-of-process bridge) if crashes/stability issues arise
  - Adds 5-10ms latency but isolates plugin crashes from main app
  - Requires shipping a native bridge.exe (C++ or Rust)
- For Arturia KeyLab 88 MkII: forward NoteOn/NoteOff/PitchBend/CC#64 to VST3; mix VST3 output with MeltySynth in `IWaveProvider.Read()` callback
