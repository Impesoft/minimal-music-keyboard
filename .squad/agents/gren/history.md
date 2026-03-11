# Gren — History

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

**Gren's architectural focus areas:**
- All IDisposable chains are complete and verified
- No lingering event subscriptions (event handler leaks are a common WinUI3 trap)
- MIDI device handles properly released on device disconnect or app exit
- Audio engine teardown sequence is correct and tested
- Thread safety between MIDI, audio, and UI threads

## Learnings

<!-- append new learnings below -->
### 2026-03-01 — Architecture Review v1.0

**Key concerns raised:**

1. **Disposal order inconsistency caught** — Section 3.1 said "reverse order" but Section 6 had a different (correct) order. Lesson: when disposal order is critical, define it in ONE canonical place and reference it elsewhere. Don't duplicate.

2. **WinUI3 windows are COM-backed** — you cannot "let GC collect" a WinUI3 Window and expect clean resource release. Event subscriptions on long-lived services from a Window create leak vectors. Always unsubscribe explicitly and null the reference. Track open windows so shutdown can close them.

3. **MeltySynth thread safety is assumed, not verified** — the architecture assumed NoteOn/NoteOff is safe to call concurrently with Render based on community usage patterns, but this isn't documented by the author. Before implementation, the team must inspect MeltySynth source or add synchronization. Flagged as high-severity.

4. **Volatile semantics for reference swaps across threads** — replacing a Synthesizer instance reference from a background thread while the audio thread reads it requires `Volatile.Read`/`Volatile.Write`. A plain field read can be cached by JIT. This is a subtle but real bug vector in .NET.

5. **MIDI device disconnect is a first-class concern for always-on apps** — USB devices WILL be disconnected during normal use. The architecture must handle this gracefully from day one, not as an afterthought. Designed a Disconnected state machine pattern.

6. **Bank Select accumulation is standard MIDI** — no timeout needed. CC#0/CC#32 values are sticky per the MIDI spec until the next PC message consumes them. This is correct as-designed.

### 2026-03-15 — Architecture Doc v1.1 Verification

**Verified implementation against architecture doc — found significant improvements:**

1. **ConcurrentQueue pattern eliminates thread-safety risk** — The draft architecture assumed MeltySynth's NoteOn/NoteOff could be called from MIDI thread while Render runs on audio thread. The actual implementation is smarter: MIDI thread enqueues commands via `ConcurrentQueue<MidiCommand>`, audio thread dequeues and processes them, then renders. All Synthesizer calls happen on a single thread. This is the **correct pattern for real-time audio** and eliminates the thread-safety concern I flagged in v1.0.

2. **Naming evolved: MidiMessageRouter → MidiInstrumentSwitcher** — Architecture draft used `MidiMessageRouter` but actual implementation is `MidiInstrumentSwitcher`. The new name is more descriptive (it switches instruments, not just routes messages). Updated doc to reflect reality.

3. **Folder structure simplified** — Draft had separate `Audio/`, `Instruments/`, `Tray/`, `Settings/` folders. Actual implementation consolidates most components under `Services/`. This is cleaner — fewer top-level folders, easier to navigate. `Models/` and `Interfaces/` are properly separated.

4. **Reconnect polling implemented correctly** — MidiDeviceService implements disconnect detection + reconnect polling with CancellationTokenSource. This addresses the concern I raised in v1.0 about MIDI disconnect handling. Well done.

5. **Lesson: Verify architecture against code after initial implementation** — Architecture docs can drift from reality during rapid development. Periodic verification catches naming changes, structural evolution, and implementation improvements that strengthen the design. This verification found the ConcurrentQueue pattern (an improvement over the draft) and corrected naming mismatches before they caused confusion.

6. **SettingsWindow pattern discrepancy** — Architecture doc says "tracked, disposed on close" but actual code says "create once, reuse forever (show/hide)". This is acceptable for a rarely-opened window, but event handler leaks remain a risk. Flagged for future audit, not blocking.

