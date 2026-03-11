# VST3 Architecture Review

**Reviewer:** Gren (Supporting Architect)  
**Date:** 2026-07-17  
**Document reviewed:** `docs/vst3-architecture-proposal.md` v1.0 (Spike)  
**Research reviewed:** `docs/vst3-dotnet-options.md` (Faye)  
**Verdict:** APPROVED WITH CONDITIONS

## Summary

The overall architecture is sound: out-of-process bridge (Option C) is the correct choice for a tray-resident app that runs for days, the `IInstrumentBackend` abstraction is clean, and the phased delivery plan is pragmatic. However, the proposal has six issues ranging from BLOCKING to SUGGESTED that must be addressed before implementation begins. The two BLOCKING issues are: (1) an unresolved contradiction between the threading model description and the code sketch for MIDI command dispatch, and (2) the absence of any `Dispose()` specification for the refactored `AudioEngine` or `Vst3BridgeBackend` — both of which manage resources that require sequenced teardown.

## Issues Found

### 1. BLOCKING — Threading model contradicts code sketch for NoteOn/NoteOff dispatch

**Section:** §4.2 (code sketch) vs §4.3 (threading model) vs §2.1 (interface contract)

The threading model in §4.3 states:

```
MIDI thread → ConcurrentQueue → Audio thread drains queue → activeBackend.NoteOn/Off()
```

This implies the AudioEngine still owns the `ConcurrentQueue<MidiCommand>`, the audio thread drains it, and backend `NoteOn()` is called **from the audio thread**. This is correct and consistent with the existing AudioEngine pattern.

But the code sketch in §4.2 shows:

```csharp
public void NoteOn(int channel, int note, int velocity)
{
    var backend = Volatile.Read(ref _activeBackend);
    backend?.NoteOn(channel, note, velocity);
}
```

This calls `backend.NoteOn()` **directly from the MIDI callback thread**. No queue. No audio-thread drain. This is a thread-safety violation for `SoundFontBackend`, which wraps MeltySynth — a synthesizer whose thread safety was flagged as unverified in the v1.0 review and resolved specifically by the ConcurrentQueue pattern (see decisions.md, Faye Decision 1).

Additionally, the interface docstring on `NoteOn` says "Enqueued by AudioEngine; processed on the audio thread." But if `NoteOn()` is a direct method call from the MIDI thread as the sketch shows, the "enqueued" language is misleading — the call is synchronous.

**The ambiguity:** Does `AudioEngine` own the queue and drain it in `Read()`, calling `backend.NoteOn()` from the audio thread? Or does each backend own its own queue? The proposal must choose one and be explicit:

- **Option A (recommended):** AudioEngine keeps its `ConcurrentQueue<MidiCommand>`. In `Read()`, the audio thread drains the queue and calls `_activeBackend.NoteOn()`. The `NoteOn()`/`NoteOff()` methods on `IInstrumentBackend` are audio-thread-only. The public `AudioEngine.NoteOn()` enqueues to the queue as today. The §4.2 sketch must be corrected.
- **Option B:** Each backend owns its own `ConcurrentQueue`. `AudioEngine.NoteOn()` calls `backend.NoteOn()` from the MIDI thread, and the backend enqueues internally. This works but duplicates queue logic across backends and changes the pattern that Faye's Decision 1 established.

**Recommended fix:** Correct the §4.2 sketch to show the ConcurrentQueue drain in a `Read()`-equivalent method. Update the `IInstrumentBackend` docstring to specify that `NoteOn()`/`NoteOff()` are called from the audio thread only. State the rule explicitly: "The MIDI callback thread never calls backend methods directly."

---

### 2. BLOCKING — No Dispose() specification for refactored AudioEngine or Vst3BridgeBackend

**Section:** §4.2, §6.1, §7.1

The current `AudioEngine.Dispose()` is well-sequenced: silence → stop WASAPI → null synthesizer → clear cache. The proposal refactors AudioEngine significantly but provides **no Dispose() sketch** for the new version. There is also no `Vst3BridgeBackend.Dispose()` specification anywhere.

For a project whose primary architectural constraint is "must be memory-leak-free and run for hours/days," omitting disposal specifications from an architecture proposal is a blocking gap. The following must be defined:

**Refactored `AudioEngine.Dispose()` must (in this order):**

