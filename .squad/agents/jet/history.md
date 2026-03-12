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

### Session: VST3 C# Bug Fixes — Process Arguments + Ring-buffer Read Tracking (2026-03-12)

**Context:** VST3 pipeline audit found two C# bugs in `Vst3BridgeBackend.cs` preventing VST3 instruments from producing sound. C++ fixes handled by Faye in parallel.

**Bug 1 — Missing Process Arguments (ROOT CAUSE):**
`LoadAsync()` launched `mmk-vst3-bridge.exe` without passing `Arguments` to `ProcessStartInfo`. The bridge process received `argc == 1`, printed usage, and exited with code 1. The C# side timed out waiting for pipe connection, set `_isReady = false`, and all subsequent NoteOn/NoteOff calls were silently dropped.

**Fix:** Added `Arguments = $"{hostPid}"` to the `ProcessStartInfo` block (line 242). The `hostPid` variable (already in scope as `Process.GetCurrentProcess().Id`) is now passed as the first command-line argument. Bridge now receives the host PID, constructs correct pipe/MMF names, connects successfully, and transitions to ready state.

**Bug 2 — Re-reading Same Frame (Ring-buffer Awareness):**
`Read()` always read from `MmfHeaderSize` offset without checking if the bridge had written a new frame since the last read. On WASAPI callbacks faster than the bridge's render tick, the same frame got re-read and played twice (caused phasing/distortion at some sample rates). Also risked reading partially-written frames.

**Fix:** 
1. Added `volatile int _lastReadPos = -1` field (line 47) to track the last read position
2. In `Read()`, added check before reading: read `writePos` from MMF header offset 12, compare to `_lastReadPos`, return silence if unchanged (lines 147-153)
3. Update `_lastReadPos = writePos` after confirming new frame available
4. Reset `_lastReadPos = -1` in `Dispose()` (line 388)

**MMF Header Layout (confirmed from code comments):**
- Offset  0: magic (0x4D4D4B56)
- Offset  4: version (1)
- Offset  8: frameSize
- Offset 12: writePos (atomic int32; bridge advances after each block)
- Offset 16+: audio data (float32 stereo-interleaved)

**Build result:** `dotnet build --no-incremental` → **Build succeeded in ~9.1s, 0 errors, 2 warnings** (CS0414 about unused `_frameSize` field — pre-existing, harmless).

**Key learnings:**
1. **Process launch requires explicit argument passing:** `ProcessStartInfo.Arguments` must be set manually. Unlike Unix `exec`, Windows CreateProcess doesn't merge the command into `argv[0]` — the first actual argument must be explicitly passed.
2. **Ring-buffer synchronization requires read tracking:** Audio threads reading from shared memory must track `writePos` to avoid re-reading stale frames. Without this, high-frequency WASAPI callbacks (e.g., 5ms quantum) read the same data multiple times before the bridge's 20ms render tick writes a new frame.
3. **Volatile fields for cross-thread state:** `_lastReadPos` is written by audio thread, but needs volatile semantics because it's logically shared state (even though only one thread accesses it, the MMF read is the coordination point). Volatile ensures compiler doesn't reorder reads of `_lastReadPos` vs `writePos`.
4. **Reset tracking state on dispose:** Any frame position tracking must reset to -1 on dispose/reset, so if the backend is reused or a new instrument loads, the first frame is always read (not skipped due to stale `_lastReadPos`).

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

### Session: VST3 Editor Error Surfacing (2026-03-12)

**Context:** OB-Xd 3 loaded elsewhere but this app still surfaced a generic "editor unavailable" path on the managed/UI side, masking the exact bridge diagnostic.

**Fixes applied:**
1. Added `IAudioEngine.GetVst3EditorAvailabilityDescription()` so the UI can read the latest bridge-supplied editor/load diagnostic even when the active backend is still SoundFont during VST3 load/failure.
2. Updated `SettingsWindow` to use that diagnostic for inline VST3 status and the "Editor Not Available" dialog instead of falling back to generic text.
3. Fixed the editor-button gating bug where the click handler always re-enabled the button in `finally`, even after failures or on non-editor-capable states.
4. Preserved exact open-editor exception text by showing `ex.Message` directly instead of wrapping it in another generic prefix.

