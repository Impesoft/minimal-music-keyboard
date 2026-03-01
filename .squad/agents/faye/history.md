# Faye — History

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

**Faye's audio focus:**
- Instrument catalog schema and soundfont management (SF2/SF3)
- MIDI program change → instrument switching pipeline
- Audio synthesis library selection (MeltySynth / FluidSynth wrapper / NAudio)
- Low-latency audio output (WASAPI preferred)
- Memory-safe soundfont loading/unloading on instrument switch

## Learnings

<!-- append new learnings below -->