### 2026-07-17 — VST3 Architecture Proposal Review

**Reviewed:** `docs/vst3-architecture-proposal.md` v1.0 (Spike)  
**Research reviewed:** `docs/vst3-dotnet-options.md` (Faye)  
**Verdict:** APPROVED WITH CONDITIONS  
**Full review:** `docs/vst3-architecture-review.md`

**Key findings:**

1. **BLOCKING: Threading model contradiction in §4.2 vs §4.3** — The code sketch calls `backend.NoteOn()` directly from the MIDI thread, bypassing the ConcurrentQueue pattern that Faye's Decision 1 established. The threading model diagram says commands go through the queue. These can't both be right. MeltySynth's thread safety is unverified — the queue is the safety mechanism. Spike must correct the sketch to show the queue drain pattern preserved.

2. **BLOCKING: No Dispose() specification for refactored AudioEngine or Vst3BridgeBackend** — The proposal refactors the two most resource-sensitive components without defining their teardown sequences. The current AudioEngine.Dispose() order (silence → stop WASAPI → null synth → clear cache) is critical; the new version must have an equivalent specification. Vst3BridgeBackend.Dispose() must define: SHUTDOWN message → wait with timeout → kill fallback → close pipe → release shared memory.

3. **REQUIRED: MixingSampleProvider swap semantics** — The proposal doesn't clarify whether backends are always in the mixer (producing silence when inactive) or dynamically added/removed. Recommended: always-in-mixer approach eliminates thread-safety concerns with NAudio's `AddMixerInput()`/`RemoveMixerInput()` during concurrent `Read()`.

4. **REQUIRED: Bridge crash mid-render behavior undefined** — What does `Vst3BridgeBackend.Read()` do when the bridge process is dead? Need a state machine: Running → Faulted (output silence) → Disposed. Exceptions from broken pipe must not propagate to the WASAPI audio thread.

5. **REQUIRED: SoundFontBackend needs a code sketch** — The Volatile.Read/Write swap, SF2 cache with double-checked locking, and FileStream `using` pattern were hard-won in v1.0 review. A bullet list saying "these move" is insufficient — need a sketch proving the patterns survive the extraction.

6. **REQUIRED: IPC resource ownership unspecified** — Who creates the MemoryMappedFile and named pipe? Recommended: host creates both, bridge connects as client. Simplifies crash cleanup.

7. **Spike's §7.2 question answered** — Spin-wait with ≤2ms timeout is acceptable on the audio thread. Preferred over kernel semaphore (avoids priority inversion). Timeout must output silence, not stale data.

**Lesson learned:** When reviewing architecture proposals that refactor existing code, always verify that the proposal includes code sketches for ALL new components — not just the ones that change shape. The `SoundFontBackend` extraction is "just a move" but moves are where patterns get accidentally dropped.

### 2026-03-11 — VST3 Architecture Proposal Re-Review (v1.1)

**Reviewed:** `docs/vst3-architecture-proposal.md` v1.1 (Spike)  
**Verdict:** APPROVED  
**Full review:** `docs/vst3-architecture-review-v1.1.md`

**Key findings:**

1. **All 6 issues from v1.0 review resolved** — Spike addressed both BLOCKING issues (threading model contradiction, missing Dispose() specs) and all 4 REQUIRED issues (mixer swap semantics, bridge crash state machine, SoundFontBackend sketch, IPC resource ownership). Every fix matched or exceeded the original recommendation.

2. **Pattern verification against live code is essential** — Compared the SoundFontBackend sketch line-by-line against `AudioEngine.cs`. All critical patterns (Volatile.Read/Write, ConcurrentQueue drain, SoundFont cache with double-checked locking, FileStream `using`) survived the extraction. The LoadAsync() improvement (explicit NoteOffAll before swap) is a net positive.

