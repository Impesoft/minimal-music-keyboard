# Phase 4 Code Review: Settings UI for VST3
**Date:** 2026-03-11  
**Reviewer:** Gren  
**Author:** Jet

## Files Reviewed

| File | Change |
|------|--------|
| `src/MinimalMusicKeyboard/Models/InstrumentDefinition.cs` | Added `InstrumentType` enum, `Type`/`Vst3PluginPath`/`Vst3PresetPath` properties, changed `SoundFontPath` from `required string` to `string?` |
| `src/MinimalMusicKeyboard/Services/AudioEngine.cs` | Added `_vst3Backend` field, registered in mixer, split `SelectInstrument` into type handlers, added Dispose |
| `src/MinimalMusicKeyboard/Services/InstrumentCatalog.cs` | Added `AddOrUpdateVst3Instrument()` method |
| `src/MinimalMusicKeyboard/Views/SettingsWindow.xaml.cs` | Type toggle (RadioButtons), SF2/VST3 panels with visibility switching, VST3 file pickers, slot instrument management |

## Summary

Good UI work — the type toggle, panel switching, and file pickers are cleanly implemented, and the AudioEngine integration correctly routes VST3 instruments through the backend with proper threading discipline. Two required fixes: one weakens the record's immutability contract (thread-safety concern), and one breaks MIDI program change resolution for SF2 instruments when any VST3 slot is configured.

## What's Correct

- **SF2 UI fully preserved** — existing combo box, SF2 browse, and slot badge workflows untouched.
- **Type toggle works** — `RadioButtons` with panel visibility switching. Initial selection set before handler attachment (no spurious fire).
- **FileOpenPicker correctly initialized** — `InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this))` on all three pickers. ✅
- **AudioEngine integration sound** — `SelectInstrument` dispatches by type. VST3 path: `NoteOffAll` → `Volatile.Write(_activeBackend, _vst3Backend)` → `LoadAsync`. Threading contract preserved.
- **Both backends always in mixer** — matches the approved always-in-mixer approach (§3.1).
- **Dispose chain complete** — `_vst3Backend.Dispose()` added in correct position (after sfBackend, before mixer).
- **Build: 0 errors, 2 warnings** (pre-existing `_frameSize` in Vst3BridgeBackend — Phase 3 placeholder).

## Issues Found

### 🟡 REQUIRED — InstrumentDefinition properties changed from `init` to `set`

**Location:** `Models/InstrumentDefinition.cs` lines 24, 31, 38, 41

`Type`, `SoundFontPath`, `Vst3PluginPath`, and `Vst3PresetPath` use `{ get; set; }` instead of `{ get; init; }`. The record is documented as *"Immutable definition of a single instrument preset"* and its references cross thread boundaries via `Volatile.Read`/`Volatile.Write` in AudioEngine. With `set`, a future developer could mutate a live instance instead of using `with`, causing a data race between the UI thread and the audio render thread.

All current call sites use `with` expressions, which work correctly with `init` accessors on records. No code requires `set`. System.Text.Json supports `init`-only properties during deserialization from .NET 5+.

**Fix:** Change all four properties from `set` to `init`:
```csharp
public InstrumentType Type { get; init; } = InstrumentType.SoundFont;
public string? SoundFontPath { get; init; }
public string? Vst3PluginPath { get; init; }
public string? Vst3PresetPath { get; init; }
```
Verify build passes — no call site changes needed.

### 🟡 REQUIRED — VST3 slot ProgramNumber collides with SF2 instruments

**Location:** `Views/SettingsWindow.xaml.cs` line 417 + `Services/InstrumentCatalog.cs` line 95

When creating a new VST3 instrument definition, `ProgramNumber = slotIdx` (0–7). These collide with SF2 defaults: Grand Piano = 0, Bright Piano = 1, Electric Piano = 4, etc. The catalog's `_byProgramNumber` dictionary is last-writer-wins:
```csharp
_byProgramNumber[inst.ProgramNumber] = inst; // VST3 overwrites SF2
```
`MidiInstrumentSwitcher.OnProgramChangeReceived()` calls `GetByProgramNumber(program)` to resolve MIDI PC messages → once a user configures any VST3 slot, the corresponding SF2 instrument becomes unreachable via MIDI program change. This breaks the existing SF2 switching workflow.

**Fix (choose one):**  
(a) Use out-of-GM-range program numbers for VST3 slots: `ProgramNumber = 128 + slotIdx`  
(b) Skip VST3-type instruments when populating `_byProgramNumber` in the catalog rebuild loop — add `if (inst.Type == InstrumentType.SoundFont)` guard before the `_byProgramNumber` insert in all three rebuild locations.

Option (b) is architecturally cleaner — VST3 instruments are triggered by button mappings, never by MIDI program changes.

## Observations (non-blocking)

- **Bridge-ready status indicator not implemented.** Jet acknowledges this; reasonable deferral since the bridge (Phase 3) doesn't exist yet. Track as future work when Phase 3 lands.
- **RadioButtons default orientation is vertical** — each slot's SF2/VST3 toggle takes more vertical space than needed. Consider `MaxColumns="2"` for horizontal layout in a future UX pass.

## Verdict: REJECTED

Two required fixes. Neither is a compilation error or fundamental architecture flaw, but Issue 2 breaks existing SF2 MIDI program change behaviour — a core functional requirement (SF2 instrument selection MUST be preserved).

Assign fixes to: **Ed** — Jet is locked out per lockout rule.

---

## Re-Review (Ed's fixes applied)
**Date:** 2026-03-11
**Issues from prior review:**
1. **RESOLVED** — `init` immutability. All 9 properties on `InstrumentDefinition` now use `{ get; init; }`. Zero `set` accessors remain. Object initializer sites (SettingsWindow lines 411–418) and `with` expressions (lines 420, 436, 487) compile correctly with `init` setters on records.
2. **RESOLVED** — Program number collision. All four catalog rebuild loops (constructor line 30, `UpdateAllSoundFontPaths` line 55, `UpdateInstrumentSoundFont` line 78, `AddOrUpdateVst3Instrument` line 99) now guard `_byProgramNumber` inserts with `if (inst.Type == InstrumentType.SoundFont)`. VST3 instruments are excluded from the program-number index. Ed chose option (b) from the original review — architecturally clean.

**Additional findings:** None. Full conformance re-check passed:
- No `set` accessors anywhere on `InstrumentDefinition`
- VST3 instruments resolved by slot ID via `GetById("vst3-slot-{n}")` — consistent with slot-based architecture
- XAML: type toggle (RadioButtons), SF2/VST3 panel switching, plugin + preset file pickers all present
- AudioEngine: `SelectInstrument` dispatches by type, `Volatile.Write` for backend swap, both backends in mixer
- Bridge-ready status indicator: still deferred (Phase 3 bridge exists as scaffold only) — already noted as non-blocking

**Verdict: APPROVED**
