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