1. Set `_disposed = true` (guard flag)
2. Call `_activeBackend?.NoteOffAll()` — silence active voices
3. Stop and dispose `_wasapiOut` — terminates audio thread. This MUST happen before disposing backends, because `MixingSampleProvider.Read()` calls `backend.GetSampleProvider().Read()` on the audio thread. Disposing a backend while the audio thread is reading from it is a use-after-dispose bug.
4. Dispose `_vst3Backend` (if created) — sends SHUTDOWN, waits, kills bridge
5. Dispose `_sfBackend` — clears SoundFont cache, nulls synthesizer reference
6. Dispose `_mixer` (if needed — check if `MixingSampleProvider` is `IDisposable`)

**`Vst3BridgeBackend.Dispose()` must (in this order):**

1. Send `SHUTDOWN` message over named pipe
2. Wait with timeout (e.g., 3 seconds) for bridge process to exit
3. If bridge didn't exit, call `Process.Kill()` as fallback
4. Close named pipe handle (`NamedPipeClientStream.Dispose()`)
5. Release shared memory handle (`MemoryMappedFile.Dispose()`)
6. Dispose `MemoryMappedViewAccessor` (if holding one)

Ward's question "Is `Vst3BridgeBackend.Dispose()` described?" — the answer is no, and it must be.

**Recommended fix:** Add `Dispose()` method sketches for both `AudioEngine` (refactored) and `Vst3BridgeBackend` to the proposal, with explicit ordering and rationale for each step.

---

### 3. REQUIRED — MixingSampleProvider thread safety during backend swap is under-specified

**Section:** §4.2

The proposal adds `_sfBackend.GetSampleProvider()` to the mixer at construction time (line 312). But when a VST3 instrument is first selected, `CreateVst3Backend()` is called lazily (line 333), and presumably its `ISampleProvider` must be added to the mixer at that point. This `AddMixerInput()` call would happen from whatever thread calls `SelectInstrument()` (likely the MIDI thread or a UI thread), while the WASAPI audio thread is simultaneously calling `_mixer.Read()`.

NAudio's `MixingSampleProvider.AddMixerInput()` does use an internal lock, which provides basic protection. However, `RemoveMixerInput()` and `RemoveAllMixerInputs()` have weaker guarantees — and the proposal doesn't clarify whether backends are ever removed from the mixer or just left producing silence.

**Clarification needed:**

- Are both backends always registered with the mixer (producing silence when inactive)? If so, state this explicitly — it's the safest approach and avoids any add/remove threading concern.
- Or are backends added/removed dynamically when switching instrument types? If so, the add/remove must happen on the audio thread (inside `Read()`), not from the MIDI/UI thread.

**Recommended fix:** State explicitly in §4.2: "Both backends are added to the mixer at initialization (or on first creation). An inactive backend produces silence via `Array.Clear()` in its `Read()`. Backends are never removed from the mixer — only the `_activeBackend` reference determines which receives MIDI commands." This eliminates the thread-safety concern entirely.

---

### 4. REQUIRED — Bridge crash mid-render: audio thread behavior undefined

**Section:** §3.2, §7.1, §7.2

The proposal identifies bridge crash detection (§7.1: "Named pipe `BrokenPipeException` is the signal") and audio thread blocking (§7.2: "shared memory spin-wait should have a timeout"), but doesn't connect these into a concrete behavior specification.

When the bridge process crashes:

1. The audio thread is inside `Vst3BridgeBackend.Read()`, spin-waiting on shared memory `writeIndex`.
2. The bridge is dead. `writeIndex` will never advance.
3. After the 5ms spin-wait timeout (§7.2), what happens?

The proposal says "output the previous block (repeat) or silence" — but which one? And what about the next `Read()` call? Does `Vst3BridgeBackend` detect that the bridge is dead and switch to a permanent "output silence" mode until restart? Or does it retry IPC every `Read()` call, burning CPU on a dead pipe?

Additionally: the named pipe is used for commands (`MIDI`, `RENDER`, `SHUTDOWN`). If the bridge crashes, the pipe handle is broken. The next `RENDER` command write will throw `IOException`/`BrokenPipeException`. This exception must not propagate to NAudio's audio thread — it would kill the WASAPI output and silence the entire app, including the healthy SF2 backend.

**Recommended fix:** Define three states for `Vst3BridgeBackend`: `Running`, `Faulted`, `Disposed`. When the bridge crashes:
- Catch `IOException`/`BrokenPipeException` in `Read()` → transition to `Faulted`
- In `Faulted` state: `Read()` outputs silence immediately (zero-cost), does not attempt IPC
- `BridgeProcessManager` detects crash independently (process exit event) and can attempt restart
- After successful restart: transition back to `Running`, re-send `INIT`
- Expose a `BridgeFaulted` event for the host to display "plugin crashed" notification

---

