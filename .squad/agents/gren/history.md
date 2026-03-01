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
