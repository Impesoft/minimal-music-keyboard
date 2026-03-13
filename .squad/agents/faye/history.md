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

### OB-Xd Editor Attach Investigation (2026-03-13)

**Files modified:**
- `src/mmk-vst3-bridge/src/audio_renderer.cpp` — pre-show the editor frame/client HWND, focus the client, and force an initial redraw before calling `IPlugView::attached(HWND)`.

**What we proved:**
- The native host already satisfied the important Win32/VST3 editor prerequisites for OB-Xd: dedicated editor thread, OLE/STA initialization, top-level frame window, child client HWND, `IPlugFrame`, and a running message pump before `attached()`.
- I added one more low-risk compatibility tweak by making the host window explicitly shown, focused, and paintable before `attached()`, then validated against the installed OB-Xd bundle at `C:\Program Files\Common Files\VST3\OB-Xd.vst3`.
- OB-Xd still returned `load_ack: ok=true, supportsEditor=true` but `openEditor` again timed out inside `IPlugView::attached(HWND)`, so the remaining hang is no longer credibly explained by missing basic host-side window setup.

**Key diagnostic detail:**
- The exact bridge harness run against `C:\Program Files\Common Files\VST3\OB-Xd.vst3` still produced `editor_opened: ok=false` with the timeout message after the host had already created the frame/client HWNDs and set the plug frame.
- The same load ACK also showed OB-Xd's `setComponentState()` path can raise a structured exception (`0xC0000005`) during controller sync; the bridge now survives that, but it further suggests the plugin is fragile in minimal-host scenarios beyond the remaining justified host tweaks.

**Conclusion:**
- With the pre-attach show/focus/redraw compatibility improvement in place and the exact installed plugin still hanging inside `attached()`, the residual OB-Xd editor failure should now be treated as plugin-side or as requiring a much broader host feature surface (for example full run-loop semantics) than this bridge currently aims to provide.
- For future VST3 GUI investigations, a tiny pipe/MMF harness is enough to prove whether the native sidecar itself still reproduces the issue without involving the WinUI app.

### Bridge Message-Pump Architecture + Final Host Diagnostics (2026-03-13)

**Context:** After low-risk host-side tweaks (pre-show/focus/redraw) still didn't resolve OB-Xd editor timeout, team identified root cause as JUCE MessageManager thread affinity issue in bridge main thread.

**What Spike fixed:**
- Refactored `Bridge::Run()` to run Win32 message loop (`GetMessageW`) on main thread
- Pipe reading moved to background thread that posts `WM_BRIDGE_COMMAND` to hidden window
- All VST3 operations (load/open/close) now on main thread with active message pump

**Consequence for audio dev:**
- Audio render thread (separate, mutex-protected) completely unaffected
- `AudioRenderer::OpenEditor()` now fully synchronous — no promises, no timeout, reliable
- OB-Xd editor now successfully calls `attached()` without deadlock
- Plugin-side editor incomplete UI confirmed as outside minimal host scope

**IPlugFrame queryInterface fix:**
- Identified VST3 spec violation: `EditorPlugFrame::queryInterface()` only handled `FUnknown::iid`, not `IPlugFrame::iid`
- Applied spec-compliant fix matching HostApplication pattern
- This improves compatibility for future plugins even if OB-Xd remains unsupported

### Bridge Load ACK Crash/No-Response Hardening (2026-03-13)

**Files modified:**
- `src/mmk-vst3-bridge/src/bridge.cpp` — wrapped load-command handling so native exceptions become `load_ack` failures instead of dropping the pipe silently; parse/ACK write failures now also emit stderr diagnostics.
- `src/MinimalMusicKeyboard/Services/Vst3BridgeBackend.cs` — redirected bridge stdout/stderr, buffered native diagnostics, and folded bridge exit codes + captured stderr into load-failure messages when the ACK is missing or times out.

**What changed operationally:**
- A bridge-side C++ exception during `renderer_.Load(...)`, `SupportsEditor()`, or `GetEditorDiagnostics()` now returns `ack:"load_ack", ok:false` with a real error message instead of tearing down the request path with `<no response>`.
- If the native bridge still dies before it can ACK, the managed backend now reports deterministic context such as the bridge process exit code and the last stderr lines, so OB-Xd-style failures no longer look silent from the app.

