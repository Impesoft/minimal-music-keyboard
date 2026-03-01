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