### 5. REQUIRED — SoundFontBackend extraction must preserve Volatile and cache patterns explicitly

**Section:** §6.1

§6.1 lists what moves to `SoundFontBackend`: Synthesizer field + Volatile pattern, SoundFont cache, etc. This is correct. But there is no code sketch for `SoundFontBackend` — only for the refactored `AudioEngine`. The patterns that Gren flagged as critical in v1.0 (Volatile swap, SF2 cache with double-checked locking, `using` on FileStream, `NoteOffAll` before synthesizer swap) are described only by name in a bullet list.

Given that these patterns were hard-won (each resolved a specific bug vector identified in review), and that the extraction is a significant refactor touching the audio hot path, a code sketch for `SoundFontBackend` is needed to verify the patterns survive the move.

**Recommended fix:** Add a `SoundFontBackend` code sketch to §6.1 showing at minimum:
- The `Volatile.Read/Write` pattern for `_synthesizer` in `Read()` and `LoadAsync()`
- The `_soundFontCache` with its lock
- The `Read()` method showing command drain (if AudioEngine delegates, this is where synth.NoteOn() calls happen)
- The `Dispose()` method: `NoteOffAll()` → null synthesizer → clear cache

---

### 6. REQUIRED — Shared memory ownership and cleanup on crash

**Section:** §3.2, §7.3

The proposal defines the shared memory layout and naming scheme (`mmk-vst3-audio-{pid}`), but doesn't specify:

- **Who creates the `MemoryMappedFile`?** The host (managed side) or the bridge (native side)? This matters because the creator owns the lifecycle. If the bridge creates it and crashes, the OS reclaims it — but the host still holds a stale `MemoryMappedViewAccessor` that may read garbage.
- **Who creates the named pipe?** Same question. If the bridge is the pipe server and it crashes, the host's `NamedPipeClientStream` will get `IOException` on next write. If the host is the pipe server, the bridge connects as client — simpler crash handling.

**Recommended fix:** State explicitly: "The host creates both the `MemoryMappedFile` and the named pipe server. The bridge connects as a client. On bridge crash, the host retains valid handles and can re-create the bridge process which reconnects to existing IPC resources." This is cleaner than having the bridge own resources that must be cleaned up across process boundaries.

---

### 7. SUGGESTED — `_backendLock` in SelectInstrument may contend with audio thread

**Section:** §4.2

The sketch shows:

```csharp
public void SelectInstrument(InstrumentDefinition instrument)
{
    var backend = ResolveBackend(instrument.Type);
    lock (_backendLock)
    {
        _activeBackend?.NoteOffAll();
        _activeBackend = backend;
    }
    _ = Task.Run(() => backend.LoadAsync(instrument));
}
```

But `NoteOn()` reads `_activeBackend` via `Volatile.Read` (no lock). If the audio thread is draining the command queue and calling `_activeBackend.NoteOn()` between the `NoteOffAll()` and the assignment, commands could be routed to the old backend after `NoteOffAll()` silenced it.

This is a minor race — worst case is a few audible notes on the outgoing backend during a switch, which self-resolve. But it's worth noting.

