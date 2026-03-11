# Jet — Phase 1 Fix-Up Report
**Date:** 2026-03-11
**Requested by:** Ward Impe
**Fixes applied by:** Jet (standing in for Faye, who is locked out this cycle)

---

## Fixes Applied

### Fix 1 — Compilation error (AudioEngine.cs, line 177) ✅
**Issue:** Stray extra closing parenthesis: `}));` inside `LoadSoundFont()`.
**Fix:** Changed to `});` — deleted one `)`.
**Impact:** This was the sole compilation blocker (CS1002 + CS1513).

### Fix 2 — Interface threading contract (IInstrumentBackend.cs) ✅
**Issue:** `NoteOn()`, `NoteOff()`, `NoteOffAll()` had summary docs saying "audio render thread only" but lacked the explicit `<remarks>` block Gren required for Phase 2 implementers.
**Fix:** Added `<remarks>Called from the audio thread only. Do not call from the MIDI callback thread.</remarks>` to all three methods.

### Fix 3 — Unused ConcurrentQueue field (SoundFontBackend.cs) ✅
**Issue:** Constructor accepted `ConcurrentQueue<MidiCommand> commandQueue` and stored it as `_commandQueue`, but the field was never read anywhere in the class. Dead code from an earlier design sketch.
**Fix:**
- Removed the `private readonly ConcurrentQueue<MidiCommand> _commandQueue;` field.
- Removed the `ConcurrentQueue<MidiCommand> commandQueue` constructor parameter and the `_commandQueue = ...` assignment.
- Removed the now-unused `using System.Collections.Concurrent;` directive.
- Updated `AudioEngine` constructor call from `new SoundFontBackend(path, _commandQueue)` to `new SoundFontBackend(path)` (necessary consequence of the parameter removal — not a separate change).

---

## Build Verification

Command: `dotnet build Q:\source\minimal-music-keyboard\MinimalMusicKeyboard.sln --no-incremental`

**Result: Build succeeded — 0 errors, 0 warnings.**

Both projects compiled cleanly:
- `MinimalMusicKeyboard.Tests` — succeeded
- `MinimalMusicKeyboard` — succeeded

---

## Scope Confirmation

No other changes were made. Issues #4 and #5 from Gren's review (SUGGESTIONS only) were intentionally left untouched per the task brief. Ready for Gren's re-review.
