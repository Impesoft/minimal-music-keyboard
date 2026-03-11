# Phase 1 Code Review
**Reviewer:** Gren
**Date:** 2026-03-11
**Verdict:** REJECTED

---

## Checklist Results

### Threading
- ✅ PASS — AudioEngine.ReadSamples() drains the ConcurrentQueue on the audio thread (lines 203–225)
- ✅ PASS — The drain loop dispatches to backend.NoteOn/NoteOff/NoteOffAll/SetProgram (lines 208–224)
- ✅ PASS — No direct backend calls from MIDI-path methods; all enqueue to ConcurrentQueue. Dispose() calls NoteOffAll directly as specified in §4.5 of the proposal. SelectInstrument enqueues NoteOffAll through the queue (improvement over proposal sketch).
- ⚠️ PARTIAL — IInstrumentBackend per-method docs correctly state "audio render thread only." However, the interface-level summary is missing the full threading contract from proposal §2.1 (ConcurrentQueue pattern, MIDI thread restriction, LoadAsync background task). See Issue #2.

### Dispose() Sequence
- ✅ PASS — AudioEngine.Dispose() order: `_disposed=true` → `NoteOffAll` → `Stop+Dispose wasapiOut` (under lock) → `Dispose _sfBackend` → `Dispose _mixer`. Matches §4.5 exactly.
- ✅ PASS — SoundFontBackend.Dispose() silences voices (`NoteOffAll(immediate: true)`), nulls synthesizer via Volatile.Write, clears SoundFont cache. Correct teardown.
- ✅ PASS — No double-dispose risk. AudioEngine has `_disposed` guard. SoundFontBackend operations are idempotent (null-conditional + clear on empty dict).

