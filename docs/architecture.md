# Minimal Music Keyboard — Architecture Document

**Version:** 1.0 (Draft for Gren Review)
**Author:** Spike (Lead Architect)
**Date:** 2026-03-01
**Status:** DRAFT — Pending Gren's architectural review

---

## 1. Project Structure

```
MinimalMusicKeyboard/
├── MinimalMusicKeyboard.sln
├── docs/
│   └── architecture.md
├── src/
│   └── MinimalMusicKeyboard/
│       ├── MinimalMusicKeyboard.csproj
│       ├── App.xaml / App.xaml.cs          # WinUI3 entry point
│       ├── Program.cs                       # Main — single-instance gate, tray bootstrap
│       ├── Core/
│       │   ├── AppLifecycleManager.cs       # Startup/shutdown orchestration
│       │   └── SingleInstanceGuard.cs       # Mutex-based single-instance enforcement
│       ├── Midi/
│       │   ├── MidiDeviceService.cs         # MIDI device enumeration + message listener
│       │   ├── MidiMessageRouter.cs         # Parses messages, routes to audio or instrument switching
│       │   └── MidiDeviceInfo.cs            # DTO for discovered devices
│       ├── Audio/
│       │   ├── AudioEngine.cs               # Synthesis via MeltySynth, WASAPI output
│       │   └── SoundFontManager.cs          # Load/unload soundfont files
│       ├── Instruments/
│       │   ├── InstrumentCatalog.cs         # Maps instrument names → soundfont + bank/preset
│       │   ├── InstrumentDefinition.cs      # Config model per instrument
│       │   └── InstrumentSwitcher.cs        # Handles MIDI PC-triggered switching
│       ├── Tray/
│       │   ├── TrayIconService.cs           # Tray icon lifecycle + context menu
│       │   └── TrayMenuBuilder.cs           # Builds context menu (instruments, settings, exit)
│       ├── Settings/
│       │   ├── SettingsWindow.xaml / .cs     # On-demand WinUI3 settings page
│       │   ├── SettingsViewModel.cs         # MVVM view model
│       │   └── AppSettings.cs               # Settings model + persistence
│       └── Helpers/
│           └── DisposableExtensions.cs
└── tests/
    └── MinimalMusicKeyboard.Tests/
        ├── MinimalMusicKeyboard.Tests.csproj
        └── ...                              # Ed owns test strategy
```

**Namespaces** mirror folder structure: `MinimalMusicKeyboard.Core`, `MinimalMusicKeyboard.Midi`, `MinimalMusicKeyboard.Audio`, etc.

**Target framework:** `net8.0-windows10.0.22621.0` (Windows App SDK 1.5+)

---

## 2. Key Library Choices

### MIDI I/O: **NAudio.Midi**

| Option | Verdict |
|--------|---------|
| `Windows.Devices.Midi2` | Win11-only UWP/WinRT API. Promising but immature, limited community support, complex async enumeration. Too bleeding-edge for a must-work tool. |
| `RtMidi.NET` | Thin wrapper around native C++ RtMidi. Requires shipping native binaries, adds deployment complexity. |
| **`NAudio.Midi` ✓** | Battle-tested, pure managed, excellent Arturia compatibility, simple `MidiIn`/`MidiOut` API. Huge community. No native deps. Ships as a single NuGet. |

**Decision:** NAudio (specifically `NAudio.Midi` namespace). Proven reliability, trivial to enumerate devices, straightforward event-based message handling. Mark Heath maintains it actively.

### Audio Synthesis: **MeltySynth**

| Option | Verdict |
|--------|---------|
| NAudio SoundFont | NAudio can *read* SF2 but has no built-in synthesizer. You'd have to write your own voice allocator. No. |
| managed-midi + FluidSynth | FluidSynth is excellent but it's a native C library. P/Invoke or wrapper adds complexity, native binary management, potential memory issues across the managed/native boundary. |
| **`MeltySynth` ✓** | Pure C# SoundFont synthesizer. Zero native dependencies. ~3k lines, no allocations in hot path. SF2 support. Renders to float[] buffers that pipe directly to NAudio's WASAPI output. MIT licensed. |

**Decision:** MeltySynth for synthesis, NAudio's `WasapiOut` for audio output. This gives us a fully managed audio pipeline with no P/Invoke. Faye has final say on synthesis approach — this is my recommendation pending her review.

