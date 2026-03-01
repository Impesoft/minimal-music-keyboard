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

### 2026-03-01 — Architecture v1 Decisions
- **MIDI I/O:** Chose NAudio.Midi over Windows.Devices.Midi2 (too immature) and RtMidi.NET (native dep complexity). NAudio is battle-tested, pure managed, great Arturia compatibility.
- **Audio Synthesis:** Chose MeltySynth (pure C# SF2 synthesizer) + NAudio WasapiOut. Zero native dependencies. Avoids FluidSynth P/Invoke complexity. Pending Faye's validation.
- **System Tray:** H.NotifyIcon.WinUI — only real option for WinUI3 tray apps.
- **Single Instance:** Named Mutex (`Global\MinimalMusicKeyboard`). Works packaged and unpackaged. Simpler than named pipes since we don't need IPC between instances.
- **Instrument Switching:** MIDI Program Change messages. Standard MIDI approach, Arturia pads can send PC natively.
- **Settings Window:** On-demand creation/destruction pattern — never held in memory when closed. Keeps idle footprint under 50MB.
- **Disposal Order:** Router → MIDI → Audio → Tray → Mutex. MIDI stops before audio to prevent events hitting a disposed engine.
- **Threading:** Three threads in play — UI (STA), MIDI callback (NAudio), audio render (WASAPI). MeltySynth is thread-safe for note events across MIDI/audio threads.
- **Memory Budget:** Estimated 29-44MB idle, well within 50MB target.
- **Open for Gren:** DI container weight, MIDI reconnect strategy, multi-SF2 support, packaged vs unpackaged deployment.
