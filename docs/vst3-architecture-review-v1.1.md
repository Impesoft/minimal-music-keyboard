# VST3 Architecture Proposal — Gren's Re-Review (v1.1)

**Reviewer:** Gren (Supporting Architect)  
**Date:** 2026-03-11  
**Document reviewed:** `docs/vst3-architecture-proposal.md` v1.1 (Spike)  
**Previous review:** `docs/vst3-architecture-review.md` (v1.0 — APPROVED WITH CONDITIONS)  
**Verdict:** APPROVED

---

## Resolution Check

### Issue 1: Threading model contradiction — ✅ RESOLVED

**What I looked for:** The §4.2 code sketch must show MIDI thread enqueuing to `ConcurrentQueue` only — never calling backend methods directly. The `IInstrumentBackend` interface docstring must state that `NoteOn`/`NoteOff`/`SetProgram` are audio-thread-only. The rule "The MIDI callback thread never calls backend methods directly" must be stated explicitly.

**What I found:** The v1.1 revision corrects all three locations. The `IInstrumentBackend` docstring (§2.1) now states: "NoteOn/NoteOff/SetProgram are called from the AUDIO RENDER THREAD ONLY. AudioEngine drains its ConcurrentQueue during ReadSamples() and dispatches to the active backend on the audio thread. The MIDI callback thread never calls backend methods directly." The §4.2 code sketch shows `NoteOn()` and `NoteOff()` enqueuing to `_commandQueue` only — no direct backend calls. The §4.3 threading model diagram is consistent: `MIDI thread → ConcurrentQueue → Audio thread drains queue → activeBackend.NoteOn/Off()`. The §2.2 design decisions section reiterates the rule. The contradiction is eliminated. All three sources of truth agree.

---

### Issue 2: Dispose() specifications — ✅ RESOLVED

**What I looked for:** Full ordered `Dispose()` method sketches for both the refactored `AudioEngine` and `Vst3BridgeBackend`, with explicit rationale for ordering.

**What I found:** §4.5 adds a dedicated "Disposal Sequence" section with complete code sketches for both components. `AudioEngine.Dispose()` follows the exact order I specified: (1) set `_disposed = true`, (2) silence active voices via `NoteOffAll()`, (3) stop and dispose WASAPI output — critically before disposing backends, with the correct rationale (audio thread calls `backend.Read()` — disposing a backend first is use-after-dispose), (4) dispose VST3 backend, (5) dispose SF2 backend, (6) dispose mixer if `IDisposable`. `Vst3BridgeBackend.Dispose()` specifies: (1) set `_state = Disposed`, (2) send `SHUTDOWN` over pipe (best-effort, catches `IOException` for already-broken pipe), (3) wait 3 seconds for graceful exit, (4) force-kill via `Process.Kill()`, (5) close pipe, (6) dispose `MemoryMappedViewAccessor`, (7) dispose `MemoryMappedFile`. The ordering is correct — IPC resources are released only after the bridge process is confirmed dead or killed.

---

### Issue 3: Mixer swap semantics — ✅ RESOLVED

**What I looked for:** Explicit statement that both backends are permanently registered with the mixer, never removed. Inactive backends produce silence via `Array.Clear()`. No dynamic `AddMixerInput()`/`RemoveMixerInput()` during normal operation.

**What I found:** §4.2 includes a comment block (lines 305–308) that states exactly this: "Both backends are added to the mixer at initialization (or on first creation). They are NEVER removed. An inactive backend's Read() simply calls Array.Clear() and returns silence. The _activeBackend reference determines which backend receives MIDI commands — it is NOT the mixer input list." The `Vst3BridgeBackend` is lazily created (first VST3 instrument selection triggers `CreateAndRegisterVst3Backend()`), which means `AddMixerInput()` is called once at creation time — not dynamically on every instrument switch. This is acceptable; the one-time add is a benign thread-safety concern since NAudio's `AddMixerInput()` uses an internal lock. After that, no further mixer mutations occur.

---

### Issue 4: Bridge crash state machine — ✅ RESOLVED

**What I looked for:** Three defined states (`Running`, `Faulted`, `Disposed`) with explicit behaviors for `Read()` and `NoteOn()`/`NoteOff()` in each state. A `BridgeFaulted` event. A crash-to-recovery flow.

**What I found:** §7.1 defines the complete state machine. The `BridgeState` enum has three values: `Running`, `Faulted`, `Disposed`. A state behavior table specifies: (a) `Running` — normal IPC, transitions to `Faulted` on `IOException`/`BrokenPipeException`; (b) `Faulted` — `Read()` immediately outputs silence via `Array.Clear()` (zero-cost, no IPC), `NoteOn()`/`NoteOff()` are no-ops, transitions to `Running` after successful restart; (c) `Disposed` — terminal state, all methods are no-ops. The `BridgeFaulted` event is declared as `EventHandler<string>`. The crash flow is a 7-step sequence: bridge crashes → `Read()` catches exception → set `Faulted` → raise event → tray tooltip → `BridgeProcessManager` detects via `Process.Exited` → user-triggered or auto restart reconnects to host-owned IPC. This is exactly what I specified.

---

### Issue 5: SoundFontBackend code sketch — ✅ RESOLVED

