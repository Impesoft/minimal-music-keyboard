using MinimalMusicKeyboard.Models;
using System;

namespace MinimalMusicKeyboard.Interfaces;

/// <summary>
/// Contract for the audio synthesis engine. Implementation owned by Faye.
/// Jet wires this up from MidiDeviceService events via MidiMessageRouter (Spike's design).
/// </summary>
public interface IAudioEngine : IDisposable
{
/// <summary>
    /// Raised when an instrument fails to load (e.g. VST3 bridge missing or plugin rejected).
    /// The string argument is a human-readable error message.
    /// May be raised on a background thread — subscribers must marshal to the UI thread if needed.
    /// </summary>
    event EventHandler<string>? InstrumentLoadFailed;

    /// <summary>
    /// Raised when a VST3 instrument loads successfully and the backend is ready for playback.
    /// The string argument is the plugin path (or display name if path is unavailable).
    /// May be raised on a background thread — subscribers must marshal to the UI thread if needed.
    /// </summary>
    event EventHandler<string>? InstrumentLoadSucceeded;

    /// <summary>Trigger a note-on event (key pressed). Thread-safe — callable from MIDI callback.</summary>
    void NoteOn(int channel, int note, int velocity);

    /// <summary>Trigger a note-off event (key released). Thread-safe — callable from MIDI callback.</summary>
    void NoteOff(int channel, int note);

    /// <summary>
    /// Switch to an instrument by MIDI program number within the current soundfont (0-127).
    /// Lightweight — does not load a new SF2 file.
    /// </summary>
    void SelectInstrument(int programNumber);

    /// <summary>
    /// Switch to the specified instrument definition, loading its soundfont if needed.
    /// Soundfont loading is dispatched to a background thread; returns immediately.
    /// </summary>
    void SelectInstrument(InstrumentDefinition instrument);

    /// <summary>
    /// Load an SF2 soundfont from disk and swap it as the active soundfont.
    /// Dispatched to a background thread; returns immediately.
    /// </summary>
    void LoadSoundFont(string path);

    /// <summary>
    /// Send bank + program change to the synthesizer. Routes through the audio command queue.
    /// </summary>
    void SetPreset(int channel, int bank, int preset);

    /// <summary>
    /// Set master volume (0.0 = silent, 1.0 = full). Thread-safe — callable from any thread.
    /// </summary>
    void SetVolume(float volume);

    /// <summary>Returns all active WASAPI render endpoints available on this machine.</summary>
    IReadOnlyList<AudioDeviceInfo> EnumerateOutputDevices();

    /// <summary>
    /// Switch to the WASAPI device with the given ID (null = system default).
    /// The in-flight synthesizer is preserved; only the output endpoint changes.
    /// </summary>
    void ChangeOutputDevice(string? deviceId);

    /// <summary>
    /// Get the currently active instrument backend (SoundFontBackend or Vst3BridgeBackend).
    /// Returns null if no backend is active.
    /// </summary>
    IInstrumentBackend? GetActiveBackend();

    /// <summary>
    /// Returns the latest VST3 editor availability or failure diagnostic reported by the bridge backend.
    /// </summary>
    string GetVst3EditorAvailabilityDescription();
}
