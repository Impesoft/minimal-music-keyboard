# VST3 Instrument Support — Architecture Proposal

**Version:** 1.1  
**Author:** Spike (Lead Architect)  
**Date:** 2026-07-17 (revised 2026-07-18)  
**Status:** REVISED — Pending Gren re-review v1.1  
**Requested by:** Ward Impe  

---

## 0. Executive Summary

Add VST3 instrument plugin support alongside existing SF2 soundfonts. The design introduces:

1. **`InstrumentType` enum** — discriminates SF2 vs VST3 in `InstrumentDefinition`
2. **`IInstrumentBackend`** — new abstraction for pluggable audio synthesis backends
3. **`AudioEngine` as mixer host** — owns WASAPI output; backends are `ISampleProvider` slots
4. **Out-of-process VST3 bridge** — recommended hosting approach for crash isolation

All changes are backward-compatible. Existing `instruments.json` files with no `type` field default to `SoundFont`. The SF2 path continues to use MeltySynth exactly as today.

---

## 1. InstrumentDefinition — Type Discriminator

### 1.1 New Enum

```csharp
// Models/InstrumentType.cs
namespace MinimalMusicKeyboard.Models;

public enum InstrumentType
{
    /// <summary>SF2 soundfont instrument (default for backward compatibility).</summary>
    SoundFont = 0,

    /// <summary>VST3 plugin instrument.</summary>
    Vst3 = 1,
}
```

### 1.2 Extended InstrumentDefinition

```csharp
public sealed record InstrumentDefinition
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("displayName")]
    public required string DisplayName { get; init; }

    /// <summary>Discriminator. Defaults to SoundFont when absent in JSON (backward compat).</summary>
    [JsonPropertyName("type")]
    public InstrumentType Type { get; init; } = InstrumentType.SoundFont;

    // ── SF2-specific ──────────────────────────────────────────────────
    [JsonPropertyName("soundFontPath")]
    public string SoundFontPath { get; init; } = "[SoundFont Not Configured]";

    [JsonPropertyName("bankNumber")]
    public int BankNumber { get; init; }

    // ── VST3-specific ─────────────────────────────────────────────────
    /// <summary>Path to the .vst3 bundle directory (e.g., "C:\VSTPlugins\Diva.vst3").</summary>
    [JsonPropertyName("vst3PluginPath")]
    public string? Vst3PluginPath { get; init; }

    /// <summary>Optional .vstpreset file for initial state.</summary>
    [JsonPropertyName("vst3PresetPath")]
    public string? Vst3PresetPath { get; init; }

    // ── Shared ────────────────────────────────────────────────────────
    /// <summary>MIDI program number (0–127). Used for PC routing regardless of type.</summary>
    [JsonPropertyName("programNumber")]
    public int ProgramNumber { get; init; }

    [JsonPropertyName("category")]
    public string Category { get; init; } = string.Empty;
}
```

### 1.3 JSON Backward Compatibility

