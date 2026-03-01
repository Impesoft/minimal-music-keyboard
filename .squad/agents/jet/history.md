# Jet — History

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

**Jet's implementation focus:**
- MIDI device discovery and I/O (Windows.Devices.Midi2 / NAudio / RtMidi.NET — TBD)
- System tray integration (H.NotifyIcon for WinUI3 or similar)
- Single-instance enforcement
- On-demand settings window lifecycle
- Graceful shutdown: stop MIDI thread → dispose audio engine → destroy tray icon

## Learnings

<!-- append new learnings below -->