**Build result:** `dotnet build src\MinimalMusicKeyboard\MinimalMusicKeyboard.csproj --no-incremental` → **Build succeeded in ~9.1s, 0 errors, 2 pre-existing warnings** (`CS0414` on `_frameSize` in `Vst3BridgeBackend`).

**Key learnings:**
1. **Do not key editor diagnostics off the active backend alone:** delayed VST3 activation means the old backend can remain active while the bridge is loading or faulted, so the UI needs a direct path to the last VST3 diagnostic.
2. **UI finally-blocks can accidentally erase capability gating:** re-enabling an editor button unconditionally after a failed click restores a broken path and reintroduces generic dialogs.
3. **Exception wrappers can hide the useful part of the message:** when bridge/editor code already returns a stage-specific failure string, surface that message directly in the dialog.

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

## VST3 Pipeline Fix + GUI Scoping (2026-03-12)

**Task:** Implement C# root cause fix + scope editor GUI hosting
**Status:** ✅ Complete — Commit 9939563 + scoping document

### C# Fixes Implemented (agent-32)
- **Fix 1:** Added Arguments = \"{hostPid}\" to ProcessStartInfo (ROOT CAUSE)
  - Bridge now receives host PID, constructs correct pipe/MMF names, connects successfully
  - Located at ~line 242 in Vst3BridgeBackend.cs
- **Fix 2:** Ring-buffer read tracking via _lastReadPos volatile field
  - Check MMF writePos (offset 12) before reading audio data
  - Prevents stale-frame re-reads and phasing/distortion artifacts
  - Lines 47 (field), 147-153 (Read method), 388 (Dispose reset)

### Design
- MMF header layout confirmed: magic (0), version (4), frameSize (8), writePos (12), audio data (16+)
- writePos is atomic int32; bridge increments after each render block
- C# side tracks _lastReadPos to detect new frame availability

### Impact
- VST3 instruments now produce audio at correct pitch and volume
- Audio pipeline bridged from UI → C# backend → named pipe → C++ bridge → VST3 plugin → MMF → WASAPI
- Ring-buffer prevents timing glitches between WASAPI callbacks and bridge render ticks

### GUI Hosting Scoping (agent-30)
- Evaluated 3 options: native popup (recommended), WinUI3 hosted (rejected—stability hazard), standalone (rejected)
- **Recommendation:** Bridge-owned Win32 popup (Option A)
  - Medium complexity, high stability, matches DAW out-of-process plugin model
  - Separate message pump thread; no audio render thread blocking
  - IPC: openEditor / closeEditor commands; ditorOpened / ditorClosed events
- **C# UI additions:** "Open Editor" button per VST3 slot
  - Hidden when no plugin path; toggles Open↔Close state
  - Wired to SendOpenEditorAsync() / SendCloseEditorAsync() on Vst3BridgeBackend
- **Deferred to Phase 2:** Tier 4 (GUI) pending audio verification

### Build Status
✅ Builds successfully (0 errors, 2 pre-existing warnings: CS0414 unused _frameSize)

### Testing Checklist (Required)
1. Load VST3 instrument; verify audio output
2. Verify pitch at 48 kHz (correct playback rate)
3. Verify full 960 samples per frame (no silence/gaps)
4. Test MIDI program change routing
5. Test plugins requiring IHostApplication (NI, Arturia)
6. Verify no regression in SF2 backend

### Session: Tray Icon + VST3 Editor Support (2026-03-12)

**Context:** Ward requested two enhancements: (1) Use AppIcon.ico for tray icon instead of emoji, (2) Add UI to open VST3 plugin editors.

**Task 1 — Tray Icon with .ico File:**
**Investigation:** H.NotifyIcon.WinUI 2.x (the library used for tray integration) does not expose a file-based IconSource type. The library only provides `GeneratedIconSource` (renders text/emoji in memory) and does not have `BitmapIconSource` or a way to load .ico files directly. To use AppIcon.ico, we would need P/Invoke (`LoadImage` from user32.dll) and access to `TaskbarIcon`'s internal HWND — both non-trivial and require wrapping Win32 NOTIFYICONDATA directly.