`System.Text.Json` will deserialize missing `"type"` fields as `InstrumentType.SoundFont` (the enum's default value 0). No custom converter needed. The `SoundFontPath` property keeps its `required` keyword removed — it now has a default value, so old JSON without a `type` key and new JSON with `type: "Vst3"` both deserialize cleanly.

**Breaking change note:** `SoundFontPath` changes from `required` to optional-with-default. This is a source-breaking change for code using the `new InstrumentDefinition { ... }` syntax without specifying `SoundFontPath`. The fix is to ensure all construction sites set `SoundFontPath` explicitly or rely on the default. This is a small, contained change.

---

## 2. IInstrumentBackend — Abstraction Layer

### 2.1 Interface Definition

```csharp
// Interfaces/IInstrumentBackend.cs
using NAudio.Wave;
using MinimalMusicKeyboard.Models;

namespace MinimalMusicKeyboard.Interfaces;

/// <summary>
/// Abstraction for an audio synthesis backend (SF2 soundfont, VST3 plugin, etc.).
/// Each backend produces audio as an ISampleProvider that the AudioEngine mixer consumes.
///
/// Threading contract:
///   - NoteOn/NoteOff/SetProgram are called from the AUDIO RENDER THREAD ONLY.
///     AudioEngine drains its ConcurrentQueue&lt;MidiCommand&gt; during ReadSamples()
///     and dispatches to the active backend on the audio thread. The MIDI callback
///     thread never calls backend methods directly.
///   - The ISampleProvider.Read() returned by GetSampleProvider() is called on the WASAPI
///     audio render thread. This is the audio hot path — no blocking, no allocation.
///   - LoadAsync is called from a background Task. It may take seconds (loading SF2 or
///     initializing a VST3 plugin). The backend must not produce audio until loading completes.
/// </summary>
public interface IInstrumentBackend : IDisposable
{
    /// <summary>Human-readable name for diagnostics (e.g., "MeltySynth SF2", "VST3: Diva").</summary>
    string DisplayName { get; }

    /// <summary>The instrument type this backend handles.</summary>
    InstrumentType BackendType { get; }

    /// <summary>
    /// Load/initialize the backend for the given instrument definition.
    /// For SF2: loads the soundfont file and creates the synthesizer.
    /// For VST3: loads the plugin DLL, initializes COM interfaces, applies preset.
    /// May be called multiple times (instrument switch within same type).
    /// </summary>
    Task LoadAsync(InstrumentDefinition instrument, CancellationToken cancellation = default);

    /// <summary>Whether the backend has successfully loaded and can produce audio.</summary>
    bool IsReady { get; }

    /// <summary>
    /// Returns the ISampleProvider that produces audio for this backend.
    /// The AudioEngine mixer will call Read() on this from the WASAPI render thread.
    /// Must return a valid provider even before LoadAsync completes (output silence).
    /// Format: IEEE float, 48 kHz, stereo.
    /// </summary>
    ISampleProvider GetSampleProvider();

    /// <summary>MIDI note-on. Called from the audio render thread only.</summary>
    void NoteOn(int channel, int note, int velocity);

    /// <summary>MIDI note-off. Called from the audio render thread only.</summary>
    void NoteOff(int channel, int note);

    /// <summary>Silence all voices immediately.</summary>
    void NoteOffAll();

    /// <summary>MIDI program/bank change within this backend's domain.</summary>
    void SetProgram(int channel, int bank, int program);
}
```

### 2.2 Key Design Decisions

**Q: Does the backend own WASAPI output?**  
**A: No.** The `AudioEngine` owns the single WASAPI output device and a `MixingSampleProvider`. Each backend registers its `ISampleProvider` with the mixer. This design:
- Avoids multiple WASAPI devices competing for the audio endpoint
- Allows future multi-backend layering (e.g., SF2 pad layer + VST3 piano)
- Keeps device enumeration/switching in one place
- Means backends are pure audio producers — no platform coupling

**Q: Does the backend handle its own command queuing?**  
**A: No.** The `AudioEngine` owns the single `ConcurrentQueue<MidiCommand>`. The audio thread drains the queue in `ReadSamples()` and dispatches `NoteOn`/`NoteOff`/`SetProgram` to the active backend. **The MIDI callback thread never calls backend methods directly. All backend method calls happen on the audio thread via the command queue drain in `ReadSamples()`.** For the Vst3BridgeBackend, IPC commands are batched and sent over the named pipe during the backend's `Read()` call — still on the audio thread.

---

## 3. VST3 Hosting Approach — Recommendation

### 3.1 Options Evaluated

| Option | Latency | Crash Isolation | Complexity | Native Deps | Notes |
|--------|---------|-----------------|------------|-------------|-------|
| A. Direct COM P/Invoke | ~0ms | ❌ None | Very High | LoadLibrary | Hand-roll COM vtable interop for ~15 interfaces |
| B. VST.NET / NuGet host | ~0ms | ❌ None | Low | Managed wrapper | No mature, maintained .NET VST3 host library exists (2026) |
| C. Out-of-process bridge | ~2-5ms | ✅ Full | Medium | Bridge exe | Spawn native C++ process, communicate via named pipe |
| D. CLAP alternative | ~0ms | ❌ None | Medium | LoadLibrary | Better C ABI, but users have VST3 plugins, not CLAP |

### 3.2 Recommendation: Option C — Out-of-Process VST3 Bridge

**Primary rationale:** This app runs as a tray-resident background process for hours or days. A VST3 plugin crash (access violation, heap corruption, infinite loop) must not bring down the host. Option C is the only approach that provides true crash isolation.

**Latency analysis:**  
- Named pipe round-trip on localhost: ~0.1ms per message
- Audio block transfer (960 samples × 2ch × 4 bytes = 7.5KB at 48kHz/20ms): ~0.05ms memcpy
- Total added latency: **≤1ms per audio block** (not 5ms — that estimate assumed TCP sockets)
- With shared memory for the audio buffer instead of pipe transfer: **<0.5ms**
- This is well within the existing 20ms WASAPI buffer. The user will not perceive a difference.

**Architecture:**

```
┌─────────────────────────────────────────────────────────┐
│  MinimalMusicKeyboard.exe (managed, .NET 8)             │
│                                                          │
│  AudioEngine                                             │
│    ├── MixingSampleProvider                              │
│    │     ├── SoundFontBackend (ISampleProvider)          │
│    │     └── Vst3BridgeBackend (ISampleProvider) ──────┐ │
│    └── WasapiOut                                       │ │
│                                                        │ │
│  Vst3BridgeBackend                                     │ │
│    ├── Named pipe: \\.\pipe\mmk-vst3-{pid}             │ │
│    ├── Shared memory: mmk-vst3-audio-{pid}             │ │
│    └── Watchdog timer (restart on crash)               │ │
└────────────────────────────────────────────────────┬────┘
                                                     │ IPC
┌────────────────────────────────────────────────────┴────┐
│  mmk-vst3-bridge.exe (native C++, ~200KB)               │
│                                                          │
│    ├── LoadLibrary("plugin.vst3")                        │
│    ├── IPluginFactory → IComponent → IAudioProcessor     │
│    ├── Receives MIDI commands via named pipe              │
│    ├── Writes float PCM blocks to shared memory          │
│    └── Heartbeat ping every 500ms                        │
│                                                          │
│  Crash → process exits → host detects broken pipe        │
│       → host shows "plugin crashed" in tray tooltip      │
│       → user can retry via settings UI                   │
└─────────────────────────────────────────────────────────┘
```

**Bridge protocol (named pipe, binary):**

| Message | Direction | Payload |
|---------|-----------|---------|
| `INIT` | Host → Bridge | Plugin path, sample rate, block size, preset path |
| `READY` | Bridge → Host | Success + plugin name, or error string |
| `MIDI` | Host → Bridge | Channel, status, data1, data2 (4 bytes) |
| `RENDER` | Host → Bridge | Block index (trigger; bridge writes to shared mem) |
| `AUDIO` | Bridge → Host | (implicit — read from shared memory ring buffer) |
| `PING` | Host → Bridge | Heartbeat |
| `PONG` | Bridge → Host | Heartbeat response |
| `SHUTDOWN` | Host → Bridge | Clean exit request |

**Shared memory layout** (memory-mapped file):

```
Offset 0x0000: uint32 writeIndex    (bridge writes, host reads)
Offset 0x0004: uint32 readIndex     (host writes, bridge reads)
Offset 0x0008: float[blockSize * 2] audioBuffer  (interleaved stereo)
```

Double-buffered: bridge writes block N+1 while host reads block N.

**IPC resource ownership:** The host (`Vst3BridgeBackend`) creates both the `MemoryMappedFile` and the `NamedPipeServerStream`. The bridge process connects to both as a client (`NamedPipeClientStream` + `MemoryMappedFile.OpenExisting`). This ensures the host retains valid handles on bridge crash. A crashed bridge does not orphan IPC resources. On restart, the new bridge process connects to the existing host-owned resources — no re-creation needed. The naming scheme `mmk-vst3-audio-{hostPid}` uses the **host's** PID (not the bridge's), so the name is stable across bridge restarts.