3. **Queue-drain ownership ambiguity flagged but non-blocking** — The SoundFontBackend sketch drains the ConcurrentQueue unconditionally in Read(), but the invariant says only the active backend should drain. Both backends are in the mixer, so both Read() methods are called. Flagged as implementation note for Faye — recommend AudioEngine drains and dispatches (preserving current pattern) rather than backends self-draining.

4. **IPC naming scheme detail matters** — Using host PID (not bridge PID) in `mmk-vst3-audio-{hostPid}` means the shared memory name is stable across bridge restarts. The host doesn't need to recreate MemoryMappedFile on crash recovery. Small detail, big operational benefit.

**Lesson learned:** When a proposal revision addresses all blocking issues cleanly, look for second-order issues that emerge from the fixes. The queue-drain ambiguity wasn't visible in v1.0 because the threading model was contradictory — once the model was clarified, the "who drains the queue" question became visible. Approval doesn't mean perfection — flag implementation notes for the coding team.

### 2026-03-11 — Phase 1 Code Review (REJECTED)

**Reviewed:** Phase 1 implementation (Faye) — IInstrumentBackend, SoundFontBackend, AudioEngine refactor, InstrumentDefinition changes
**Verdict:** REJECTED — 1 blocking + 2 required fixes
**Full review:** `docs/phase1-code-review.md`

**Key findings:**

1. **BLOCKING: Compilation error in AudioEngine.cs line 177** — Extra closing parenthesis in `LoadSoundFont()` method: `}));` instead of `});`. Single-character fix but prevents the entire project from building. Always build-verify before submitting for review.

2. **REQUIRED: Interface-level threading contract missing from IInstrumentBackend** — Per-method docs correctly say "audio render thread only" but the interface summary omits the full ConcurrentQueue pattern that proposal §2.1 specifies. Phase 2 implementers need this front and center.

3. **REQUIRED: Unused ConcurrentQueue field in SoundFontBackend** — Faye correctly chose option (a) from my v1.1 implementation note (AudioEngine drains queue and dispatches), but left the queue injection from the proposal sketch. Dead fields that imply the wrong threading model are worse than no field at all.

4. **Architecture is sound** — Every significant design decision was correct: queue-drain ownership, Volatile swap preservation, SF2 cache pattern, LoadSoundFont retention for backward compat, volume in ReadSamples wrapper. The issues are mechanical, not architectural.

**Lesson learned:** When reviewing a refactor that extracts code from class A to class B, verify that artifacts from abandoned design alternatives (like a constructor parameter for a pattern that was ultimately rejected) don't survive into the implementation. Code sketches in proposals are starting points — the implementation may correctly deviate, but must clean up remnants of the sketch's approach. Also: always build the code before reviewing. A compilation error should have been caught before review was requested.

### 2026-03-11 — Phase 1 Re-Review (APPROVED)

**Reviewed:** Jet's 3 fixes to Phase 1 implementation
**Verdict:** APPROVED — all 3 issues resolved cleanly

**Key findings:**

1. **Jet's fixes were precisely scoped** — Each fix addressed exactly one issue from the rejection, with no collateral changes. The stray paren removal, `<remarks>` additions, and unused field cleanup were all surgically applied. This is how mechanical fixes should be done.

2. **Build verification is non-negotiable** — Confirmed 0 errors, 0 warnings post-fix. The original BLOCKING issue was a compilation failure; verifying the build is the first and most important check on re-review.

3. **Lockout rule worked as intended** — Faye wrote the original code, Jet applied the fixes. Fresh eyes on mechanical fixes prevents "blind spot" recurrence where the original author might miss the same issue twice.

**Lesson learned:** When re-reviewing fixes to a rejected review, verify each fix individually against the original issue description, then do a holistic check for regressions. Don't assume "3 small fixes = no risk." Even mechanical changes can introduce issues if applied to the wrong line or with incorrect scope. Build verification is the final gate.