### System Tray: **H.NotifyIcon.WinUI**

| Option | Verdict |
|--------|---------|
| Hardcodet.NotifyIcon.Wpf | WPF-only. Wrong UI stack. |
| Custom (Shell_NotifyIcon P/Invoke) | Works but we'd be maintaining shell notification boilerplate ourselves. |
| **`H.NotifyIcon.WinUI` ✓** | Purpose-built for WinUI3/Windows App SDK. Supports context menus, tooltips, balloon notifications. Active maintenance. Handles the WinUI3 threading quirks for us. |

**Decision:** H.NotifyIcon.WinUI. It's the only real option for WinUI3 tray apps and it works well.

### Single-Instance: **Named Mutex**

| Option | Verdict |
|--------|---------|
| `Windows.ApplicationModel.SingleInstance` | Only works for packaged (MSIX) apps. We want to support unpackaged deployment too. |
| Named pipe | Overkill — we don't need IPC between instances, just rejection of the second. |
| **Named Mutex ✓** | Simple, reliable, works for both packaged and unpackaged. Standard Windows pattern. |

**Decision:** Named Mutex (`Global\MinimalMusicKeyboard`). If the mutex is already held, the second instance exits immediately. No IPC needed — we just block the duplicate.

### Audio Output: **NAudio WasapiOut (shared mode)**

WASAPI shared mode for lowest-latency managed audio output without exclusive device access. Users can still hear other apps. Buffer size: 50ms default (configurable in settings for latency-sensitive users).

---

## 3. Component Architecture

### 3.1 AppLifecycleManager

**Responsibility:** Orchestrates the entire app lifecycle — startup sequence, shutdown sequence, single-instance enforcement.

```csharp
public sealed class AppLifecycleManager : IDisposable
{
    // Owns (in construction order):
    private readonly SingleInstanceGuard _guard;
    private readonly AppSettings _settings;
    private readonly InstrumentCatalog _catalog;
    private readonly AudioEngine _audio;
    private readonly MidiDeviceService _midi;
    private readonly MidiMessageRouter _router;
    private readonly TrayIconService _tray;

    // Startup: guard → settings → catalog → audio → midi → router → tray
    // Shutdown: router → midi → audio → tray → guard (see Section 6 for rationale)
}
```

**Startup sequence:**
1. `SingleInstanceGuard` — acquire mutex or exit
2. `AppSettings` — load from JSON
3. `InstrumentCatalog` — build catalog from settings
4. `AudioEngine` — load default soundfont, initialize WASAPI output
5. `MidiDeviceService` — open configured MIDI input device
6. `MidiMessageRouter` — wire MIDI events → audio engine + instrument switcher
7. `TrayIconService` — show tray icon, build context menu

### 3.2 MidiDeviceService

```csharp
public sealed class MidiDeviceService : IDisposable
{
    // Wraps NAudio.Midi.MidiIn
    // Enumerates available MIDI input devices
    // Opens/closes device by index or name
    // Fires events: NoteOn, NoteOff, ControlChange, ProgramChange
    // Runs on dedicated background thread (NAudio's internal callback thread)

    event EventHandler<MidiNoteEventArgs> NoteReceived;
    event EventHandler<MidiControlEventArgs> ControlChangeReceived;
    event EventHandler<MidiProgramEventArgs> ProgramChangeReceived;
}
```

**IDisposable contract:** Stops listening, closes MidiIn handle, unsubscribes all events. Must be called before AudioEngine disposal.

**MIDI device disconnect handling (Gren — REQUIRED):**
NAudio's `MidiIn` wraps Win32 `midiInOpen`/`midiInClose`. If the USB MIDI device is physically disconnected while listening, the Win32 callback thread terminates and the handle becomes invalid. `MidiDeviceService` MUST:
1. Catch and handle `MmException` or `InvalidOperationException` from NAudio on device disconnect — never crash.
2. Enter a `Disconnected` state and fire a `DeviceDisconnected` event (for UI notification via tray tooltip).
3. Dispose the stale `MidiIn` instance cleanly.
4. Auto-reconnect can be deferred to post-MVP, but the architecture must support it: a periodic timer (e.g., every 3 seconds) re-enumerates devices and attempts reconnection if the configured device reappears.
5. During the disconnected state, all MIDI event handlers remain subscribed (to the service, not to `MidiIn`) so reconnection is seamless — just open a new `MidiIn` and start listening.

