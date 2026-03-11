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

### 2026-07-17 — VST3 Architecture Proposal
- **IInstrumentBackend abstraction:** New interface in `Interfaces/IInstrumentBackend.cs` decouples synthesis from AudioEngine. Backends are ISampleProvider audio producers. SF2 and VST3 implement the same contract.
- **AudioEngine becomes mixer host:** Owns WasapiOut + MixingSampleProvider. Routes MIDI to active backend. Backends produce audio; engine mixes and outputs. No more SF2-specific code in AudioEngine itself.
- **Out-of-process VST3 bridge:** Recommended `mmk-vst3-bridge.exe` (native C++) communicating via named pipe (commands) + memory-mapped file (audio). Crash isolation is critical for a tray-resident app running hours/days. IPC latency <1ms per block — imperceptible vs 20ms WASAPI buffer.
- **InstrumentDefinition extended:** Added `InstrumentType` enum discriminator (SoundFont=0, Vst3=1), `Vst3PluginPath`, `Vst3PresetPath`. JSON backward-compatible via default enum value.
- **LoadSoundFont removed from IAudioEngine:** SF2-specific method replaced by type-dispatching `SelectInstrument(InstrumentDefinition)`.
- **SoundFontBackend extraction:** MeltySynth Synthesizer, SoundFont cache, command queue drain, and render logic move from AudioEngine to `Services/Backends/SoundFontBackend.cs`. This is the highest-risk Phase 1 change — audio hot path refactoring.
- **Catalog stays flat:** No grouping by type. MIDI PC routing is type-agnostic. UI groups via LINQ.
- **Phased delivery:** Phase 1 (abstraction + SF2 backend extraction), Phase 2 (managed IPC client), Phase 3 (native bridge), Phase 4 (settings UI).
- **Rejected alternatives:** In-process COM P/Invoke (~2000 lines unsafe, no crash isolation), VST.NET (no mature library), CLAP-only (users have VST3 plugins).
- **Open for Gren:** Bridge lifecycle management, audio thread spin-wait vs semaphore, bridge language (C++ vs Rust), plugin GUI support, CLAP as future addition.

### 2026-07-18 — VST3 Proposal v1.1 Revisions (Gren Review Response)
- Addressed all 6 issues from Gren's review (2 BLOCKING + 4 REQUIRED).
- **BLOCKING #1 fixed:** Rewrote §4.2 AudioEngine sketch — NoteOn() now enqueues to ConcurrentQueue, backends drain on audio thread. Updated §2.1 docstrings: NoteOn/NoteOff are audio-thread-only. Removed _backendLock. Added explicit threading invariant rule.
- **BLOCKING #2 fixed:** Added §4.5 with Dispose() sketches for AudioEngine (6 steps) and Vst3BridgeBackend (7 steps) with exact ordering rationale.
- **REQUIRED #3 fixed:** Clarified mixer input permanence — backends always in mixer, never removed, inactive backends output silence.
- **REQUIRED #4 fixed:** Added BridgeState machine (Running/Faulted/Disposed) with behavior table and crash→faulted flow to §7.1. Added BridgeFaulted event.
- **REQUIRED #5 fixed:** Added full SoundFontBackend code sketch to §6.1 preserving Volatile swap, SF2 cache with lock, command queue drain in Read(), FileStream using block, and Dispose sequence.
- **REQUIRED #6 fixed:** Added IPC resource ownership to §3.2 — host creates MemoryMappedFile + NamedPipeServerStream, bridge connects as client. Host PID in naming scheme for stability.