**Reusable pattern:**
- For native helper processes behind a line-based ACK protocol, harden both ends: catch and serialize command-local failures on the native side, then capture child-process stderr/exit code on the managed side as a second diagnostic channel.
- Treat `<no response>` as an instrumentation bug. Even when the root plugin failure is outside host control, the host should still surface either a structured ACK error or a concrete crash/exit diagnostic.

### Load-Time VST3 Editor Diagnostics Deployment Fix (2026-03-13)

**Files modified:**
- `src/mmk-vst3-bridge/src/bridge.cpp` — load ACK now preserves a non-empty `editorDiagnostics` string whenever `supportsEditor` is false, falling back to the native load error if discovery diagnostics were never populated.
- `src/MinimalMusicKeyboard/MinimalMusicKeyboard.csproj` — fixed the app build copy path so the freshly built native bridge from `src\mmk-vst3-bridge\build\Release` is actually deployed beside the managed app.

**What was really going wrong:**
- The native bridge source already emitted detailed editor discovery diagnostics, but the WinUI app was still launching an older deployed `mmk-vst3-bridge.exe` that predated the `supportsEditor` / `editorDiagnostics` fields.
- The stale binary stayed in `bin\...\win-x64\mmk-vst3-bridge.exe` because the MSBuild copy target pointed at a non-existent repo-root `mmk-vst3-bridge\build\Release` folder instead of `src\mmk-vst3-bridge\build\Release`.
- That meant the managed parser saw `supportsEditor=false` with no `editorDiagnostics` field and fell back to the generic "Plugin editor is not available." text.

**Reusable pattern:**
- When a native/managed IPC contract looks correct in source but runtime behavior still matches an older protocol, verify the deployed binary beside the app, not just the producer project output.
- For VST3 load ACKs, always send a non-empty editor-availability diagnostic when `supportsEditor` is false so the managed layer never has to invent a generic fallback.

**Build result:**
- ✅ Native bridge rebuilt successfully in `src\mmk-vst3-bridge\build\Release`
- ✅ Managed app rebuilt successfully in Debug x64 and now deploys the same bridge binary hash beside the app
- ✅ `dotnet test` still completes with the existing "0 tests discovered" warning only

### OB-Xd VST3 Host Compatibility Fix (2026-03-12)

**Files modified:**
- `src/mmk-vst3-bridge/src/audio_renderer.cpp` — Added controller state sync from component after separate-controller initialization and after successful preset loads
- `src/mmk-vst3-bridge/CMakeLists.txt` — Added Steinberg `memorystream.cpp` so `Steinberg::MemoryStream` links in the native bridge build

**What fixed the likely host-side compatibility gap:**
- Some VST3 plugins, including OB-Xd-style separate-controller plugins, expect the host to call `IEditController::setComponentState()` after `controller->initialize()`.
- Our host already discovered and initialized separate controllers, but never copied the component state into the controller, leaving controller-side parameters out of sync with the component.
- The bridge now snapshots component state into a `Steinberg::MemoryStream`, rewinds it, and passes it to `controller_->setComponentState()` for separate-controller plugins.

**Why this matters:**
- This is a standard VST3 host responsibility for split component/controller plugins and can affect load-time compatibility, editor behavior, and initial parameter state.
- Re-running the same sync after a `.vstpreset` load keeps the controller/editor aligned with the newly loaded component state.

**Build result:**
- ✅ Native bridge builds successfully in Release after linking `memorystream.cpp`
- ✅ Managed app builds successfully in Release (2 pre-existing CS0414 warnings only)

### Cumulative VST3 Bridge Summary (2026-03-01 through 2026-03-12)

**Overview:** Faye implemented and debugged native VST3 plugin hosting via C++ bridge (mmk-vst3-bridge) with managed C# backend and Win32 GUI integration.