### 3.3 AudioEngine

```csharp
public sealed class AudioEngine : IDisposable
{
    // Owns: MeltySynth.Synthesizer, NAudio.Wave.WasapiOut
    // Loads SF2 files via MeltySynth.SoundFont
    // Processes NoteOn/NoteOff by calling Synthesizer.NoteOn/NoteOff
    // Renders audio in WasapiOut's callback on audio thread
    // Handles instrument preset changes (bank select + program change)

    void NoteOn(int channel, int key, int velocity);
    void NoteOff(int channel, int key);
    void SetPreset(int channel, int bank, int preset);
    void LoadSoundFont(string path);
}
```

**Threading:** Audio rendering runs on WASAPI's callback thread. `NoteOn`/`NoteOff` are called from MIDI callback thread — MeltySynth's `Synthesizer` is thread-safe for these operations (note events are lock-free internally). SoundFont loading is a blocking operation that must happen on a background thread, not the audio callback.

### 3.4 InstrumentCatalog

```csharp
public sealed class InstrumentCatalog
{
    // Loaded from settings JSON
    // Maps: instrument name → (soundfont path, bank number, preset number)
    // Maps: MIDI Program Change number → instrument name
    // Provides ordered list for tray menu display

    IReadOnlyList<InstrumentDefinition> Instruments { get; }
    InstrumentDefinition? GetByProgramChange(int programNumber);
    InstrumentDefinition? GetByName(string name);
    InstrumentDefinition CurrentInstrument { get; }
}
```

### 3.5 TrayIconService

```csharp
public sealed class TrayIconService : IDisposable
{
    // Owns: H.NotifyIcon.TaskbarIcon
    // Creates tray icon with context menu:
    //   - Current instrument (display only)
    //   - Instrument submenu (switch via click)
    //   - "Settings..." (opens SettingsWindow)
    //   - "Exit"
    // Handles "Exit" → signals AppLifecycleManager to begin shutdown
    // Handles "Settings..." → creates SettingsWindow on demand

    event EventHandler ExitRequested;
    event EventHandler SettingsRequested;
}
```

### 3.6 SettingsWindow

**On-demand creation pattern:** The SettingsWindow is NOT kept alive when closed. It is created fresh each time the user clicks "Settings..." and disposed when closed.

```csharp
// In TrayIconService or AppLifecycleManager:
private SettingsWindow? _activeSettingsWindow;

private void OnSettingsRequested()
{
    if (_activeSettingsWindow is not null)
    {
        _activeSettingsWindow.Activate(); // bring existing to front
        return;
    }

    _activeSettingsWindow = new SettingsWindow(_settings, _catalog, _midi);
    _activeSettingsWindow.Closed += (_, _) =>
    {
        // Explicitly unsubscribe any service events the window holds
        _activeSettingsWindow = null; // release reference for GC
    };
    _activeSettingsWindow.Activate();
}

// In Dispose(): if _activeSettingsWindow is not null, close it
```

**Why:** WinUI3 windows hold significant resources (XAML trees, COM objects). Keeping a hidden window alive defeats our <50MB idle target. Creating on demand adds ~200ms of perceived latency — acceptable for a settings dialog opened rarely.

**IMPORTANT (Gren):** The SettingsWindow MUST explicitly unsubscribe from any events on long-lived services (MidiDeviceService, AudioEngine, InstrumentCatalog) in its Closed handler. WinUI3 windows are COM-backed — relying on GC alone risks both event handler leaks and COM reference leaks. The `_activeSettingsWindow` pattern above also prevents duplicate windows and ensures Dispose() during shutdown can close any open settings window.

---

## 4. Data Flow

### MIDI Message Path