**Recommended fix:** If the ConcurrentQueue pattern is preserved (Issue #1), the race is eliminated: the queue drain happens on the audio thread, which can atomically swap the active backend reference after draining. No lock needed. The current `lock (_backendLock)` pattern is only necessary if `NoteOn()` is called directly from the MIDI thread.

---

### 8. SUGGESTED — Phase 1 behavioral equivalence guarantee should be explicit

**Section:** §3.4, §7.4

§7.4 says "Phase 1 PR must pass all existing `AudioEngine` tests with zero changes to test code." This is good but insufficient. Tests are not exhaustive — the existing tests verify concurrency, disposal, and graceful degradation, but don't verify audio output correctness. A synthesizer swap regression (e.g., `Volatile.Write` dropped during extraction) would pass all tests but produce silence or wrong instruments.

**Recommended fix:** Add to §3.4 Phase 1 acceptance criteria: "Phase 1 must be a pure refactoring with no observable behavior change for SF2 instruments. This means: (a) all existing tests pass unchanged, (b) manual smoke test confirms audio output is identical for SF2 instrument selection and switching, (c) the Volatile.Read/Write pattern, SoundFont cache, ConcurrentQueue drain, and FileStream `using` block are preserved verbatim — verified by code review before merge."

---

### 9. SUGGESTED — Spike's §7.2 question: spin-wait vs semaphore on audio thread

**Section:** §7.2

Spike asks: "Is spin-waiting on shared memory acceptable on the audio thread, or should we use a semaphore/event?"

**Gren's answer:** Spin-wait with a short timeout (≤2ms) is acceptable and preferred for the audio thread. A kernel semaphore (`EventWaitHandle`, `ManualResetEventSlim`) would require a kernel transition (~1-2μs) and risks priority inversion if the bridge thread is pre-empted while holding the signal. Spin-wait on a volatile `uint32 writeIndex` is the standard pattern for lock-free audio IPC. The timeout must output silence (not stale data) to avoid audible glitches.

---

### 10. SUGGESTED — Open questions 1-5 should have default answers in the proposal

**Section:** §10

The five open questions (bridge language, plugin GUI, audio format, preset management, CLAP) are reasonable to defer, but each should have a stated default/recommendation so implementation doesn't stall waiting for answers:

1. **Bridge language:** C++ (default — largest VST3 SDK ecosystem)
2. **Plugin GUI:** Defer (explicitly out of scope for v1)
3. **Audio format:** Bridge resamples to 48kHz if plugin requests different rate (host format is authoritative)
4. **Preset management:** Load-only for v1 (state save deferred)
5. **CLAP:** Defer (design bridge protocol to be format-agnostic as Spike suggests)

## Approved / Blocked Sections

| Section | Status | Notes |
|---------|--------|-------|
| §1 InstrumentDefinition / InstrumentType | ✅ Approved | Clean, backward-compatible. JSON default behavior verified. |
| §2 IInstrumentBackend interface | ❌ Blocked | Threading contract docstring contradicts §4.2 sketch (Issue #1). Must clarify whether NoteOn/NoteOff are audio-thread-only or MIDI-thread-safe. |
| §3 VST3 hosting approach (Option C) | ✅ Approved | Correct choice for long-running tray app. Latency analysis is sound. |
| §3.2 Bridge protocol | ✅ Approved with conditions | Protocol is clean. Must specify IPC resource ownership (Issue #6). |
| §3.4 Phased delivery | ✅ Approved | Phase 1 first is correct. Add explicit behavioral equivalence criteria (Issue #8). |
| §4 AudioEngine refactoring | ❌ Blocked | Code sketch has threading bug (Issue #1). No Dispose() specification (Issue #2). MixingSampleProvider swap semantics unclear (Issue #3). |
| §5 InstrumentCatalog changes | ✅ Approved | Flat list, typed entries, validation rules are all correct. |
| §6 File/folder structure | ✅ Approved with conditions | Structure is good. SoundFontBackend extraction needs code sketch (Issue #5). |
| §7 Risk areas | ✅ Approved | Good self-awareness. Bridge crash behavior needs to be promoted from "risk" to "specified behavior" (Issue #4). |
| §8 Memory budget | ✅ Approved | Bridge process isolation is a genuine advantage for the memory budget. |
| §9 Backward compatibility | ✅ Approved | Thorough analysis. |
| §10 Open questions | ✅ Approved | Add default answers (Issue #10). |

## Implementation Prerequisites

Before Faye and Jet begin writing code, the following must be true:

### Must be resolved by Spike (proposal revision):

1. **[BLOCKING]** Resolve the threading model contradiction in §4.2 — update the code sketch to show the ConcurrentQueue pattern preserved. State explicitly: "The MIDI callback thread never calls backend methods directly."
2. **[BLOCKING]** Add `Dispose()` method sketches for both refactored `AudioEngine` and `Vst3BridgeBackend` with explicit ordering.
3. **[REQUIRED]** Clarify mixer input swap semantics — are backends always in the mixer (preferred) or dynamically added/removed?
4. **[REQUIRED]** Define `Vst3BridgeBackend` state machine: `Running` → `Faulted` → `Disposed`, with behavior for each state in `Read()`.
5. **[REQUIRED]** Add `SoundFontBackend` code sketch showing preservation of Volatile, cache, and command drain patterns.
6. **[REQUIRED]** Specify IPC resource ownership (host creates MMF + pipe server; bridge connects as client).

### Must be true before Phase 1 implementation starts:

7. All existing `AudioEngine` tests pass on current `main` branch (baseline).
8. Faye understands the ConcurrentQueue drain pattern, Volatile swap pattern, and SF2 cache — these must transfer verbatim to `SoundFontBackend`.
9. Ed reviews Phase 1 PR for pattern preservation before merge.

### Must be true before Phase 2 implementation starts:

10. Phase 1 merged and verified (all tests pass, manual smoke test confirms SF2 playback).
11. Spike's proposal revision addresses Issues #1–#6 from this review.
12. Bridge protocol is agreed (current design is acceptable pending Issue #6 resolution).
