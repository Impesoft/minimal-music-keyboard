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