```
┌──────────────┐     NAudio callback     ┌──────────────────┐
│  MIDI Device  │ ──────────────────────► │ MidiDeviceService │
│ (Arturia 88)  │      (background        │  (event parsing)  │
└──────────────┘       thread)            └────────┬─────────┘
                                                   │
                                          typed events (NoteOn,
                                          CC, PC, etc.)
                                                   │
                                                   ▼
                                         ┌──────────────────┐
                                         │ MidiMessageRouter │
                                         │  (routing logic)  │
                                         └───┬──────────┬───┘
                                             │          │
                                    Note/CC  │          │ Program Change
                                    events   │          │ (instrument switch)
                                             ▼          ▼
                                   ┌─────────────┐  ┌──────────────────┐
                                   │ AudioEngine  │  │ InstrumentSwitcher│
                                   │ (MeltySynth) │  │ (catalog lookup)  │
                                   └──────┬──────┘  └────────┬─────────┘
                                          │                   │
                                          │          SetPreset(bank, preset)
                                          │◄──────────────────┘
                                          │
                                          ▼
                                   ┌─────────────┐
                                   │  WasapiOut   │
                                   │ (audio out)  │
                                   └─────────────┘
```

**Latency budget:**
- MIDI USB polling: ~1ms
- NAudio callback dispatch: <1ms
- MidiMessageRouter parse + route: <0.1ms
- MeltySynth render to buffer: ~2-5ms
- WASAPI shared mode buffer: ~50ms (configurable)
- **Total:** ~55ms typical (dominated by WASAPI buffer)

---

## 5. Threading Model

| Thread | Owner | Components | Notes |
|--------|-------|-----------|-------|
| **UI thread** (STA) | WinUI3 / DispatcherQueue | TrayIconService, SettingsWindow, AppLifecycleManager | Only used for tray menu and settings UI |
| **MIDI callback thread** | NAudio.Midi.MidiIn | MidiDeviceService events → MidiMessageRouter | NAudio fires events on a dedicated Win32 callback thread. Keep handlers fast — no blocking. |
| **Audio render thread** | NAudio.Wave.WasapiOut | AudioEngine (MeltySynth.Synthesizer.Render) | WASAPI calls our ISampleProvider.Read() on its own thread. MeltySynth renders here. |
| **Background thread (occasional)** | Task.Run | SoundFont loading, settings save | Heavy I/O operations dispatched off UI thread |

### Synchronization Boundaries

1. **MIDI thread → AudioEngine:** MeltySynth `NoteOn`/`NoteOff`/`NoteOffAll` are called from the MIDI callback while the audio thread renders. **Gren note:** MeltySynth is not explicitly documented as thread-safe by its author. Before implementation, verify via code inspection that concurrent NoteOn + Render is safe. If not, add a slim lock (`SpinLock` or `lock` with minimal scope) around NoteOn/NoteOff/Render. Profile to ensure it doesn't add latency. Fallback: use a lock-free concurrent queue to buffer note events, consumed by the audio thread in Render().

2. **MIDI thread → InstrumentSwitcher → AudioEngine.SetPreset:** The `SetPreset` call changes the active bank/preset. This modifies synthesizer state. We use a lightweight `lock` around preset changes only (not per-note). Preset changes are rare (user-initiated), so contention is negligible.

3. **MIDI thread → UI thread (tray update):** When instrument switches, we update the tray tooltip. Use `DispatcherQueue.TryEnqueue()` to marshal to UI thread. Fire-and-forget — MIDI thread must not wait.

4. **SoundFont loading:** Blocks a background thread. While loading, the old soundfont remains active. Swap is atomic (replace the `Synthesizer` instance reference). We call `NoteOffAll()` before swap to avoid hanging notes.

   **Gren note — REQUIRED pattern for Synthesizer swap:**
   The audio render callback MUST copy the Synthesizer reference to a local variable before using it. This prevents the GC from collecting the old instance mid-render:
   ```csharp
   // In ISampleProvider.Read() — audio callback:
   var synth = Volatile.Read(ref _synthesizer); // snapshot the reference
   synth.Render(buffer, offset, count);
   // Old synth stays alive as long as this local ref exists
   ```
   The swap site must use `Volatile.Write(ref _synthesizer, newSynth)` to ensure visibility across threads. After swap, null out all references to the old Synthesizer and consider `GC.Collect(2, GCCollectionMode.Optimized, false)` to reclaim the old SoundFont memory promptly (justified here — SF2 files can be 10-50MB).

---

## 6. Disposal Chain

Shutdown is triggered by `TrayIconService.ExitRequested` or process termination signal.

**Exact ordered shutdown sequence:**