### 3.3 Why Not In-Process (Option A)?

While in-process COM interop would eliminate the bridge process, it violates core project constraints:

1. **"Zero native deps" philosophy** — Option A requires hand-rolling COM vtable layouts for `IPluginFactory3`, `IComponent`, `IAudioProcessor`, `IEditController`, `IConnectionPoint`, plus parameter queuing. That's ~2000 lines of unsafe C# interop code with no safety net.

2. **Stability risk** — A badly-behaved VST3 plugin can corrupt the managed heap, cause a `StackOverflowException`, or deadlock on the audio thread. In a tray app that runs for days, this is unacceptable.

3. **Debugging nightmare** — Mixed managed+native stack traces through COM vtables are extremely difficult to diagnose in production.

### 3.4 Phased Delivery

The bridge is the most complex component. Recommended phased approach:

| Phase | Scope | Deliverable |
|-------|-------|-------------|
| **Phase 1** | `IInstrumentBackend` abstraction + refactored `AudioEngine` | SF2 works through new backend interface. No VST3 yet. |
| **Phase 2** | `Vst3BridgeBackend` (managed side) + bridge protocol | Managed IPC client, mock bridge for testing |
| **Phase 3** | `mmk-vst3-bridge.exe` (native C++) | Actual VST3 COM hosting, audio rendering |
| **Phase 4** | Settings UI for VST3 instrument configuration | Browse .vst3, select presets, test audio |

Phase 1 is the critical architectural change. Phases 2-4 can be delivered incrementally.

---

## 4. AudioEngine Refactoring

### 4.1 Approach: Option C — Backend-Pluggable Mixer Host

The `AudioEngine` becomes a **mixer host** that owns:
- The `WasapiOut` output device (unchanged)
- A `MixingSampleProvider` that sums audio from all active backends
- The `ConcurrentQueue<MidiCommand>` for thread-safe MIDI dispatch
- Volume control (applied post-mix)