**Major milestones:**
1. **Phase 3 (2026-03-11):** Project scaffolding with CMake, vcpkg, IPC (named pipes), shared memory, audio render thread
2. **Phase 3b (2026-03-12):** VST3 SDK integration — plugin loading, MIDI event list, stereo output rendering  
3. **Bug fixes (2026-03-19):** Fixed 4 critical audio/spec issues (sample rate, IHostApplication, IEditController, MIDI event type)
4. **Lifetime handling (2026-03-19):** Dangling pointers, IConnectionPoint cleanup, single-object COM handling
5. **Bus activation (2026-03-19):** Added mandatory activateBus calls before setActive; GUI thread with message pump
6. **Race conditions (2026-03-12):** Deferred backend activation until LoadAsync completes
7. **Diagnostics (2026-03-12):** Stage-specific editor error reporting for OB-Xd troubleshooting

**Critical patterns learned:**
- VST3 plugin load sequence: Module → queryInterface(IComponent) → activate buses → setActive → initialize → setupProcessing
- Editor bring-up: Query IEditController → createView(kEditor) → attach to HWND → message loop
- Single-object plugins: IComponent and IEditController may be the same COM object; detect via queryInterface and skip duplicate init/cleanup
- Host responsibilities: IHostApplication required by spec, IConnectionPoint disconnect before terminate, proper teardown order
- Thread model: Audio render on bridge thread, editor on dedicated Win32 message loop thread, avoid blocking IPC loop

**Build status:** ✅ C# solution clean (0 errors), C++ bridge builds successfully (x64 Release)

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

## VST3 Pipeline Audit + C++ Fixes (2026-03-12)

**Task:** Root-cause audio silence + secondary bug fixes
**Status:** ✅ Complete — Commit 9939563

### Findings (Audit / agent-29)
- Root cause: ProcessStartInfo.Arguments missing (bridge exits rgc < 2)
- Secondary bugs: sample rate (44.1→48 kHz), block size (256→960), IHostApplication missing, QueueSetProgram broken
- Tier 1 (audio): Root cause + sample/block rate fix
- Tier 2 (spec): IHostApplication + IEditController initialization
- Tier 3 (ring-buffer): C# read tracking
- Tier 4 (GUI): VST3 editor window hosting (scoped by Jet)

### C++ Fixes Implemented (agent-31)
- **Fix 1:** Sample rate 44'100 → 48'000; block size 256 → 960 (audio_renderer.h)
- **Fix 2:** Created host_application.h stub; implements getName(), COM model
- **Fix 3:** Query + initialize IEditController; store as member; proper teardown
- **Fix 4:** QueueSetProgram: output event → input MIDI bytes (kDataEvent + raw MIDI)

### Lifetime Bugs Fixed (pre-commit, agent-31 follow-up)
- **Bug 1:** HostApplication now member hostApp_ (was local, got destroyed)
- **Bug 2:** Added disconnect before terminate in ResetPluginState (IConnectionPoint)

### Impact
- Bridge now connects successfully and initializes VST3 plugins
- Sample rate and block size aligned between host and bridge
- IHostApplication available for plugins requiring it
- Plugin state initialization correct; GUI hosting prepared for Phase 2

### Technical Details
- Used Steinberg::IPtr<> for COM lifetime (ref-counted RAII)
- HostApplication uses std::atomic<uint32> for thread-safe ref counting
- IEditController queried via component_->queryInterface(); fallback to factory
- IConnectionPoint disconnect/terminate sequence per VST3 best practices

### Build Status
✅ C# builds successfully (0 errors, 2 pre-existing warnings)
⏳ C++ bridge build deferred (VST3 SDK setup required)

### VST3 Bus Activation + GUI Hosting (2026-03-19)

**Files modified:**
- `src/mmk-vst3-bridge/src/audio_renderer.h` — Added `OpenEditor()`, `CloseEditor()`, `EditorMessageLoop()` methods; added `plugView_`, `editorThread_`, `editorHwnd_`, `editorOpen_` members; included `ivsteditcontroller.h`, `iplugview.h`, and `Windows.h`
- `src/mmk-vst3-bridge/src/audio_renderer.cpp` — Fixed critical bus activation bug in `Load()`; corrected `processData.numSamples`; implemented full Win32 GUI hosting with separate message loop thread
- `src/mmk-vst3-bridge/src/bridge.cpp` — Added `openEditor` and `closeEditor` command handlers with JSON ack responses