```
1. MidiMessageRouter.Dispose()
   → Unsubscribes from MidiDeviceService events
   → Ensures no new messages are routed

2. MidiDeviceService.Dispose()
   → Calls MidiIn.Stop()
   → Calls MidiIn.Dispose()
   → MIDI callback thread terminates
   → No more MIDI events can fire

3. AudioEngine.Dispose()
   → Calls Synthesizer.NoteOffAll() (silence all voices)
   → Calls WasapiOut.Stop()
   → Calls WasapiOut.Dispose()
   → Audio render thread terminates
   → Nulls Synthesizer reference (SoundFont byte arrays become GC-eligible)
   → Note: SoundFont file loading must use `using` on FileStream — file handle
     must not remain open after load completes

4. TrayIconService.Dispose()
   → Removes tray icon via TaskbarIcon.Dispose()
   → Icon disappears from notification area

5. SingleInstanceGuard.Dispose()
   → Releases named mutex

6. Application.Exit()
```

**Why this order:**
- MIDI stops first → no new note events can arrive at a disposed audio engine
- Audio stops second → all voices silenced, output device released
- Tray stops third → icon removed (user sees app is gone)
- Mutex last → another instance can now start

**Guard against partial disposal:** Each `Dispose()` is wrapped in try/catch. A failure in step 2 must not prevent steps 3-6. Log errors but continue.

---

## 7. Settings Persistence

### Storage Location

```
%LOCALAPPDATA%\MinimalMusicKeyboard\settings.json
```

Using `Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)`. Not roaming — MIDI device names are machine-specific.

### Schema

```json
{
  "version": 1,
  "midi": {
    "inputDeviceName": "Arturia KeyLab 88 MkII",
    "autoConnect": true,
    "reconnectOnDisconnect": true
  },
  "audio": {
    "wasapiBufferMs": 50,
    "masterVolume": 0.8
  },
  "instruments": {
    "defaultInstrument": "Grand Piano",
    "catalog": [
      {
        "name": "Grand Piano",
        "soundFontPath": "soundfonts\\GeneralUser.sf2",
        "bank": 0,
        "preset": 0,
        "programChangeNumber": 0
      },
      {
        "name": "Electric Piano",
        "soundFontPath": "soundfonts\\GeneralUser.sf2",
        "bank": 0,
        "preset": 4,
        "programChangeNumber": 4
      }
    ]
  },
  "startup": {
    "runOnWindowsStartup": false,
    "startMinimized": true
  }
}
```

**Persistence strategy:**
- Load on startup (deserialize with `System.Text.Json`)
- Save on settings window close (if changed)
- No auto-save timer — settings change rarely
- If file is missing or corrupt, create with sensible defaults
- Schema version field for future migration

### SoundFont File Location

SoundFonts stored in app directory under `soundfonts/`. Paths in settings are relative to app root. Users can also specify absolute paths.

---

## 8. Instrument Switching via MIDI

### Trigger: **MIDI Program Change (PC) messages**

**Why Program Change:**
- It's the MIDI-standard way to switch instruments. Every MIDI controller supports it.
- The Arturia KeyLab 88 MkII can send PC messages from its pads/buttons (configurable via Arturia MIDI Control Center).
- No ambiguity — PC messages have a single purpose in the MIDI spec.
- CC messages are better reserved for expression (mod wheel, sustain, volume).

### How It Works

1. User configures `programChangeNumber` per instrument in settings (0-127)
2. `MidiMessageRouter` receives a PC message from `MidiDeviceService`
3. Router looks up the program number in `InstrumentCatalog.GetByProgramChange(pc)`
4. If found, `InstrumentSwitcher` calls `AudioEngine.SetPreset(channel, bank, preset)`
5. Tray tooltip updates to show new instrument name
6. If no mapping exists for that PC number, the message is ignored (no error)

### Channel Handling

All operations on **MIDI Channel 1** by default (configurable). The Arturia KeyLab sends on Channel 1 unless reconfigured. Multi-channel support is a future consideration, not MVP.

### Bank Select Support

For soundfonts with >128 presets, we support the standard Bank Select flow:
- CC #0 (Bank Select MSB) → CC #32 (Bank Select LSB) → PC
- The router accumulates bank select messages and applies them with the next PC

---