### 2026-03-11 — Phase 2 Re-Review (APPROVED)

**Reviewed:** Jet's 3 fixes to Phase 2 implementation (Vst3BridgeBackend)
**Verdict:** APPROVED — all 3 issues resolved cleanly

**Key findings:**

1. **IPC ownership correctly reversed** — Host is now `NamedPipeServerStream` + `MemoryMappedFile.CreateNew()`, using host PID in all resource names. The flow (create IPC → launch bridge → WaitForConnectionAsync → send load → await ack) matches the approved spec §3.2 exactly. This is the most critical fix: the Phase 3 bridge can now be designed as a client connecting to host-owned resources, which was the original architectural intent.

2. **Audio thread is now zero-allocation** — `Channel<MidiCommand>` with a `readonly struct` replaces `Channel<string>`. NoteOn/NoteOff/NoteOffAll/SetProgram write struct values into the channel. `SerializeCommand()` is confined to `RunPipeWriterAsync` (background drain task). The struct contains `string?` fields for Load/Shutdown, but those commands are never called from the audio thread, so no allocation concern.

3. **Dispose shutdown sequence is correct** — `Writer.Complete()` causes `ReadAllAsync` to drain remaining items (including shutdown command) and exit naturally. The 500ms drain wait + 2s bridge grace period + force-kill fallback honours the spec's shutdown contract. CTS cancellation is cleanup-only, called after the task has exited. Subtle but important: `Complete()` does NOT cancel the enumeration — it signals end-of-stream, allowing the drain to finish.

4. **Build warnings are acceptable** — Two warnings for unused `_frameSize` field. This is a Phase 3 placeholder for ring buffer spin-wait sync (spec §7.2). Removing now would be churn; Phase 3 will use it.

**Lesson learned:** When a struct carries reference-type fields (like `string?`), verify that those fields are only populated on the intended thread. In `MidiCommand`, `Path` and `Preset` are set only for `Load` (called from LoadAsync, off-audio-thread) and default to `null` for audio-thread commands. The design is correct, but it requires discipline from future maintainers — a comment or code review note is warranted if anyone adds new command kinds that set these fields from the audio thread.

### 2026-03-11 — Phase 2 Code Review (REJECTED)

**Reviewed:** Phase 2 implementation (Faye) — Vst3BridgeBackend.cs, BridgeFaultedEventArgs.cs
**Verdict:** REJECTED — 1 blocking + 2 required fixes
**Full review:** `docs/phase2-code-review.md`

**Key findings:**

1. **BLOCKING: IPC resource ownership reversed** — The approved spec (v1.1 §3.2) explicitly states the host creates both the named pipe server and the MMF, using the host PID in resource names. The implementation reverses this: the host is a `NamedPipeClientStream` connecting to a bridge-owned pipe, and opens the MMF with `OpenExisting` (bridge creates it). Resource names use bridge PID instead of host PID. This is not a "either way works" choice — it was a deliberate architectural decision for crash recovery (stable names across bridge restarts, host retains handles on bridge crash). Getting this wrong in Phase 2 would propagate incorrect assumptions into Phase 3 bridge design.

2. **REQUIRED: String allocation on audio render thread** — NoteOn, NoteOff, and SetProgram use C# string interpolation to build JSON commands, allocating a new string on every call. The class documentation explicitly says "no allocations, no blocking" but every MIDI event allocates. Fix: change `Channel<string>` to `Channel<BridgeCommand>` with a readonly struct, serialize to JSON in the background writer task.

3. **REQUIRED: Dispose() race prevents graceful shutdown** — The disposal sequence calls `TryComplete()` → `Cancel()` → `Kill()` in rapid succession. Cancelling the writer CTS can prevent the shutdown command from being drained and sent to the bridge. The spec says wait 3s for graceful exit then force-kill; the implementation kills immediately with no grace period. Fix: wait for writer task drain, then give the bridge a grace period before killing.