**What I looked for:** Full code sketch showing: (a) `Volatile.Read`/`Write` pattern for `_synthesizer` in `Read()` and `LoadAsync()`, (b) `_soundFontCache` with double-checked locking, (c) `Read()` showing command queue drain + `synth.Render()`, (d) `Dispose()` with correct teardown order.

**What I found:** §6.1 provides a 110-line code sketch for `SoundFontBackend`. Verified against the live `AudioEngine.cs` — all critical patterns survive the extraction:

| Pattern | Live AudioEngine.cs | Proposal SoundFontBackend |
|---------|-------------------|---------------------------|
| Volatile.Read in Read() | Line 246: `Volatile.Read(ref _synthesizer!)` | Line 604: `Volatile.Read(ref _synthesizer)` ✅ |
| ConcurrentQueue drain | Lines 259–269: while loop with switch | Lines 612–628: identical pattern ✅ |
| SoundFont cache + lock | Lines 214–235: double-checked locking | Lines 646–665: identical pattern ✅ |
| FileStream `using` | Line 224: `using (var stream = File.OpenRead(path))` | Line 655: `using (var stream = new FileStream(...))` ✅ |
| Volatile.Write on swap | Line 194: `Volatile.Write(ref _synthesizer!, newSynth)` | Line 643: `Volatile.Write(ref _synthesizer, newSynth)` ✅ |
| Dispose: NoteOffAll → null → clear | Lines 296–318 | Lines 683–691 ✅ |

The `LoadAsync()` adds an explicit `oldSynth?.NoteOffAll()` before the Volatile.Write swap (line 641–643), which is an improvement over the current code — it silences the old synthesizer before replacing it, preventing a brief overlap of voices from both synthesizers.

---

### Issue 6: IPC resource ownership — ✅ RESOLVED

**What I looked for:** Explicit statement of who creates the `MemoryMappedFile` and named pipe. Recommended: host creates both, bridge connects as client.

**What I found:** §3.2 adds a dedicated paragraph: "The host (`Vst3BridgeBackend`) creates both the `MemoryMappedFile` and the `NamedPipeServerStream`. The bridge process connects to both as a client (`NamedPipeClientStream` + `MemoryMappedFile.OpenExisting`). This ensures the host retains valid handles on bridge crash. A crashed bridge does not orphan IPC resources. On restart, the new bridge process connects to the existing host-owned resources — no re-creation needed." The naming scheme `mmk-vst3-audio-{hostPid}` uses the **host's** PID, which is a good detail — the name is stable across bridge restarts, meaning the host doesn't need to recreate the `MemoryMappedFile` on bridge crash recovery.

---

## Implementation Note (Non-Blocking)

The `SoundFontBackend.Read()` sketch drains the `ConcurrentQueue<MidiCommand>` unconditionally (lines 611–628), but the threading invariant at §4.2 states "The `_activeBackend` reference determines which backend drains the shared `ConcurrentQueue` — inactive backends skip the drain and output silence." Since both backends are permanently in the mixer, both `Read()` methods are called by `MixingSampleProvider` every audio callback. If the inactive backend also drains the queue, commands will be consumed by the wrong backend.

The invariant is correctly stated — only the active backend should drain. But the enforcement mechanism is not shown in the sketch. During implementation, Faye should resolve this by either: (a) having `AudioEngine` drain the queue in its own `ReadSamples()` wrapper and call `_activeBackend.NoteOn()` directly (matching the current pattern), or (b) passing the `_activeBackend` reference to each backend so it can check `if (this != activeBackend) return silence`. Option (a) is simpler and preserves the existing `AudioEngine.ReadSamples()` structure.

This is not a blocking concern — the threading safety invariant is correct, and the resolution is straightforward during implementation.

---

## Final Verdict

The proposal is **APPROVED**. All six issues from the v1.0 review have been addressed satisfactorily. Phase 1 implementation may begin. Faye is cleared to start the `IInstrumentBackend` + `SoundFontBackend` + `AudioEngine` refactor. Implementation must follow the threading model and `Dispose()` sequences exactly as specified in v1.1.

---

## Implementation Prerequisites (if APPROVED)

### Before Phase 1 code is committed to main:

- [ ] All existing `AudioEngine` tests pass on current `main` branch (baseline established)
- [ ] Faye understands the ConcurrentQueue drain pattern, Volatile.Read/Write swap pattern, and SF2 cache — these must transfer verbatim to `SoundFontBackend`
- [ ] The queue-drain ownership is resolved: either AudioEngine drains and dispatches (recommended), or backends check active status before draining (see Implementation Note above)
- [ ] Phase 1 PR passes all existing `AudioEngine` tests with zero changes to test code
- [ ] Manual smoke test confirms SF2 audio output is identical after refactor
- [ ] Ed reviews Phase 1 PR for pattern preservation before merge (Volatile, cache, queue drain, FileStream `using`)

### Before Phase 2 code is committed to main:

- [ ] Phase 1 merged and verified (all tests pass, smoke test confirmed)
- [ ] Bridge protocol implementation matches §3.2 (host creates IPC resources, bridge connects as client)
- [ ] `Vst3BridgeBackend` state machine matches §7.1 (Running → Faulted → Disposed)
- [ ] `Vst3BridgeBackend.Dispose()` follows the exact sequence in §4.5
- [ ] Bridge crash recovery tested: kill bridge process → verify host outputs silence → restart → verify audio resumes