## Appendix A: NuGet Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| `Microsoft.WindowsAppSDK` | 1.5+ | WinUI3 runtime |
| `NAudio` | 2.2+ | MIDI input + WASAPI audio output |
| `MeltySynth` | 2.3+ | SF2 soundfont synthesis (pure C#) |
| `H.NotifyIcon.WinUI` | 2.1+ | System tray icon for WinUI3 |
| `Microsoft.Extensions.Hosting` | 8.0+ | DI container + hosted services (optional, evaluate weight) |

**Note on DI:** We may not need `Microsoft.Extensions.Hosting`. The component graph is small enough for manual construction in `AppLifecycleManager`. Evaluate during implementation — if the dependency adds >5MB to idle memory, skip it and wire by hand.

## Appendix B: Memory Budget

| Component | Estimated Idle Memory |
|-----------|-----------------------|
| WinUI3 runtime (no visible window) | ~15-20MB |
| NAudio MidiIn (listening) | ~1MB |
| MeltySynth Synthesizer (loaded SF2) | ~5-15MB (depends on soundfont) |
| WasapiOut buffers | ~1MB |
| H.NotifyIcon tray | ~2MB |
| App code + CLR overhead | ~5MB |
| **Total estimated** | **~29-44MB** ✓ |

Target is <50MB idle. Budget allows headroom for a reasonably-sized General MIDI soundfont (~10MB).

## Appendix C: Open Questions for Gren Review

1. **DI Container:** Manual wiring vs `Microsoft.Extensions.DependencyInjection` — weight vs testability tradeoff. Leaning manual.
2. **MIDI Reconnect:** Should `MidiDeviceService` poll for device reconnection, or should the user manually reconnect via settings? Polling adds complexity but improves UX for USB disconnects.
3. **Multiple SoundFonts:** MVP loads one SF2 at a time. Should the catalog support instruments from different SF2 files simultaneously? This affects `AudioEngine` design (one Synthesizer per SF2 vs one shared).
4. **Packaged vs Unpackaged:** MSIX packaging gives us auto-start and clean uninstall, but adds deployment friction. Start unpackaged?

---

*This document is subject to revision after Gren's review. Faye should validate the MeltySynth + NAudio audio pipeline assumptions. Ed should review disposal chain for testability.*

---

## Gren's Review

**Reviewer:** Gren (Supporting Architect)
**Date:** 2026-03-01
**Status:** ⚠️ APPROVED WITH REQUIRED CHANGES

### Verdict

The architecture is solid overall. Spike has made good library choices (fully managed pipeline, no P/Invoke), the component structure is clean, and the disposal chain is well-thought-out. The design can proceed to implementation once the required changes below are addressed.

### Changes Already Applied to This Document

The following concerns were significant enough that I edited the relevant sections directly:

1. **Section 3.1 — Disposal order comment was wrong.** The comment said "reverse order" (tray first) but Section 6 correctly specifies router→midi→audio→tray→guard. Fixed the comment to match Section 6.

2. **Section 3.6 — SettingsWindow disposal pattern was unsafe.** The original "fire and forget, let GC collect" pattern for WinUI3 windows risks event handler leaks and COM reference leaks. Replaced with tracked `_activeSettingsWindow` pattern that explicitly nulls the reference on close and can be force-closed during shutdown.

3. **Section 3.2 — MIDI device disconnect handling was missing.** The settings schema references `reconnectOnDisconnect` but the architecture had no design for it. Added required disconnect handling: catch exceptions, enter Disconnected state, fire event, dispose stale handle. Auto-reconnect architecture is specified but implementation can be deferred.

4. **Section 5 — Synthesizer swap pattern was underspecified.** "Replace the reference" is not thread-safe without the correct pattern. Added required `Volatile.Read`/`Volatile.Write` pattern with local variable snapshot in the audio callback.

5. **Section 5 — MeltySynth thread safety claim was unverified.** Changed from "no lock needed" to "verify thread safety before implementation, add lock if needed."

6. **Section 6 — SoundFont disposal was vague.** Changed "resources to be collected" to explicit nulling of reference plus requirement that file loading uses `using` on FileStream.

### Accepted Decisions (No Changes Needed)

- **Library choices (NAudio, MeltySynth, H.NotifyIcon.WinUI):** All sound. Fully managed pipeline eliminates P/Invoke memory risks. NAudio is battle-tested for MIDI. MeltySynth's zero-allocation render path is ideal for real-time audio.
- **Named Mutex for single-instance:** Correct and simple. Works packaged and unpackaged.
- **WASAPI shared mode at 50ms:** Reasonable default. Shared mode is correct for a background app.
- **Program Change for instrument switching:** Correct use of MIDI spec. The bank select accumulation pattern (CC#0 → CC#32 → PC) follows the MIDI standard. Note: bank select values are "sticky" per MIDI spec — no timeout needed.
- **Settings persistence strategy:** Load-once, save-on-close is appropriate for rarely-changed settings. Schema versioning is good forward planning.
- **Manual DI wiring:** Agree. The component graph is small (~7 objects). `Microsoft.Extensions.Hosting` would add memory overhead with no real benefit. Wire by hand in `AppLifecycleManager`.
- **On-demand SettingsWindow creation:** Correct trade-off. 200ms creation cost is invisible for a rarely-opened dialog. Keeps idle memory low.

### Additional Concerns (Not Blocking, But Track These)

1. **H.NotifyIcon ghost icon on crash:** If the app crashes without `TaskbarIcon.Dispose()`, the tray icon remains as a ghost until the user hovers over it. This is a Windows shell limitation, not fixable in code. Document this as a known issue for users. Consider: on startup, if the app was not cleanly shut down last time (detect via a sentinel file or registry key), log a warning.

2. **SoundFont switching across different SF2 files:** The architecture covers `SetPreset` (same SF2, different bank/preset) but not switching to an instrument from a *different* SF2 file. This requires `LoadSoundFont` on a background thread. During the load (~100-500ms for a 10MB SF2), incoming MIDI notes should continue playing on the old instrument. The `Volatile.Read` local-reference pattern handles this correctly — document this flow explicitly for implementers.

3. **Memory spike during SF2 switch:** When switching between different SoundFont files, both the old and new SF2 data will be in memory simultaneously during the transition. For large soundfonts (50MB+), this could temporarily push past the 50MB target. Accept this as transient. The `GC.Collect` hint after swap (already documented above) mitigates this.

4. **InstrumentCatalog.CurrentInstrument mutability:** This property is read from the UI thread (tray tooltip) and written from the MIDI thread (via InstrumentSwitcher). Use `Volatile.Read`/`Volatile.Write` or make it a thread-safe property.

5. **Startup with missing MIDI device:** If the configured MIDI device is not connected at startup, the app should start successfully in Disconnected state (tray shows "No MIDI device") rather than failing. This falls out naturally from the disconnect handling design added above.

### Responses to Appendix C Open Questions

1. **DI Container:** Manual wiring. Confirmed. No objections.
2. **MIDI Reconnect:** Graceful disconnect handling is REQUIRED for MVP (added to Section 3.2). Auto-reconnect polling can be post-MVP but the architecture now supports it.
3. **Multiple SoundFonts:** One at a time for MVP. The Volatile swap pattern supports this cleanly. One Synthesizer per SF2 would be wasteful — stick with swap.
4. **Packaged vs Unpackaged:** Start unpackaged. Named Mutex handles single-instance. MSIX can come later if Windows Store distribution is desired.

### Required Changes Summary (Must Be Done Before Implementation)

| # | Section | Change | Severity |
|---|---------|--------|----------|
| 1 | 3.2 MidiDeviceService | Implement disconnect handling (catch, state machine, dispose stale handle) | **High** — crash on USB disconnect is unacceptable for an always-on app |
| 2 | 5. Threading | Verify MeltySynth thread safety or add synchronization | **High** — concurrent NoteOn + Render without verified safety is a potential corruption/crash vector |
| 3 | 5. Threading | Use `Volatile.Read`/`Volatile.Write` for Synthesizer swap pattern | **Medium** — without volatile, JIT may cache the field read and the audio thread could use a stale reference |
| 4 | 3.6 SettingsWindow | Use tracked window pattern; explicitly unsubscribe service events on close | **Medium** — event handler leak on a COM-backed WinUI3 window is a slow memory leak |
| 5 | AudioEngine | Use `using` on FileStream when loading SF2 files | **Low** — file handle leak, but only during load operations |
| 6 | InstrumentCatalog | Make `CurrentInstrument` thread-safe (Volatile or lock) | **Low** — read-write from different threads |
