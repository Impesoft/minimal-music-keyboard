# Orchestration Log: Ed Phase 4 Fixes Applied

**Date:** 2026-03-11  
**Agent:** Ed (Tester / QA)  
**Context:** Gren rejected Phase 4 with 2 required fixes; Jet was locked out per review protocol  
**Status:** ✅ **COMPLETE — Build verified**

## Objective

Apply both required fixes from Gren's Phase 4 code review rejection before re-review can proceed.

---

## Fix 1: InstrumentDefinition Immutability — `set` → `init`

**File:** `src/MinimalMusicKeyboard/Models/InstrumentDefinition.cs`

Changed four properties from mutable `set` to immutable `init`:

- `Type { get; set; }` → `Type { get; init; }`
- `SoundFontPath { get; set; }` → `SoundFontPath { get; init; }`
- `Vst3PluginPath { get; set; }` → `Vst3PluginPath { get; init; }`
- `Vst3PresetPath { get; set; }` → `Vst3PresetPath { get; init; }`

All other properties (`Id`, `DisplayName`, `BankNumber`, `ProgramNumber`, `Category`) were already `init`.

**Rationale:** `InstrumentDefinition` is documented as immutable and crosses the audio thread boundary via `Volatile.Read`/`Volatile.Write` in `AudioEngine.cs`. Mutable `set` accessors on a live instance would allow a data race between the UI thread and the audio render thread.

**Impact on call sites:** None — all call sites use `with` expressions, which are compatible with `init` accessors.

---

## Fix 2: VST3 Program Number Collision Resolution

**File:** `src/MinimalMusicKeyboard/Services/InstrumentCatalog.cs`

Added `if (inst.Type == InstrumentType.SoundFont)` guard before every `_byProgramNumber` insert.

Applied in all four rebuild loops:
1. Constructor (`InstrumentCatalog()`)
2. `UpdateAllSoundFontPaths()`
3. `UpdateInstrumentSoundFont()`
4. `AddOrUpdateVst3Instrument()`

**Problem:** VST3 instruments (slot indices 0–7) were overwriting GM/SF2 entries in `_byProgramNumber`, making those SF2 instruments unreachable via MIDI program change messages (called by `MidiInstrumentSwitcher.OnProgramChangeReceived()`).

**Solution (Gren option b):** VST3 instruments are triggered by button mappings only, not MIDI PC messages. Excluding VST3 entries from `_byProgramNumber` means `GetByProgramNumber()` only resolves SF2 instruments, preserving the existing SF2 MIDI switching workflow. VST3 instruments remain fully reachable via `GetById()` using their `vst3-slot-{N}` IDs.

---

## Build Result

✅ **Build succeeded**

```
Build: 0 errors, 2 warnings (pre-existing)
Warnings: CS0414 — Vst3BridgeBackend._frameSize (Phase 3 placeholder, unused)
```

---

## Next Steps

Phase 4 is ready for Gren's re-review with all required fixes applied.

**Full decision log:** See `.squad/decisions.md` — "Decision: Phase 4 Code Review — Settings UI for VST3"