### IInstrumentBackend Interface
- ✅ PASS — Has all required members: LoadAsync(), NoteOn(), NoteOff(), NoteOffAll(), SetProgram(), GetSampleProvider(), IsReady, DisplayName, BackendType, IDisposable
- ⚠️ PARTIAL — XML doc comments present on all members but interface-level threading contract is abbreviated (see Issue #2)
- ✅ PASS — Correct namespace: `MinimalMusicKeyboard.Interfaces`

### SoundFontBackend
- ✅ PASS — Extracts ALL MeltySynth logic from AudioEngine. Zero `using MeltySynth` in AudioEngine.cs (only a comment on line 13 mentioning "MeltySynth" in the class summary).
- ✅ PASS — Preserves Volatile.Write/Read synthesizer swap pattern. Verified: Read() line 53, LoadAsync() line 95, IsReady line 40, NoteOn/NoteOff lines 109/112.
- ✅ PASS — NoteOn/NoteOff documented as audio-thread-only (lines 107, 111, 115)
- ⚠️ ISSUE — Constructor accepts ConcurrentQueue but the field `_commandQueue` is never read. See Issue #3.
- ✅ PASS — IsReady: `Volatile.Read(ref _synthesizer) is not null` — correct.

### InstrumentDefinition
- ✅ PASS — InstrumentType enum: SoundFont=0, Vst3=1
- ✅ PASS — Type defaults to SoundFont for backward-compat JSON deserialization
- ✅ PASS — Vst3PluginPath and Vst3PresetPath added as nullable strings
- ✅ PASS — SoundFontPath changed from required to nullable (`string?`). AudioEngine handles both null and placeholder string.
- ✅ PASS — JSON property names are camelCase via `[JsonPropertyName]`

### AudioEngine Refactor
- ❌ FAIL — Does not compile. See Issue #1.
- ✅ PASS — Implements IAudioEngine with same public surface (LoadSoundFont retained for Phase 1 backward compat — correct decision)
- ✅ PASS — Uses MixingSampleProvider (_mixer) as the output, ReadFully=true
- ✅ PASS — SoundFontBackend registered as permanent mixer input (line 54), never removed
- ✅ PASS — Zero MeltySynth-specific code remaining in AudioEngine (verified by grep)

---

## Issues Found

### Issue #1 — Compilation error: extra closing parenthesis ❌ BLOCKING

**File:** `src/MinimalMusicKeyboard/Services/AudioEngine.cs`, line 177
**Severity:** BLOCKING

Line 177 has `}));` — one closing parenthesis too many. `LoadAsync(` opens one paren, but the closing has two. This produces `CS1002: ; expected` and `CS1513: } expected` — the project does not compile.

```csharp
// CURRENT (broken):
        }));

// CORRECT:
        });
```

Build output confirms: `error CS1002` and `error CS1513` at line 177.

---

### Issue #2 — IInstrumentBackend missing interface-level threading contract ⚠️ REQUIRED

**File:** `src/MinimalMusicKeyboard/Interfaces/IInstrumentBackend.cs`, lines 8–10
**Severity:** REQUIRED (not blocking, but must be fixed before merge)

The proposal §2.1 specifies a detailed threading contract in the interface-level summary:

> *"NoteOn/NoteOff/SetProgram are called from the AUDIO RENDER THREAD ONLY. AudioEngine drains its ConcurrentQueue during ReadSamples() and dispatches to the active backend on the audio thread. The MIDI callback thread never calls backend methods directly."*

The implementation only has: `"Abstraction for an audio synthesis backend (SF2 soundfont, VST3 plugin, etc.)."` The per-method docs are correct, but the interface-level contract is what Phase 2 implementers (Vst3BridgeBackend) will read first. This threading contract was a hard-won invariant — it should be prominent.

---

### Issue #3 — SoundFontBackend stores unused ConcurrentQueue reference ⚠️ REQUIRED

**File:** `src/MinimalMusicKeyboard/Services/SoundFontBackend.cs`, lines 20, 29, 32
**Severity:** REQUIRED (not blocking, but must be fixed before merge)

The constructor accepts and stores a `ConcurrentQueue<MidiCommand>` but no method ever reads `_commandQueue`. This is dead code from the proposal's §6.1 sketch where the backend self-drained the queue. Faye correctly chose option (a) from my v1.1 review Implementation Note — AudioEngine drains and dispatches — but didn't remove the now-unused queue injection.

An unused queue field on a backend class sends the wrong signal about the threading model: it implies the backend drains the queue, which it doesn't. Either:
- **(a)** Remove the `_commandQueue` field and constructor parameter entirely, or
- **(b)** If retained for Phase 2 compatibility reasons, add a clear `// Retained for future multi-backend queue routing — currently unused` comment.

Option (a) is preferred. The constructor should take only `string soundFontPath`.

---

### Issue #4 — InstrumentDefinition uses `set` instead of `init` 💡 SUGGESTION

**File:** `src/MinimalMusicKeyboard/Models/InstrumentDefinition.cs`, lines 24, 31, 39, 43
**Severity:** SUGGESTION

The proposal specifies `init` setters for `Type`, `SoundFontPath`, `Vst3PluginPath`, and `Vst3PresetPath` to preserve the record's immutability. The implementation uses `set`, which allows post-construction mutation. The class-level doc says "Immutable definition" but the `set` accessors undermine that. System.Text.Json in .NET 8+ can deserialize `init` properties via source generators. Consider changing to `init` if no call site requires post-construction mutation.

---

### Issue #5 — SoundFontBackend.Dispose() lacks `_disposed` guard 💡 SUGGESTION

**File:** `src/MinimalMusicKeyboard/Services/SoundFontBackend.cs`, lines 133–143
**Severity:** SUGGESTION

While the Dispose operations are idempotent, adding a `_disposed` guard matches the AudioEngine pattern and is defensive best practice for a resource class. Not strictly necessary — current code is safe — but improves consistency.

---

## Verdict Summary

**REJECTED** — 1 blocking issue prevents compilation. The fix is a single-character deletion (line 177: `}));` → `});`), so this should be a quick turnaround.

The 2 REQUIRED issues (interface threading contract, unused queue field) must also be fixed before merge but are non-blocking for a re-review — they can be bundled with the syntax fix.

The architecture is sound. Faye made the right call on every significant design question:
- AudioEngine drains the queue (option (a) from my implementation note) ✅
- LoadSoundFont retained for Phase 1 backward compat ✅
- Volume control applied in ReadSamples wrapper ✅
- SoundFontBackend is its own ISampleProvider (self-returning GetSampleProvider) ✅
- Volatile.Read/Write pattern preserved verbatim ✅
- SoundFont cache with double-checked locking preserved ✅
- FileStream `using` pattern preserved ✅

**Who should fix:** Per lockout rules, Faye is locked out of this artifact for this cycle. **Recommend Jet** — the fixes are mechanical (syntax error, doc comments, remove unused field) and don't require deep audio domain knowledge. Jet should be able to apply all three fixes and submit for re-review within one cycle.

**After fixes:** Re-review is expected to be a quick APPROVED. Phase 2 may NOT begin until Phase 1 passes.