4. **Architecture is sound** — The state machine (5 states with lock-based transitions), silence-fill paths (3 guards in Read()), NoteOffAll resilience during teardown, Channel configuration (SingleReader, no synchronous continuations), and bridge-absent graceful handling are all well-designed. The issues are about spec compliance and audio-thread discipline, not structural.

**Lesson learned:** When an architecture spec makes an explicit "who owns what" decision (like IPC resource ownership), verify the implementation matches that decision character-for-character. Ownership model reversals are subtle — the code "works" either way in isolation, but the assumptions propagate to the other side of the IPC boundary (the Phase 3 bridge). By the time the mismatch is discovered across a process boundary, it's much harder to fix. Always check resource creation vs. resource connection directionality.

### 2026-03-11 — Phase 4 Code Review (REJECTED)

**Reviewed:** Phase 4 implementation (Jet) — Settings UI for VST3 instrument configuration
**Verdict:** REJECTED — 2 required fixes, assigned to Ed
**Full review:** `docs/phase4-code-review.md`

**Key findings:**

1. **REQUIRED: `InstrumentDefinition` properties use `set` instead of `init`** — `Type`, `SoundFontPath`, `Vst3PluginPath`, `Vst3PresetPath` were given `set` accessors. The record is documented as immutable and its references cross thread boundaries via `Volatile.Read`/`Volatile.Write`. All call sites use `with` expressions which work with `init`. The `set` accessor is unnecessarily permissive and removes the compile-time guard against accidental mutation of shared instances. Fix: change to `init`.

2. **REQUIRED: VST3 slot ProgramNumber collides with SF2 instruments** — New VST3 instruments use `ProgramNumber = slotIdx` (0–7), colliding with SF2 defaults in `_byProgramNumber` (last-writer-wins dictionary). `MidiInstrumentSwitcher.OnProgramChangeReceived()` uses `GetByProgramNumber()` — configuring any VST3 slot makes the corresponding SF2 instrument unreachable via MIDI program change. Fix: guard `_byProgramNumber` inserts with `inst.Type == InstrumentType.SoundFont` in all three catalog rebuild loops.

3. **UI implementation is solid** — Type toggle with RadioButtons, panel visibility switching, FileOpenPicker initialization (WinRT.Interop), AudioEngine backend routing (Volatile.Write + LoadAsync), disposal chain, and both-backends-in-mixer pattern are all correct.

4. **Bridge-ready status indicator deferred** — Not implemented; reasonable since Phase 3 bridge doesn't exist yet. Tracked as future work.

**Lesson learned:** When a polymorphic type (SF2 + VST3) shares a lookup key that was designed for one variant (ProgramNumber for SF2), adding the second variant can silently corrupt the lookup for the first. Dictionary-backed indexes are especially dangerous here because they fail silently (overwrite, no exception). Always check whether shared data structures assume homogeneous types when adding a new discriminated variant. The fix is simple — filter by type when populating variant-specific indexes — but the bug is invisible without tracing the data flow from UI → catalog → MIDI switcher.

### 2026-03-11 — Phase 3 Code Review (APPROVED)

**Reviewed:** Phase 3 implementation (Faye) — mmk-vst3-bridge C++ scaffold (12 files)
**Verdict:** APPROVED — 0 blocking, 0 required, 6 non-blocking notes
**Full review:** `docs/phase3-code-review.md`

**Key findings:**
1. **IPC direction correct** — Bridge uses `CreateFileW` (client) to connect to host's named pipe; uses `OpenFileMappingW` to open host-created MMF. Host PID in all resource names. Matches spec §3.2 and approved Phase 2.
2. **MMF header byte-compatible** — Magic `0x4D4D4B56`, version=1, frameSize, writePos at offsets 0/4/8/12, stereo float32 audio at offset 16. `InterlockedExchange` for atomic writePos update.
3. **JSON protocol complete** — All 6 commands handled. Load ack format matches `ParseLoadAck` on the managed side.
4. **Threading model clean** — Main thread runs JSON command loop, dedicated render thread fills MMF. Lock-free SPSC ring buffer with correct acquire/release memory ordering.
5. **RAII consistent** — All three resource classes have destructors that release handles.
6. **CMake/vcpkg correct** — cmake_minimum_required(3.20), C++20, nlohmann-json via vcpkg, VST3 SDK documented as TODO.

