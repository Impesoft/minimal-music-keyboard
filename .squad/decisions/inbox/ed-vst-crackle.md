# Ed — VST crackle triage

**Date:** 2026-03-13  
**Status:** Diagnosed / QA recommendation  
**Requested by:** Ward Impe

## Decision

Treat current VST crackle/pop triage as a **timing-domain problem first**, not a stale-buffer replay problem.

## Why

1. **Producer/consumer clocks are independent.**  
   The native bridge renders on its own thread and paces itself with `std::this_thread::sleep_until(...)` in `src\mmk-vst3-bridge\src\audio_renderer.cpp:832-848`.  
   The managed app consumes audio on the WASAPI callback thread via `AudioEngine.ReadSamples()` / `Vst3BridgeBackend.Read()` in `src\MinimalMusicKeyboard\Services\AudioEngine.cs:245-289` and `src\MinimalMusicKeyboard\Services\Vst3BridgeBackend.cs:149-215`.

2. **Current MMF contract is a real ring, not a single stale block.**  
   The host publishes version-2 MMF metadata with `frameSize`, `ringCapacity`, and `writeCounter` in `src\MinimalMusicKeyboard\Services\Vst3BridgeBackend.cs:291-308`.  
   The bridge writes complete blocks into per-slot ring storage before atomically advancing `writeCounter` in `src\mmk-vst3-bridge\src\mmf_writer.cpp:46-65`.

3. **Reader behavior on starvation is silence/drop, not replay.**  
   `Vst3BridgeBackend.Read()` consumes unread ring blocks and zero-fills any remainder when no newer block is available; it does not intentionally reuse the previous block (`src\MinimalMusicKeyboard\Services\Vst3BridgeBackend.cs:175-207`).

4. **Block size is currently aligned in code.**  
   The host creates MMF blocks at 960 mono frames and the bridge configures `maxSamplesPerBlock = 960`, then renders/sleeps using `frameSize_` (`src\MinimalMusicKeyboard\Services\Vst3BridgeBackend.cs:293-308`, `src\mmk-vst3-bridge\src\audio_renderer.cpp:523-528`, `832-848`).

## Consequences

- **Most likely audible failure signatures:**
  - bridge late → zero-filled gaps / hard pops
  - bridge early for long enough → ring overflow and dropped blocks / discontinuities
  - variable scheduling latency → irregular crackle under load even if average throughput is correct

- **Least likely of the requested hypotheses:** stale-buffer replay from the current MMF logic.

## QA recommendation

Instrument the existing code before changing architecture:

1. Count every zero-fill in `Vst3BridgeBackend.Read()`
2. Count every `unreadBlockCount > _ringCapacity` overflow event
3. Log writeCounter/readBlockCounter deltas and time between published blocks
4. Correlate crackle timestamps with those counters during a sustained-note soak test

That evidence will distinguish:
- underrun (`zero-fill` spikes),
- overflow/drift (`overflow` spikes),
- block mismatch (systematic periodic artifacts from first playback even with stable counters),
- scheduling jitter (irregular counter gaps with no static pattern).
