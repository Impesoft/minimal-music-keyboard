# Jet — History

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

**Jet's implementation focus:**
- MIDI device discovery and I/O (Windows.Devices.Midi2 / NAudio / RtMidi.NET — TBD)
- System tray integration (H.NotifyIcon for WinUI3 or similar)
- Single-instance enforcement
- On-demand settings window lifecycle
- Graceful shutdown: stop MIDI thread → dispose audio engine → destroy tray icon

## Learnings

<!-- append new learnings below -->

### Session: Phase 4 — VST3 Settings UI (2026-03-11, Latest)

**Context:** Extended the Settings UI to support VST3 instrument configuration for the 8 button mapping slots. Users can now select either SF2 or VST3 instruments per slot.

**Implementation:**

**Settings UI Extensions:**
- Redesigned `PopulateButtonMappings()` to include an instrument type selector (SF2 / VST3) per slot
- Added two panels per slot:
  - SF2 panel: Existing catalog combo + SF2 path + browse (shown when SF2 selected)
  - VST3 panel: Plugin path + browse, preset path + browse (shown when VST3 selected)
- Panels dynamically show/hide based on type selector (RadioButtons control)
- Added three file picker methods:
  - `PickSf2FileAsync()` (existing)
  - `PickVst3PluginFileAsync()` — filters `.vst3` files
  - `PickVst3PresetFileAsync()` — filters `.vstpreset` files
- All pickers use `InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this))` pattern for WinUI3 handle initialization

**Data Model:**
- Extended `MappingRowState` record with:
  - `RadioButtons TypeSelector` — SF2 vs VST3 toggle
  - `StackPanel Sf2Panel`, `StackPanel Vst3Panel` — separate control containers
  - `TextBlock Vst3PluginLabel`, `TextBlock Vst3PresetLabel` — VST3 path displays
  - `InstrumentDefinition? SlotInstrument` — tracks full definition (not just ID)
- Each VST3 slot creates a unique `InstrumentDefinition`:
  - `Id = "vst3-slot-{slotIndex}"`
  - `DisplayName = "VST3 Slot {slotIndex + 1}"`
  - `Type = InstrumentType.Vst3`
  - `Vst3PluginPath`, `Vst3PresetPath` — user-selected paths

**Backend Integration:**
- **AudioEngine.cs:** Added `Vst3BridgeBackend _vst3Backend` field
  - Registered VST3 backend's sample provider with mixer
  - Split `SelectInstrument()` into `HandleSoundFontInstrument()` and `HandleVst3Instrument()`
  - VST3 instruments trigger backend switch + `LoadAsync()` call
  - Added `_vst3Backend.Dispose()` to cleanup
- **InstrumentCatalog.cs:** Added `AddOrUpdateVst3Instrument()` method
  - VST3 slot instruments persisted to `instruments.json`
  - Retrieved via `GetById()` like SF2 instruments

**Build result:** `dotnet build --no-incremental` → **Build succeeded in ~8.7s, 0 errors, 2 warnings** (CS0414 about unused `_frameSize` field in Vst3BridgeBackend — harmless, retained for consistency).

