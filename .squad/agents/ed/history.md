# Ed — History

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

**Ed's QA focus:**
- Long-running stability (app in tray for hours/days without degrading)
- Resource disposal verification (all IDisposable patterns complete)
- MIDI device lifecycle (plug/unplug during operation)
- Concurrent access safety (MIDI thread vs. UI thread vs. audio thread)
- Rapid instrument switching, sustained chord bursts, edge case MIDI messages

## Learnings

<!-- append new learnings below -->