**Non-blocking notes (6):** Shutdown guard short-circuit, Global MMF namespace probe unnecessary, char-by-char ReadLine acceptable, silent JSON parse failure, PING/PONG deferred, no MMF size validation.

**Lesson learned:** When reviewing a cross-process IPC scaffold, the single most important check is verifying that both sides agree on: (a) who creates vs. who connects to each resource, (b) the exact byte layout of shared memory, and (c) the wire format of every message including ack fields. If any of these diverge, the bug is invisible in isolation and only manifests when both processes run together. Phase 3 passes all three checks against the approved Phase 2 implementation.

### 2026-03-11 — Phase 4 Re-Review (APPROVED)

**Reviewed:** Ed's fixes to Phase 4 (Settings UI for VST3) — originally authored by Jet
**Original verdict:** REJECTED (2 required issues)
**Re-review verdict:** APPROVED

**Issue 1 (init immutability):** Resolved. All 9 properties on `InstrumentDefinition` now use `{ get; init; }`. Zero `set` accessors remain. Object initializer and `with` expression call sites are compatible.

**Issue 2 (program number collision):** Resolved. All four `_byProgramNumber` rebuild loops guard with `inst.Type == InstrumentType.SoundFont`. VST3 instruments excluded from program-number index. Ed chose option (b) — type-filtered insert — which is the architecturally cleaner approach.

**Full conformance check passed:** No additional blocking or required issues. AudioEngine threading discipline (Volatile.Read/Write), disposal chain, both-backends-in-mixer pattern, and UI elements (type toggle, file pickers, panel switching) all correct.

### 2026-03-12 — Phase 3b Code Review (APPROVED)

**Reviewed:** Phase 3b implementation (Faye) — VST3 SDK integration in mmk-vst3-bridge
**Verdict:** APPROVED — 0 blocking, 0 required, 4 non-blocking notes
**Full review:** `docs/phase3b-code-review.md`

**Key findings:**
1. **VST3 lifecycle correct** — Module::create, factory scan for kVstAudioEffectClass, IComponent::initialize, setupProcessing, setActive, setProcessing — all in correct order with matching reverse teardown in ResetPluginState.
2. **Resource management leak-free** — IPtr<T> for component/processor, Module::Ptr for module, Steinberg::owned() for QI result. Module held alive while plugin is in use.
3. **Thread safety adequate** — eventsMutex_ protects pending events, pluginMutex_ protects plugin state. Consistent lock order (pluginMutex_ → eventsMutex_) prevents deadlock.
4. **Wire protocol compatible** — `"ack":"load_ack"` accepted by managed-side ParseLoadAck (line 501 accepts both "load" and "load_ack").
5. **Graceful degradation verified** — FillBuffer fills silence first, returns early if no processor. Load failures return error via IPC ack without crashing.

**Non-blocking notes (4):** Null host context in initialize(), std::mutex on render thread (acceptable for current workload), frameSize/kMaxBlockSize alignment assumption, silent preset failure on stderr only.

**Lesson learned:** When reviewing VST3 hosting code, the three most critical checks are: (a) the initialization sequence matches the SDK's required lifecycle (initialize → setupProcessing → setActive → setProcessing, with exact reverse on teardown), (b) COM references are managed via smart pointers (IPtr/owned) not raw release() calls — a single missed release() is an invisible leak, and (c) the process() call receives properly structured ProcessData with pre-allocated buffers on the render thread to avoid heap allocation in the audio path. This implementation passes all three.