Backends are pure `ISampleProvider` audio sources. The engine routes MIDI commands to the active backend.

**Why not Option A (full mixer) or Option B (separate engines)?**

- **Option A** (each backend is an independent ISampleProvider in a mixer) is what we're doing — but we only activate one at a time. The mixer infrastructure exists for future layering but currently routes to a single active backend.
- **Option B** (separate `IAudioEngine` implementations) would duplicate WASAPI management, device enumeration, volume control, and command queuing across two classes. Violates DRY.
- **Option C** (as described) keeps all platform-specific audio output code in one place. Backends are testable in isolation (feed them a mock buffer, assert audio output).

### 4.2 Refactored AudioEngine Sketch

```csharp
public sealed class AudioEngine : IAudioEngine
{
    private readonly MixingSampleProvider _mixer;
    private readonly ConcurrentQueue<MidiCommand> _commandQueue = new();
    private IInstrumentBackend? _activeBackend;
    private volatile bool _disposed;

    // Backend registry — created once, reused across instrument switches.
    // Both backends are added to the mixer at initialization (or on first creation).
    // They are NEVER removed. An inactive backend's Read() simply calls Array.Clear()
    // and returns silence. The _activeBackend reference determines which backend receives
    // MIDI commands — it is NOT the mixer input list.
    private readonly SoundFontBackend _sfBackend;
    private Vst3BridgeBackend? _vst3Backend; // lazy — only created when first VST3 instrument selected

    public AudioEngine(string? outputDeviceId = null)
    {
        _mixer = new MixingSampleProvider(
            WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels))
        {
            ReadFully = true  // output silence when no inputs
        };

        _sfBackend = new SoundFontBackend(_commandQueue);
        _mixer.AddMixerInput(_sfBackend.GetSampleProvider());

        _wasapiOut = CreateWasapiOut(outputDeviceId);
        _wasapiOut.Init(new VolumeSampleProvider(_mixer, () => _volume));
        _wasapiOut.Play();
    }

    // ── Public MIDI API (called from MIDI callback thread) ────────────
    // Enqueues to ConcurrentQueue only. Never calls backend methods directly.
    public void NoteOn(int channel, int note, int velocity)
    {
        _commandQueue.Enqueue(new MidiCommand(MidiCommandType.NoteOn, channel, note, velocity));
    }

    public void NoteOff(int channel, int note)
    {
        _commandQueue.Enqueue(new MidiCommand(MidiCommandType.NoteOff, channel, note, 0));
    }

    // ── Audio thread (called from MixingSampleProvider.Read()) ────────
    // The audio thread drains the command queue and dispatches to the active backend.
    // This happens inside each backend's Read() via the shared command queue reference.
    // SoundFontBackend drains the queue in its Read() and calls synth.NoteOn/Off/Render.
    // Vst3BridgeBackend drains pending MIDI commands and sends them over the IPC pipe.

    public void SelectInstrument(InstrumentDefinition instrument)
    {
        var backend = ResolveBackend(instrument.Type);
        Volatile.Read(ref _activeBackend)?.NoteOffAll();
        Volatile.Write(ref _activeBackend, backend);
        _ = Task.Run(() => backend.LoadAsync(instrument));
    }

    private IInstrumentBackend ResolveBackend(InstrumentType type) => type switch
    {
        InstrumentType.SoundFont => _sfBackend,
        InstrumentType.Vst3 => _vst3Backend ??= CreateAndRegisterVst3Backend(),
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private Vst3BridgeBackend CreateAndRegisterVst3Backend()
    {
        var backend = new Vst3BridgeBackend(_commandQueue);
        _mixer.AddMixerInput(backend.GetSampleProvider());
        return backend;
    }
    // ... SetPreset, Volume, device enumeration similar
}
```

> **Threading invariant:** The MIDI callback thread never calls backend methods directly. All backend method calls happen on the audio thread via the command queue drain in `ReadSamples()`. The `_activeBackend` reference determines which backend drains the shared `ConcurrentQueue<MidiCommand>` — inactive backends skip the drain and output silence.

### 4.3 Threading Model Changes

**Before (SF2-only):**
```
MIDI thread → ConcurrentQueue → Audio thread drains queue → synth.Render()
```

**After (multi-backend):**
```
MIDI thread → ConcurrentQueue → Audio thread drains queue → activeBackend.NoteOn/Off()
                                                          → mixer.Read() calls each backend's Read()
```

For the **SoundFontBackend**, the internal threading is identical to today's AudioEngine: command queue drain + MeltySynth Render() in the Read() call.

For the **Vst3BridgeBackend**, the Read() call:
1. Sends any pending MIDI commands to the bridge process via named pipe
2. Signals the bridge to render one block
3. Reads the resulting audio from shared memory
4. Returns the float samples to the mixer