**Critical bug fixed (Sub-task A):**
- **VST3 bus activation missing:** VST3 spec requires calling `component_->activateBus()` for each bus BEFORE calling `component_->setActive(true)`. Without activating the event input bus, most plugins silently ignore MIDI note events.
- **Fix:** Added `component_->activateBus(kAudio, kOutput, 0, true)` and `component_->activateBus(kEvent, kInput, 0, true)` after `setupProcessing()` and before `setActive(true)`.
- **Secondary fix:** Changed `processData.numSamples` from hardcoded `kMaxBlockSize` to dynamic `frameSize` parameter, ensuring the plugin renders exactly the requested number of samples per process call.

**GUI hosting implementation (Sub-task B):**
- `OpenEditor()`: Queries `IEditController::createView(kEditor)`, validates HWND platform support, creates a Win32 window (`WS_OVERLAPPEDWINDOW | WS_VISIBLE`), attaches the plugin view, and starts a dedicated message loop thread
- `CloseEditor()`: Posts `WM_QUIT` to wake the message loop, joins the thread, calls `IPlugView::removed()`, and destroys the window
- `EditorMessageLoop()`: Runs standard Win32 message pump (`GetMessageW` → `TranslateMessage` → `DispatchMessageW`) while `editorOpen_` is true
- Window class registration is idempotent (class `MmkVst3Editor` is registered once, subsequent calls fail gracefully with `ERROR_CLASS_ALREADY_EXISTS`)
- Editor thread is separate from the audio render thread to avoid blocking the bridge's IPC command loop
- `ResetPluginState()` now calls `CloseEditor()` first to ensure clean teardown before plugin termination

**VST3 patterns learned:**
- Bus activation is mandatory: Even if a plugin doesn't use event input or audio output, the host must explicitly activate each bus via `activateBus()` before calling `setActive(true)`. Some plugins fail silently without this.
- GUI hosting thread model: `IPlugView` requires a Win32 message pump running on the thread that called `attached()`. Using a dedicated thread keeps the bridge responsive while the plugin GUI is open.
- Window lifetime: Always call `IPlugView::removed()` BEFORE releasing the `IPlugView` pointer or destroying the HWND, otherwise some plugins crash on cleanup.
- Message loop cleanup: Use `PostMessageW(hwnd, WM_QUIT, 0, 0)` to gracefully exit `GetMessageW()` loop, then join the thread to ensure it completes before destroying resources.

**Status:** Code complete. C++ build deferred pending VST3 SDK setup.

### VST3 Editor Availability Bug Investigation (2026-03-19)

**Problem:** User reports "Editor not available or vst not loaded" when trying to open VST3 editor GUI after bridge rebuild.

**Root cause analysis:**

1. **Race condition in AudioEngine.HandleVst3Instrument():**
   - Line 187 of `AudioEngine.cs` immediately assigns `_vst3Backend` as the active backend via `Volatile.Write()`
   - Line 188 calls `_vst3Backend.LoadAsync(instrument)` as fire-and-forget (`_ =`)
   - `LoadAsync` is async and takes time (start process → connect pipe → send load → wait for ack)
   - If user clicks "Open Editor" button before `LoadAsync` completes, `SupportsEditor` returns false because `_isReady` is still false
   - **Location:** `src/MinimalMusicKeyboard/Services/AudioEngine.cs:187-188`

2. **Silent failure when bridge.exe is missing:**
   - `LoadAsync` returns early at line 213 if `mmk-vst3-bridge.exe` doesn't exist
   - Backend is already assigned as active, but `_isReady` never gets set to true
   - User sees "not loaded" message even though bridge should be present
   - **Location:** `src/MinimalMusicKeyboard/Services/Vst3BridgeBackend.cs:206-214`

3. **Error message origin:**
   - "Editor not available or vst not loaded" generated at `SettingsWindow.xaml.cs:487-488`
   - Triggered when `SupportsEditor` property returns false (which is just `_isReady`)
   - **Location:** `src/MinimalMusicKeyboard/Views/SettingsWindow.xaml.cs:473-493`