**Key learnings:**
1. **Slot-based VST3 Instruments:** Each VST3 slot gets a unique `InstrumentDefinition` stored in the catalog. This keeps the architecture consistent — all instruments come from the catalog, whether SF2 or VST3.
2. **WinUI3 FileOpenPicker Requires Window Handle:** Unpackaged WinUI3 apps must call `InitializeWithWindow.Initialize()` with `WindowNative.GetWindowHandle(this)` before showing pickers, or they fail silently.
3. **No Breaking Changes:** SF2 instrument selection continues to work exactly as before. SF2 is the default type (index 0), and all existing workflows are untouched.
4. **Backend Switching:** Audio engine swaps active backend via `Volatile.Write(ref _activeBackend, _vst3Backend)`. Audio thread reads from the active backend's sample provider on the next render callback.
5. **VST3 Bridge Not Implemented Yet:** Phase 3 (native C++ bridge) is now scaffolded (Faye's Phase 3 delivery), so `Vst3BridgeBackend.IsReady` will be false until bridge SDK integration is complete. UI allows configuration and paths persist to `instruments.json`.

**Known Limitations:**
- **VST3 Bridge Status Indicator:** Task spec mentioned showing "⚠️ VST3 bridge not ready" if `IsReady=false`. Not implemented yet — can be added in a future phase by subscribing to `Vst3BridgeBackend.BridgeFaulted` event.
- **No MVVM Toolkit:** Project uses code-behind pattern with direct event handlers. No `[RelayCommand]` or `[ObservableProperty]` attributes.


### Session: Phase 2 Fix-Up — Gren's 3 REJECTED issues (2026-03-11, Latest)

**Context:** Faye implemented Vst3BridgeBackend (Phase 2). Gren rejected with 3 issues (1 blocking, 2 required). Faye locked out; Jet applied fixes.

**Fix 1 — IPC resource ownership reversed (BLOCKING):**
The original implementation had the host connecting as a `NamedPipeClientStream` and opening an existing MMF (`MemoryMappedFile.OpenExisting`). Per the approved spec (vst3-architecture-proposal.md §3.2), the host must be the **server** side:
- Changed `NamedPipeClientStream` → `NamedPipeServerStream` (host creates pipe server, waits for bridge to connect)
- Changed `MemoryMappedFile.OpenExisting()` → `MemoryMappedFile.CreateNew()` (host creates MMF, writes header)
- Changed PID in names from `_bridgeProcess.Id` (bridge) → `Process.GetCurrentProcess().Id` (host)
- Reordered flow: create IPC resources → launch bridge → wait for connection → send load command

**Fix 2 — String allocations on audio thread (REQUIRED):**
`NoteOn()`, `NoteOff()`, `NoteOffAll()`, `SetProgram()` were allocating JSON strings inline via string interpolation (`$"..."`). These are called from the WASAPI audio render thread and must not allocate.
- Added `MidiCommand` readonly struct (discriminated union with `Kind` enum: NoteOn, NoteOff, NoteOffAll, SetProgram, Load, Shutdown)
- Changed `Channel<string>` → `Channel<MidiCommand>`
- Audio thread methods now write stack-allocated structs to the channel (zero heap allocation)
- Added `SerializeCommand(MidiCommand)` method that formats JSON in the background `RunPipeWriterAsync()` task
- All string allocation moved off the audio thread to the drain task thread

**Fix 3 — Dispose() race prevents shutdown command (REQUIRED):**
`Dispose()` was canceling the writer task (`_writerCts.Cancel()`) immediately after enqueueing the shutdown command, then killing the bridge. The drain task would throw `OperationCanceledException` before sending the command.
- After completing the channel, wait up to 500ms for the writer task to drain: `_pipeWriterTask?.Wait(TimeSpan.FromMilliseconds(500))`
- After writer exits, give bridge 2s graceful exit window: `_bridgeProcess.WaitForExit(2000)` before force-kill
- Move `_writerCts?.Cancel()` to the end (cleanup only, after writer task exited)

**Build result:** `dotnet build --no-incremental` → **Build succeeded in ~8.6s, 0 errors, 2 warnings** (CS0414 about unused `_frameSize` field — harmless, retained for consistency).

**Scribe actions:**
- Created orchestration log: `.squad/orchestration-log/2026-03-11T21-10-33Z-jet-phase2-fixes.md`
- Created session log: `.squad/log/2026-03-11T21-10-33Z-phase2-fixes.md`
- Merged decision into `.squad/decisions.md`
- Deleted inbox files: `jet-phase2-fixes.md`, `gren-phase2-review.md`

**Key learnings:**
1. **IPC ownership matters for crash resilience:** Host-as-owner (pipe server + MMF creator) ensures IPC handles survive bridge crashes. Bridge-as-owner would require re-creating resources on every restart.
2. **Audio thread is zero-allocation hot path:** Even small string allocations create GC pressure that can cause missed WASAPI callbacks over hours of runtime. Use struct channels and serialize off-thread.
3. **Graceful shutdown requires coordination:** Cancel tokens preempt async work. Must wait for drain tasks to complete before killing child processes, or "best-effort" commands never get sent.

### Session: Phase 1 Fix-Up — Gren's 3 REJECTED issues (2026-03-11)

**Context:** Faye implemented Vst3BridgeBackend (Phase 2). Gren rejected with 3 issues (1 blocking, 2 required). Faye locked out; Jet applied fixes.

**Fix 1 — IPC resource ownership reversed (BLOCKING):**
The original implementation had the host connecting as a `NamedPipeClientStream` and opening an existing MMF (`MemoryMappedFile.OpenExisting`). Per the approved spec (vst3-architecture-proposal.md §3.2), the host must be the **server** side:
- Changed `NamedPipeClientStream` → `NamedPipeServerStream` (host creates pipe server, waits for bridge to connect)
- Changed `MemoryMappedFile.OpenExisting()` → `MemoryMappedFile.CreateNew()` (host creates MMF, writes header)
- Changed PID in names from `_bridgeProcess.Id` (bridge) → `Process.GetCurrentProcess().Id` (host)
- Reordered flow: create IPC resources → launch bridge → wait for connection → send load command

**Fix 2 — String allocations on audio thread (REQUIRED):**
`NoteOn()`, `NoteOff()`, `NoteOffAll()`, `SetProgram()` were allocating JSON strings inline via string interpolation (`$"..."`). These are called from the WASAPI audio render thread and must not allocate.
- Changed `Channel<string>` → `Channel<MidiCommand>` (readonly struct)
- Audio thread methods now write stack-allocated structs to the channel (zero heap allocation)
- Added `SerializeCommand(MidiCommand)` that formats JSON in the background `RunPipeWriterAsync()` task
- All string allocation moved off the audio thread to the drain task thread

**Fix 3 — Dispose() race prevents shutdown command (REQUIRED):**
`Dispose()` was canceling the writer task (`_writerCts.Cancel()`) immediately after enqueueing the shutdown command, then killing the bridge. The drain task would throw `OperationCanceledException` before sending the command.
- After completing the channel, wait up to 500ms for the writer task to drain: `_pipeWriterTask?.Wait(TimeSpan.FromMilliseconds(500))`
- After writer exits, give bridge 2s graceful exit window: `_bridgeProcess.WaitForExit(2000)` before force-kill
- Move `_writerCts?.Cancel()` to the end (cleanup only, after writer task exited)

**Build result:** `dotnet build --no-incremental` → **Build succeeded in ~8.6s, 0 errors, 2 warnings** (CS0414 about unused `_frameSize` field — harmless, retained for consistency).

**Scribe actions:**
- Created decision doc: `.squad/decisions/inbox/jet-phase2-fixes.md`
- Updated history: `.squad/agents/jet/history.md`

**Key learnings:**
1. **IPC ownership matters for crash resilience:** Host-as-owner (pipe server + MMF creator) ensures IPC handles survive bridge crashes. Bridge-as-owner would require re-creating resources on every restart.
2. **Audio thread is zero-allocation hot path:** Even small string allocations create GC pressure that can cause missed WASAPI callbacks over hours of runtime. Use struct channels and serialize off-thread.
3. **Graceful shutdown requires coordination:** Cancel tokens preempt async work. Must wait for drain tasks to complete before killing child processes, or "best-effort" commands never get sent.

### Session: Phase 1 Fix-Up — Gren's 3 REJECTED issues (2026-03-11)

**Context:** Faye implemented the VST3 refactor Phase 1. Gren rejected with 3 issues. Faye locked out; Jet applied fixes.

**Fix 1 — AudioEngine.cs line 177 (compilation error):**
`}));` → `});` — one extra closing parenthesis inside `LoadSoundFont()`. Removed the stray `)`.

**Fix 2 — IInstrumentBackend.cs (interface doc threading contract):**
Added `<remarks>Called from the audio thread only. Do not call from the MIDI callback thread.</remarks>` to `NoteOn()`, `NoteOff()`, and `NoteOffAll()`. The summary lines already said "audio render thread only" but Gren required an explicit remarks block to make the contract unmissable for Phase 2 implementers.

**Fix 3 — SoundFontBackend.cs (unused ConcurrentQueue field):**
`_commandQueue` field and the `ConcurrentQueue<MidiCommand> commandQueue` constructor parameter were dead code — Faye correctly chose AudioEngine-drains-queue (option a) but didn't remove the old injection. Removed the field, the constructor parameter, and the `using System.Collections.Concurrent` directive. Updated `AudioEngine` constructor call from `new SoundFontBackend(path, _commandQueue)` to `new SoundFontBackend(path)`.

**Build result:** `dotnet build --no-incremental` → **Build succeeded in ~10s, 0 errors, 0 warnings.**

**Scribe actions:**
- Created orchestration log: `.squad/orchestration-log/2026-03-11T20-49-41Z-jet-phase1-fixes.md`
- Created session log: `.squad/log/2026-03-11T20-49-41Z-phase1-fixes-batch.md`
- Merged Jet Phase 1 fixes into `.squad/decisions.md`
- Staged and committed 6 files (3 source, 3 .squad/) with Copilot co-author trailer
- Commit hash: 4519325

### Session: Tray Icon Not Visible — ForceCreate + IconSource Fix (2026-03-01)

**Symptom:** App started but no tray icon appeared — not in taskbar, not in notification area, nowhere.

**Root cause 1 — No IconSource:** `TaskbarIcon` was constructed without an `IconSource`. Windows silently drops tray icons that have no image. The property was left as a comment placeholder in the original scaffold.

**Root cause 2 — No ForceCreate():** H.NotifyIcon.WinUI v2.x requires `ForceCreate(bool)` to be called when the `TaskbarIcon` is created programmatically (i.e. `new TaskbarIcon { ... }` in code, not instantiated from XAML resources). Without it, the icon is never actually registered with the Windows shell. The `bool` parameter controls "Efficiency Mode" — passing `true` hides the app (that's the whole problem), so we pass `false`.

**Root cause 3 — No Assets folder:** The WinUI3 scaffold was created without default template assets. The `Assets\` folder did not exist and had no icon files.

**Fixes applied:**
1. Created `src\MinimalMusicKeyboard\Assets\AppIcon.png` — minimal 32×32 PNG icon generated via System.Drawing.
2. Added explicit `<Content Include="Assets\AppIcon.png"><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></Content>` to `.csproj` — ensures the file copies to the output directory and is accessible via `ms-appx:///Assets/AppIcon.png`.
3. `TrayIconService.Initialize()`: set `IconSource = new BitmapImage(new Uri("ms-appx:///Assets/AppIcon.png"))` in the `TaskbarIcon` initializer.
4. `TrayIconService.Initialize()`: added `_taskbarIcon.ForceCreate(false)` after the icon and menu are fully configured.
5. Added `using Microsoft.UI.Xaml.Media.Imaging;` for `BitmapImage`.

**API note:** `H.NotifyIcon.TaskbarIcon.IconSource` accepts `ImageSource` (the WinUI3 type). `BitmapImage` is the correct concrete type for URI-based images — not `BitmapIcon` (which is a different control). `BitmapImage` derives from `ImageSource` and takes the URI in its constructor.

**Build result:** MSBuild 0 errors, 0 build errors. NETSDK1057 is informational only (preview SDK).

### Session: Self-Contained Windows App Runtime (2026-03-01)

**Change:** Added `<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>` and `<SelfContained>true</SelfContained>` to the main `PropertyGroup` in `MinimalMusicKeyboard.csproj`.

**Why:** Ward requested zero external installer requirements. Without these flags, users must have the Windows App Runtime installed separately on their machine. With `WindowsAppSDKSelfContained=true`, the Windows App Runtime DLLs are bundled into the build output directory alongside the app executable. `SelfContained=true` is a prerequisite for `WindowsAppSDKSelfContained`.

**Trade-off:** Publish output size increases (runtime DLLs included), but the NuGet reference to `Microsoft.WindowsAppSDK` remains — this is correct and expected. The NuGet package provides the build-time targets and headers; the DLLs ship with the app.

**Build result:** MSBuild 0 errors, 0 build errors. Output: `bin\x64\Debug\net8.0-windows10.0.22621.0\win-x64\MinimalMusicKeyboard.dll`. NETSDK1057 (preview .NET SDK notice) is informational only, not an error.

### Session: Initial Scaffold (2026-03-01)

**Files created:**
- `.gitignore` — standard .NET + WinUI3 ignores
- `MinimalMusicKeyboard.sln` — solution with main + test projects
- `src/MinimalMusicKeyboard/MinimalMusicKeyboard.csproj` — WinUI3, net8.0-windows10.0.22621.0, unpackaged
- `src/MinimalMusicKeyboard/app.manifest` — PerMonitorV2 DPI, Win10/11 compatibility
- `src/MinimalMusicKeyboard/App.xaml` + `App.xaml.cs` — no window at startup
- `src/MinimalMusicKeyboard/Program.cs` — STAThread, ComWrappersSupport, DispatcherQueue sync context
- `src/MinimalMusicKeyboard/Core/SingleInstanceGuard.cs` — named Mutex with user SID
- `src/MinimalMusicKeyboard/Interfaces/IAudioEngine.cs` — stub interface for Faye
- `src/MinimalMusicKeyboard/Midi/MidiDeviceInfo.cs` — record DTO
- `src/MinimalMusicKeyboard/Helpers/DisposableExtensions.cs` — SafeDispose for shutdown sequences
- `src/MinimalMusicKeyboard/Services/MidiDeviceService.cs` — NAudio.Midi, disconnect handling, reconnect loop
- `src/MinimalMusicKeyboard/Services/TrayIconService.cs` — H.NotifyIcon.WinUI, context menu, disposal
- `src/MinimalMusicKeyboard/Services/AppLifecycleManager.cs` — startup/shutdown orchestration, on-demand SettingsWindow
- `src/MinimalMusicKeyboard/Views/SettingsWindow.xaml` + `.xaml.cs` — on-demand stub
- `src/MinimalMusicKeyboard.Tests/MinimalMusicKeyboard.Tests.csproj` — xUnit stub for Ed

**Key patterns used:**
- Standard IDisposable: `bool _disposed`, `GC.SuppressFinalize`, guard at top of Dispose
- File-scoped namespaces throughout
- Explicit event handler unsubscription before Dispose (prevents ghost icons, handler leaks)
- `lock(_deviceLock)` around MidiIn open/close to guard concurrent reconnect vs dispose
- Reconnect via `Task.Run` + `CancellationToken` (2s polling as per arch Section 3.2)
- `SingleInstanceGuard` as `using` in `Program.Main` — mutex lifetime = process lifetime
- On-demand SettingsWindow with `_activeSettingsWindow` nullable field pattern (arch Section 3.6)

**API correctness discoveries (during build verification):**
- `NAudio.Midi.MidiCommandCode` uses `PatchChange` (not `ProgramChange`) for program change messages
- `MeltySynth 2.4.0` Synthesizer: no `ControlChange`/`ProgramChange` methods — use `ProcessMidiMessage(channel, 0xB0/0xC0, data1, data2)`
- `MeltySynth 2.4.0` SoundFont constructor takes `Stream` (not `BinaryReader`)
- `MeltySynth 2.4.0` `SoundFont` is NOT `IDisposable` — just null/clear references
- `H.NotifyIcon.WinUI 2.2.0` uses `DoubleClickCommand: ICommand` not `TrayMouseDoubleClick` event
- `WasapiOut(AudioClientShareMode, bool, int)` 3-param ctor: positional param is `latency`, not `latencyMilliseconds`
- Build requires MSBuild from VS installation (not `dotnet` CLI) for WinUI3 XAML+PRI tasks
- `H.NotifyIcon.WinUI 2.2.0` requires `Microsoft.WindowsAppSDK >= 1.6.x`
1. **Disposal order:** Followed architecture Section 6 (midi→audio→tray) not task spec's "reverse startup" (tray→audio→midi). Architecture rationale (prevents note events on disposed engine) is correct.
2. **Services/ folder:** Task spec explicitly said `Services/` for TrayIconService, MidiDeviceService, AppLifecycleManager. Architecture used separate subfolders. Followed task spec.
3. **No DI wiring:** Added Microsoft.Extensions.DependencyInjection package as requested, but left manual wiring per architecture/Gren approval. Container available for Ed's test seams.
4. **Test project reference:** No ProjectReference added — WinUI3 net8.0-windows target creates CI complications on non-Windows agents; deferred to Ed.

### Cross-Agent: Faye Integration (2026-03-01)
**Coordination with Faye (Audio Dev):**
- Faye discovered API mismatches during AudioEngine implementation and provided corrections:
  - `MeltySynth.ProcessMidiMessage(channel, 0xB0/0xC0, data1, data2)` call signature (not direct `NoteOn`/`ProgramChange`)
  - `Stream` constructor for SoundFont (not `BinaryReader`)
  - `Volatile.Read/Write` pattern for Synthesizer instance swaps across threads
- Jet's build verified NAudio/MeltySynth API details; Faye's code adapted to match reality
- Both histories now synchronized on actual library contracts