The bridge process's `IAudioProcessor::process()` call happens synchronously within the bridge — it's on the bridge's audio thread, not the host's. The host's audio thread just waits for the shared memory write to complete (spin-wait on `writeIndex` — microseconds).

### 4.4 IAudioEngine Interface Changes

```csharp
public interface IAudioEngine : IDisposable
{
    // Existing — unchanged
    void NoteOn(int channel, int note, int velocity);
    void NoteOff(int channel, int note);
    void SelectInstrument(int programNumber);
    void SelectInstrument(InstrumentDefinition instrument);
    void SetPreset(int channel, int bank, int preset);
    void SetVolume(float volume);
    IReadOnlyList<AudioDeviceInfo> EnumerateOutputDevices();
    void ChangeOutputDevice(string? deviceId);

    // Removed — SF2-specific, now internal to SoundFontBackend
    // void LoadSoundFont(string path);  ← BREAKING: remove from interface

    // New — optional, for diagnostics/UI
    IInstrumentBackend? ActiveBackend { get; }
}
```

**Breaking change:** `LoadSoundFont(string path)` is removed from `IAudioEngine`. It was an SF2-specific leak in the abstraction. Any call sites should migrate to `SelectInstrument(InstrumentDefinition)` with a constructed SF2 definition. Check `MidiInstrumentSwitcher` and any UI code for call sites.

### 4.5 Disposal Sequence

For a tray-resident app that runs for hours/days, correct disposal ordering is critical. Disposing in the wrong order causes use-after-dispose bugs on the audio thread.

#### AudioEngine.Dispose()

```csharp
public void Dispose()
{
    if (_disposed) return;
    _disposed = true;

    // 1. Silence active voices
    _activeBackend?.NoteOffAll();

    // 2. Stop + dispose WASAPI output — terminates the audio thread.
    //    MUST happen before disposing backends. MixingSampleProvider.Read() calls
    //    backend.GetSampleProvider().Read() on the audio thread. Disposing a backend
    //    while the audio thread is reading from it is a use-after-dispose bug.
    _wasapiOut?.Stop();
    _wasapiOut?.Dispose();

    // 3. Dispose VST3 backend (if created) — sends SHUTDOWN, waits, force-kills bridge
    _vst3Backend?.Dispose();

    // 4. Dispose SF2 backend — clears SoundFont cache, nulls synthesizer
    _sfBackend?.Dispose();

    // 5. Dispose mixer if it implements IDisposable
    (_mixer as IDisposable)?.Dispose();
}
```

#### Vst3BridgeBackend.Dispose()

```csharp
public void Dispose()
{
    if (_state == BridgeState.Disposed) return;

    // 1. Mark as disposed — all subsequent Read()/NoteOn() calls become no-ops
    _state = BridgeState.Disposed;

    // 2. Send SHUTDOWN over named pipe (best-effort, ignore if pipe already broken)
    try { SendCommand(BridgeCommand.Shutdown); }
    catch (IOException) { /* pipe broken — bridge already dead */ }

    // 3. Wait for bridge process to exit gracefully (3s timeout)
    if (_bridgeProcess is { HasExited: false })
    {
        if (!_bridgeProcess.WaitForExit(3000))
        {
            // 4. Force-kill if still running
            _bridgeProcess.Kill();
        }
    }

    // 5. Close named pipe handle
    _pipeStream?.Dispose();

    // 6. Release shared memory view accessor
    _sharedMemoryAccessor?.Dispose();

    // 7. Release memory-mapped file handle
    _sharedMemoryFile?.Dispose();
}
```

---

## 5. InstrumentCatalog Changes

### 5.1 Flat List, Typed Entries

The catalog remains a **flat `List<InstrumentDefinition>`**. No grouping by type. Rationale:

- MIDI Program Change routing doesn't care about instrument type — PC #4 maps to an instrument regardless of whether it's SF2 or VST3.
- The settings UI can group by type for display using LINQ: `catalog.GetAll().GroupBy(i => i.Type)`.
- A flat list is simpler to serialize, search, and index.

### 5.2 Lookup Changes

`GetByProgramNumber(int)` stays unchanged — program numbers are unique across all types.

New convenience method:

```csharp
/// <summary>Returns all instruments of the specified type.</summary>
public IReadOnlyList<InstrumentDefinition> GetByType(InstrumentType type)
    => _instruments.Where(i => i.Type == type).ToList();
```

### 5.3 Default Catalog