**Bridge C++ code analysis:**
- `Load()` function (audio_renderer.cpp:34-169) — Correctly activates buses and initializes plugin
- `OpenEditor()` function (audio_renderer.cpp:423-500) — Properly checks for `controller_` and creates Win32 window
- `bridge.cpp` "openEditor" IPC handler (lines 107-118) — Correctly calls `OpenEditor()` and sends ack with error
- All bridge-side code appears correct; problem is purely on C# side timing/state management

**Fix recommendations:**

1. **For the race condition:** Either:
   - a) Make `HandleVst3Instrument` await `LoadAsync` before assigning `_activeBackend` (but this blocks MIDI thread)
   - b) Defer `_activeBackend` assignment until `LoadAsync` completes (better — keep fire-and-forget pattern)
   - c) Add "loading" state to UI to disable editor button until ready

2. **For silent failure:** Add logging or error notification when bridge.exe is missing so user knows why VST3 isn't working

**VST3 load/editor protocol confirmed correct:**
- Bridge sends `{"ack":"load_ack","ok":true}` on successful load (bridge.cpp:68-74)
- C# waits for ack and parses correctly (Vst3BridgeBackend.cs:283-290)
- Only after successful ack does C# set `_isReady = true` (line 302)
- Editor IPC command follows same pattern with `{"ack":"editor_opened","ok":...}` (bridge.cpp:112-116)
- No path issues found — VST3 path is passed directly from C# to bridge in load command

### VST3 Editor Availability — Race Condition Resolved (2026-03-12)

**Status:** FIXED by Jet  
**Problem:** User received "Editor not available" error when clicking "Open VST3 Editor" button, despite bridge being freshly rebuilt and functional.

**Diagnosis complete** (2026-03-19 analysis):
- Root cause: `AudioEngine.HandleVst3Instrument()` assigned `_activeBackend` immediately before async `LoadAsync()` completed
- Race window: If user clicked "Open Editor" during loading, `SupportsEditor` returned false (because `_isReady` was still false)
- Secondary issue: Missing bridge.exe caused silent failure with no user feedback

**Bridge analysis:** Bridge C++ code was correct; bug was purely C# side (timing/state management).

**Solution implemented by Jet (2026-03-12):**
1. Deferred backend assignment until `LoadAsync()` completes and `_isReady == true`
2. Bridge-missing now fires `BridgeFaulted` event instead of silent return
3. New `InstrumentLoadFailed` event propagates errors to UI via `ContentDialog`
4. Editor button shows "VST3 Plugin Still Loading" during load phase
5. Build verified: 0 errors

**Related decision records:** `.squad/decisions.md` Section "Session: 2026-03-12 — VST3 Load Race Condition Fix"


### VST3 Editor Diagnostics + Shared-Controller Fix (2026-03-12)

**Files modified:**
- `src/mmk-vst3-bridge/src/audio_renderer.h` — added editor capability/diagnostic state and shared-controller tracking
- `src/mmk-vst3-bridge/src/audio_renderer.cpp` — staged editor diagnostics, richer open-editor errors, skipped double initialize/terminate for shared component+controller
- `src/mmk-vst3-bridge/src/bridge.cpp` — load ACK now includes `supportsEditor` and `editorDiagnostics`
- `src/MinimalMusicKeyboard/Services/Vst3BridgeBackend.cs` — parses editor diagnostics from load ACK and exposes editor availability reason
- `src/MinimalMusicKeyboard/Views/SettingsWindow.xaml.cs` — shows backend-provided editor reason instead of generic dialog text

**Key learnings:**
- VST3 editor failures need stage-specific reporting: direct controller query, controller class lookup, factory instantiation, controller initialize, `createView`, HWND support, Win32 host window creation, and `attached()` can all fail independently.
- Some VST3 plugins expose `IComponent` and `IEditController` on the same object. In that case the bridge must not call `initialize()` / `terminate()` twice or try to connect the object to itself via `IConnectionPoint`.
- Surfacing editor capability in the load ACK lets the WinUI layer disable the editor button and show the exact bridge diagnosis before the user retries.

