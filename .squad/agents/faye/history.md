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