The 6 default SF2 instruments remain unchanged. No default VST3 instruments are added (VST3 plugins must be user-installed — we can't ship one).

### 5.4 Validation

On catalog load, validate VST3 entries:
- `Vst3PluginPath` must not be null/empty when `Type == Vst3`
- `Vst3PluginPath` should point to an existing `.vst3` directory or file
- Log warnings for missing plugins (don't fail — plugin may be on a removable drive)

---

## 6. File/Folder Structure

New and modified files under `src/MinimalMusicKeyboard/`:

```
src/MinimalMusicKeyboard/
├── Models/
│   ├── InstrumentDefinition.cs        ← MODIFIED (add Type, Vst3 fields)
│   └── InstrumentType.cs              ← NEW (enum: SoundFont, Vst3)
│
├── Interfaces/
│   ├── IAudioEngine.cs                ← MODIFIED (remove LoadSoundFont, add ActiveBackend)
│   └── IInstrumentBackend.cs          ← NEW (backend abstraction)
│
├── Services/
│   ├── AudioEngine.cs                 ← MODIFIED (mixer host, backend routing)
│   ├── InstrumentCatalog.cs           ← MODIFIED (validation, GetByType)
│   ├── MidiInstrumentSwitcher.cs      ← MINOR CHANGE (remove LoadSoundFont calls if any)
│   │
│   ├── Backends/                      ← NEW FOLDER
│   │   ├── SoundFontBackend.cs        ← NEW (extracted from AudioEngine — MeltySynth logic)
│   │   └── Vst3BridgeBackend.cs       ← NEW (Phase 2 — IPC client to bridge process)
│   │
│   └── Vst3Bridge/                    ← NEW FOLDER (Phase 2-3)
│       ├── BridgeProtocol.cs          ← NEW (message types, serialization)
│       ├── BridgeProcessManager.cs    ← NEW (spawn, monitor, restart bridge exe)
│       └── SharedAudioBuffer.cs       ← NEW (memory-mapped file wrapper)
│
├── Assets/
│   └── mmk-vst3-bridge.exe           ← NEW (Phase 3 — native C++ bridge, shipped as asset)
│
└── ...
```

### 6.1 SoundFontBackend Extraction

The biggest Phase 1 refactoring: extract the MeltySynth synthesis logic from `AudioEngine` into `SoundFontBackend`:

**Moves to `SoundFontBackend`:**
- `Synthesizer` field + `Volatile.Read/Write` swap pattern
- `SoundFont` cache (`Dictionary<string, SoundFont>`)
- `CreateSynthesizer()`, `GetOrLoadSoundFont()`
- `ReadSamples()` inner logic (command drain + `synth.Render()`)
- `SwapSynthesizerAsync()`
- The `MidiCommand` struct and `MidiCommandType` enum

**Stays in `AudioEngine`:**
- `WasapiOut` lifecycle (create, swap device, dispose)
- `MixingSampleProvider` ownership
- Volume control
- `IAudioEngine` method implementations (delegating to active backend)
- Device enumeration

#### SoundFontBackend Code Sketch

The shared `ConcurrentQueue<MidiCommand>` is injected via the constructor. The backend drains the queue during `Read()` on the audio thread and calls synth methods directly — no cross-thread calls.

```csharp
public sealed class SoundFontBackend : IInstrumentBackend
{
    private readonly ConcurrentQueue<MidiCommand> _commandQueue;
    private Synthesizer? _synthesizer;
    private readonly Dictionary<string, SoundFont> _soundFontCache = new();
    private readonly object _soundFontCacheLock = new();
    private readonly float[] _renderBuffer = new float[BlockSize * 2];

    public string DisplayName => "MeltySynth SF2";
    public InstrumentType BackendType => InstrumentType.SoundFont;
    public bool IsReady => Volatile.Read(ref _synthesizer) is not null;

    public SoundFontBackend(ConcurrentQueue<MidiCommand> commandQueue)
    {
        _commandQueue = commandQueue;
    }

    // ── ISampleProvider.Read() — called on the WASAPI audio thread ───
    public int Read(float[] buffer, int offset, int count)
    {
        var synth = Volatile.Read(ref _synthesizer);
        if (synth is null)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }

        // Drain command queue — only the active backend does this
        while (_commandQueue.TryDequeue(out var cmd))
        {
            switch (cmd.Type)
            {
                case MidiCommandType.NoteOn:
                    synth.NoteOn(cmd.Channel, cmd.Note, cmd.Velocity);
                    break;
                case MidiCommandType.NoteOff:
                    synth.NoteOff(cmd.Channel, cmd.Note);
                    break;
                case MidiCommandType.ProgramChange:
                    synth.ProcessMidiMessage(cmd.Channel, 0xC0, cmd.Note, 0);
                    break;
            }
        }

        synth.RenderInterleaved(buffer.AsSpan(offset, count));
        return count;
    }

    // ── LoadAsync — called from a background Task ────────────────────
    public async Task LoadAsync(InstrumentDefinition instrument, CancellationToken cancellation = default)
    {
        var sf = await Task.Run(() => GetOrLoadSoundFont(instrument.SoundFontPath), cancellation);
        var settings = new SynthesizerSettings(SampleRate);
        var newSynth = new Synthesizer(sf, settings);
        newSynth.ProcessMidiMessage(0, 0xC0, instrument.ProgramNumber, 0);

        // Volatile swap: silence old synth, install new one
        var oldSynth = Volatile.Read(ref _synthesizer);
        oldSynth?.NoteOffAll();
        Volatile.Write(ref _synthesizer, newSynth);
    }

    private SoundFont GetOrLoadSoundFont(string path)
    {
        lock (_soundFontCacheLock)
        {
            if (_soundFontCache.TryGetValue(path, out var cached))
                return cached;
        }

        SoundFont sf;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            sf = new SoundFont(stream);
        }

        lock (_soundFontCacheLock)
        {
            _soundFontCache[path] = sf;
        }
        return sf;
    }

    // ── IInstrumentBackend (audio-thread-only methods) ───────────────
    public void NoteOn(int channel, int note, int velocity)
        => Volatile.Read(ref _synthesizer)?.NoteOn(channel, note, velocity);

    public void NoteOff(int channel, int note)
        => Volatile.Read(ref _synthesizer)?.NoteOff(channel, note);

    public void NoteOffAll()
        => Volatile.Read(ref _synthesizer)?.NoteOffAll();

    public void SetProgram(int channel, int bank, int program)
        => Volatile.Read(ref _synthesizer)?.ProcessMidiMessage(channel, 0xC0, program, 0);

    public ISampleProvider GetSampleProvider() => this; // SoundFontBackend is its own ISampleProvider

    // ── Dispose ──────────────────────────────────────────────────────
    public void Dispose()
    {
        Volatile.Read(ref _synthesizer)?.NoteOffAll();
        Volatile.Write(ref _synthesizer, null);
        lock (_soundFontCacheLock)
        {
            _soundFontCache.Clear();
        }
    }
}
```

---

## 7. Risk Areas for Gren's Review

### 7.1 🔴 High Risk — Bridge Process Lifecycle

The bridge process (`mmk-vst3-bridge.exe`) must be reliably spawned and monitored:
- **Startup:** What if the bridge fails to start? (missing exe, antivirus quarantine, permission denied)
- **Crash detection:** Named pipe `BrokenPipeException` is the signal. But what if the pipe breaks due to a transient OS issue, not a crash?
- **Restart policy:** Auto-restart on crash? How many times? With backoff?
- **Shutdown ordering:** Bridge must exit before `AudioEngine.Dispose()` completes. Timeout + `Process.Kill()` as fallback.
- **Multiple instances:** If user has 2 VST3 instruments configured, do we spawn 2 bridges or multiplex?

#### Vst3BridgeBackend State Machine

```csharp
private enum BridgeState { Running, Faulted, Disposed }
private volatile BridgeState _state = BridgeState.Running;

/// <summary>Raised when the bridge process crashes. Message contains the reason.</summary>
public event EventHandler<string>? BridgeFaulted;
```

**State behaviors:**

| State | `Read()` | `NoteOn()`/`NoteOff()` | IPC | Transition |
|-------|----------|------------------------|-----|------------|
| **Running** | Normal IPC: send `RENDER`, spin-wait on shared memory, return audio block | Send `MIDI` command over named pipe | Active | → `Faulted` on `IOException`/`BrokenPipeException` in `Read()` or pipe write |
| **Faulted** | Immediately calls `Array.Clear()` and returns silence (zero-cost, no IPC) | No-ops (commands are silently dropped) | Dead | → `Running` after `BridgeProcessManager` detects crash via process exit event, successfully restarts bridge, and re-sends `INIT` |
| **Disposed** | No-ops, returns silence | No-ops | N/A | Terminal state — no transitions out |

**Crash → Faulted flow:**
1. Bridge process crashes (access violation, heap corruption, etc.)
2. `Vst3BridgeBackend.Read()` catches `IOException`/`BrokenPipeException` on next pipe write
3. Set `_state = BridgeState.Faulted`
4. Raise `BridgeFaulted` event with reason string (e.g., "VST3 bridge process exited unexpectedly")
5. Tray icon shows tooltip: "Plugin crashed — click to retry"
6. `BridgeProcessManager` independently detects crash via `Process.Exited` event
7. On user retry (or auto-restart if policy allows): spawn new bridge, connect to existing host-owned IPC resources, send `INIT`, transition `_state = BridgeState.Running`

**Spike's recommendation:** One bridge process per VST3 plugin instance. Simpler isolation. A single bridge hosting multiple plugins would mean one crash kills all plugins.

### 7.2 🔴 High Risk — Audio Thread Blocking on IPC

The host's WASAPI audio thread calls `Vst3BridgeBackend.Read()`, which must get audio from the bridge process within the 20ms buffer deadline. If the bridge is slow:
- Shared memory spin-wait should have a timeout (~5ms)
- On timeout: output the previous block (repeat) or silence
- Log the underrun for diagnostics

**Question for Gren:** Is spin-waiting on shared memory acceptable on the audio thread, or should we use a semaphore/event?

### 7.3 🟡 Medium Risk — Memory-Mapped File Cleanup

Shared memory (`MemoryMappedFile`) and named pipes must be disposed cleanly:
- If the host crashes, the OS cleans up named pipes and MMFs automatically
- If the bridge crashes, the host must close its handles to the shared resources
- `BridgeProcessManager.Dispose()` must handle all of these

### 7.4 🟡 Medium Risk — SoundFontBackend Extraction Regression

Extracting MeltySynth logic from `AudioEngine` into `SoundFontBackend` touches the audio hot path. Any regression here (wrong buffer size, missing command drain, stale synthesizer reference) would break all existing SF2 playback. Ed's existing tests should catch this, but recommend:
- Phase 1 PR must pass all existing `AudioEngine` tests with zero changes to test code
- Add a new integration test: SF2 instrument switch through the new backend interface produces identical audio output

### 7.5 🟢 Low Risk — JSON Backward Compatibility

`System.Text.Json` handles missing enum properties by defaulting to 0. Verified behavior:
- Old JSON (no `type` field) → `InstrumentType.SoundFont` ✅
- New JSON (`"type": "SoundFont"`) → works ✅
- New JSON (`"type": "Vst3"`) → works ✅
- Old JSON with new fields present → ignored by old versions, no crash ✅

### 7.6 🟢 Low Risk — `LoadSoundFont` Removal from IAudioEngine

This is a breaking interface change but has a small blast radius. Grep for `LoadSoundFont` call sites — currently only in `AudioEngine` itself and potentially in test stubs. Migration is straightforward.

---

## 8. Memory Budget Impact

| Component | Idle Memory | Notes |
|-----------|-------------|-------|
| Current SF2 (MeltySynth) | 10–40 MB | SF2 cache; unchanged |
| Vst3BridgeBackend (managed side) | ~2 MB | Named pipe + MMF handles |
| mmk-vst3-bridge.exe | 5–50 MB | Depends on plugin; isolated process |
| Shared audio buffer (MMF) | ~8 KB | One stereo block × 2 (double buffer) |

**Key insight:** The bridge process memory is **not counted** against the host's 50 MB budget. The host adds only ~2 MB for the IPC client. The bridge's memory consumption depends entirely on the loaded VST3 plugin and is isolated.

---

## 9. What Stays Backward-Compatible

| Component | Change Level | Backward Compatible? |
|-----------|-------------|---------------------|
| `InstrumentDefinition` | Extended | ✅ Old JSON loads unchanged |
| `IAudioEngine` | Modified | ⚠️ `LoadSoundFont` removed — minor break |
| `AudioEngine` | Refactored | ✅ Same external behavior for SF2 |
| `InstrumentCatalog` | Extended | ✅ Flat list, same lookup methods |
| `MidiInstrumentSwitcher` | Unchanged | ✅ Routes by `InstrumentDefinition`, type-agnostic |
| Default instruments | Unchanged | ✅ Same 6 SF2 presets |
| instruments.json format | Extended | ✅ Old files load as-is |

---

## 10. Open Questions for Gren

1. **Bridge language:** C++ is the natural choice for VST3 COM hosting. Rust (via `vst3-sys` crate) is an alternative with better memory safety. Preference?

2. **Plugin GUI:** VST3 plugins can expose a GUI (`IPlugView`). Do we want to support showing the plugin editor window from our WinUI3 settings page? This would require HWND parenting from the bridge process — significant complexity. Recommendation: defer to a future phase.

3. **Audio format negotiation:** The proposal assumes 48 kHz / stereo / 32-bit float throughout. Some VST3 plugins may prefer different sample rates. Should the bridge resample, or should we negotiate?

4. **Preset management:** VST3 presets can be saved/loaded via `IEditController` state. Should the bridge support saving plugin state back to a `.vstpreset` file, or is load-only sufficient for v1?

5. **CLAP as alternative:** Should we also support CLAP plugins? CLAP has a simpler C ABI (no COM), better crash resilience, and growing adoption. It could use the same bridge architecture with a different backend. Recommend: design the bridge protocol to be plugin-format-agnostic, then add CLAP support as a future phase.
