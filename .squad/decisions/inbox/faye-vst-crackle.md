# Faye — VST crackle buffering decision

**Date:** 2026-03-13  
**Status:** Implemented  
**Scope:** VST3 bridge MMF/audio cadence

## Decision

Shift the VST bridge MMF handoff from **960-frame / 20 ms publishes** to **480-frame / 10 ms publishes**, keep a deeper **16-block ring**, prime the managed reader after `load_ack`, and run the native render loop at elevated thread priority.

## Why

The existing path rendered bridge audio in the same coarse 20 ms quantum as the shared-mode WASAPI buffer. That gave the host almost no cadence slack: a slightly late `sleep_until` wake-up or normal Windows scheduler jitter could turn directly into a missing block, heard as crackle/pop. Starting the managed reader from already-published ring contents also made the initial handoff nondeterministic.

## Consequences

- Smaller publish quanta reduce underrun severity and make MMF handoff less sensitive to timing jitter.
- Deeper ring depth preserves safety margin without forcing the bridge to free-run on only one or two blocks.
- Post-load priming skips stale prerendered blocks and starts playback on fresh plugin output.
- Elevated native render-thread priority lowers the chance of missed publishes during normal desktop contention.
