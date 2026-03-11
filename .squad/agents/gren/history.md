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
