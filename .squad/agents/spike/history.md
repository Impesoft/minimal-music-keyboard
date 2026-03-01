# Spike — History

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

**Key architectural constraints:**
- Minimal memory footprint when idle in tray (target: <50MB)
- Proper disposal of all resources (MIDI handles, audio engine, COM objects)
- WinUI3 settings window created on-demand, not kept alive when hidden
- Single-instance application
- Background MIDI listening thread must not block clean process exit

## Learnings

<!-- append new learnings below -->