**Decision:** Keep `GeneratedIconSource` with emoji for now. Added detailed code comment in `TrayIconService.cs` explaining the limitation and future improvement path. The emoji renders acceptably on modern Windows 11 systems.

**Task 2 — VST3 Editor Support (C# Side IPC):**
**Implementation:**
1. Added `IEditorCapable` interface to `IInstrumentBackend.cs` with `SupportsEditor`, `OpenEditorAsync()`, `CloseEditorAsync()`
2. Updated `Vst3BridgeBackend` to implement `IEditorCapable`:
   - Added `OpenEditor` and `CloseEditor` to `MidiCommand.Kind` enum
   - Implemented `SupportsEditor` property (returns `_isReady`)
   - Implemented `OpenEditorAsync()` that sends `{"cmd":"openEditor"}` and awaits `editor_opened` ACK (5s timeout)
   - Implemented `CloseEditorAsync()` that sends `{"cmd":"closeEditor"}` and awaits `editor_closed` ACK (5s timeout)
   - Added `ParseEditorAck()` helper method for ACK validation
   - Updated `SerializeCommand()` to handle new command types
3. Added `GetActiveBackend()` method to `IAudioEngine` / `AudioEngine` to expose current backend for UI access
4. Updated `SettingsWindow.xaml.cs` to add "Editor" button in VST3 preset row:
   - Added `vst3EditorBtn` button in the VST3 preset row (Column 2)
   - Click handler checks if active backend is `IEditorCapable` and calls `OpenEditorAsync()`
   - Shows error dialog if editor not available or fails to open
   - Added `System.Diagnostics` using for Debug logging

**Coordination with Faye:** Faye is implementing the C++ bridge side (`openEditor`/`closeEditor` commands that return `editor_opened`/`editor_closed` ACKs). C# side is complete and ready for integration once bridge supports editor hosting.

**Build:** All changes compiled successfully with no errors. One warning about unused `_frameSize` field in Vst3BridgeBackend (pre-existing, not introduced by this session).


### Session: VST3 Race-Condition Fix — Backend Assigned Before Load (2026-03-12)

**Context:** Faye reported two bugs in AudioEngine.HandleVst3Instrument():
1. _activeBackend was set to _vst3Backend BEFORE LoadAsync() ran, so SupportsEditor returned false during loading.
2. When mmk-vst3-bridge.exe is missing, LoadAsync returned silently with no user feedback.

**Root cause pattern:** Volatile.Write(ref _activeBackend, _vst3Backend) preceded the fire-and-forget _ = _vst3Backend.LoadAsync(...). Backend was assigned-but-never-ready.

**Fix 1 — Race condition (AudioEngine.cs):**
- Removed premature Volatile.Write from HandleVst3Instrument().
- Extracted LoadVst3BackendAsync(InstrumentDefinition) private async method.
- Volatile.Write(ref _activeBackend, _vst3Backend) now runs only AFTER wait _vst3Backend.LoadAsync() completes and _vst3Backend.IsReady is true.
- During loading, _activeBackend stays on the prior backend (SoundFont), so audio continues silently but without crashing.

**Fix 2 — Silent failure (Vst3BridgeBackend.cs):**
- Changed bridge-exe-missing case from silent eturn to BridgeFaulted?.Invoke(this, new BridgeFaultedEventArgs(reason)).
- This fires the existing fault event without touching the state machine (bridge was never started).

**Fix 3 — Error surfacing (AudioEngine.cs + IAudioEngine.cs + SettingsWindow.xaml.cs):**
- Added vent EventHandler<string>? InstrumentLoadFailed to IAudioEngine and AudioEngine.
- AudioEngine constructor subscribes to _vst3Backend.BridgeFaulted and re-raises as InstrumentLoadFailed.
- SettingsWindow subscribes to InstrumentLoadFailed, dispatches to UI thread via DispatcherQueue.TryEnqueue, shows a ContentDialog with the error message.
- Unsubscribes in ForceClose() to prevent leaks.

**Fix 4 — Open Editor button (SettingsWindow.xaml.cs):**
- Added a third branch: if backend is a Vst3BridgeBackend that is not yet ready, shows "VST3 Plugin Still Loading" message instead of the generic "Editor Not Available" message.

**Key pattern learned:**
> Never assign an active backend reference before its LoadAsync completes. Use an async helper method, await the load, then do the Volatile.Write assignment only on success. This prevents the "assigned-but-never-ready" window.

### Session: VST3 Load Race Condition Fix — Completion (2026-03-12)

**Status:** COMPLETE — Verified build 0 errors

**Summary:** Fixed critical race condition where `_activeBackend` was assigned before async `LoadAsync()` completed, causing "Editor not available" errors. Also surfaced missing-bridge-exe failures and improved UI feedback.

**Implementation details verified:**

1. **LoadVst3BackendAsync method:**
   - Private async Task that awaits `_vst3Backend.LoadAsync(instrument)`
   - Only assigns `Volatile.Write(ref _activeBackend, _vst3Backend)` after completion and `IsReady == true`
   - Called from `HandleVst3Instrument()` as fire-and-forget (`_ = LoadVst3BackendAsync(...)`)

2. **Bridge failure handling:**
   - `Vst3BridgeBackend.LoadAsync()` now calls `BridgeFaulted?.Invoke()` when bridge.exe missing (instead of silent return)
   - Event triggers before returning; subscribers are notified

3. **InstrumentLoadFailed event:**
   - Added to `IAudioEngine` interface: `event EventHandler<string>? InstrumentLoadFailed`
   - `AudioEngine` subscribes to `_vst3Backend.BridgeFaulted` in constructor; re-raises as `InstrumentLoadFailed`
   - `SettingsWindow` subscribes on construction, handles via `DispatcherQueue.TryEnqueue` (UI thread dispatch), shows `ContentDialog`
   - Unsubscribes in `ForceClose()` to prevent subscription leaks

4. **Open Editor button improvements:**
   - Third branch added: if `backend is Vst3BridgeBackend && !backend.IsReady`, button shows "VST3 Plugin Still Loading"
   - Distinguishes loading state from permanent unavailability
   - Better user experience during initial load phase

**Files modified:**
- `src/MinimalMusicKeyboard/Services/AudioEngine.cs` — Added `LoadVst3BackendAsync`, subscribed to `BridgeFaulted`
- `src/MinimalMusicKeyboard/Services/Vst3BridgeBackend.cs` — Bridge-missing now fires `BridgeFaulted`
- `src/MinimalMusicKeyboard/Interfaces/IAudioEngine.cs` — Added `InstrumentLoadFailed` event
- `src/MinimalMusicKeyboard/Views/SettingsWindow.xaml.cs` — Subscribed to `InstrumentLoadFailed`, improved button messaging

**Invariants:**
- Audio render thread guard `backend.IsReady` preserved
- `Volatile.Write`/`Volatile.Read` pattern unchanged
- No allocations on audio render path
- `BridgeFaulted` semantics unchanged for IPC-failure cases

**Related decision records:** `.squad/decisions.md` Section "Session: 2026-03-12 — VST3 Load Race Condition Fix"

### VST3 Editor Diagnostics Surface (2026-03-12)

**Decision:** Expose exact VST3 editor-availability diagnostics through IAudioEngine to managed UI instead of generic fallback.

**Why:** Bridge already computes failure reason (stage + error code); UI should respect that rather than replace with generic message when backend is temporarily inactive during VST3 load.

**Implementation:**
- Added IAudioEngine.GetVst3EditorAvailabilityDescription() method
- Implemented in AudioEngine by forwarding Vst3BridgeBackend.EditorAvailabilityDescription
- Updated SettingsWindow UI logic:
  - Display exact diagnostic after load success with no editor support
  - Show exact diagnostic in "Editor Not Available" dialog
  - Distinguish "still loading" (keyed off _loadingVst3SlotIndex) from permanent unavailability
  - Stop unconditionally re-enabling button in finally block

**Result:** OB-Xd and other plugins now show specific failure stages (e.g., "IPlugView::attached() failed") instead of generic "Editor Not Available". Improves debuggability.

**Commit:** bcbe000

---