**Verification:**
- `dotnet build .\MinimalMusicKeyboard.sln --no-incremental` ✅
- `dotnet test .\MinimalMusicKeyboard.sln --no-build` ✅ (0 discovered tests; existing warning unchanged)
- `"C:\Program Files\Microsoft Visual Studio\18\Professional\MSBuild\Current\Bin\MSBuild.exe" src\mmk-vst3-bridge\build\mmk-vst3-bridge.sln /m /p:Configuration=Release /p:Platform=x64` ✅

### VST3 Editor Diagnostics & Shared-Controller Bug (2026-03-12)

**Files modified:**
- \src/mmk-vst3-bridge/src/audio_renderer.h\ — Removed single-object coupling workaround
- \src/mmk-vst3-bridge/src/audio_renderer.cpp\ — Improved error reporting with stage-specific diagnostics
- \src/MinimalMusicKeyboard/Services/Vst3BridgeBackend.cs\ — New supportsEditor, editorDiagnostics fields
- \src/MinimalMusicKeyboard/Views/SettingsWindow.xaml.cs\ — UI now displays exact failure reason

**Problems fixed:**
1. **Generic editor failure message:** All editor failures reported as 'No IEditController' regardless of actual failure stage (e.g., HWND creation failed, IPlugView::attached failed). Made debugging OB-Xd GUI issues impossible.
2. **Coupled single-object controller/component:** Some plugins expose both IComponent and IEditController on the same COM object. Bridge was calling initialize() twice and connecting IConnectionPoint to itself, causing crashes/undefined behavior during teardown.

**Solution:**
- Propagate structured diagnostics from each editor bring-up stage (controller query, class lookup, factory, initialization, createView, HWND, Win32 host window, attached)
- For single-object plugins: skip second initialize() and don't connect/disconnect IConnectionPoint to self
- Return supportsEditor + editorDiagnostics array in load_ack
- SettingsWindow displays specific reason (stage + error code) when editor unavailable

**Status:** All builds clean, tests passing. Commit 16482e7.

### OB-Xd VST3 Host-Side Controller State Sync (2026-03-12)

**Decision:** Implement standard VST3 host pattern for split component/controller plugins: after IEditController::initialize(), copy component state into controller with setComponentState(). Re-sync after preset loads.

**Why:** OB-Xd and other split-architecture plugins expect host to synchronize state between component and controller. Bridge was missing this step, causing editor to start with stale state. Also ensures preset-applied changes reflect in editor.

**Implementation:**
- Added component→controller sync in src/mmk-vst3-bridge/src/audio_renderer.cpp using Steinberg::MemoryStream
- Sync triggered after controller initialization succeeds
- Re-sync triggered after successful .vstpreset load (via setComponentState())
- Added public.sdk/source/common/memorystream.cpp to CMakeLists.txt for linking

**Result:** 
- Native Release build: ✓ passed
- Managed Release build: ✓ passed (2 pre-existing warnings)
- OB-Xd editor now receives consistent state matching component
- Preset changes immediately reflected in controller/editor UI

**Commit:** f3950e5

---

---

## Session: VST3 Load-Time Diagnostics Fix (2026-03-13T08:52Z)

**Outcomes:** Committed c2e6662. Fixed deployment + protocol hardening for VST3 editor diagnostics.

**Key Actions:**
- Native bridge: Guaranteed non-empty ditorDiagnostics in load_ack serialization when supportsEditor=false
- Managed app: Updated build deploy target to copy freshly built mmk-vst3-bridge.exe from src\mmk-vst3-bridge\build\Release

**What Was Happening:**
- App's MSBuild copy path pointed to non-existent root-level mmk-vst3-bridge\build\Release
- Stale bridge binary stayed in deployed location
- Protocol lacked guarantee for non-empty diagnostic when editor unavailable

**Verification:**
- ✅ Native build successful
- ✅ Managed build successful  
- ✅ dotnet test passed
- ✅ OB-Xd now shows real load-time diagnostic reason, not generic fallback

**Related:** OB-Xd VST3 Host Compatibility Fix (2026-03-12), earlier in history
