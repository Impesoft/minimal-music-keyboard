using MinimalMusicKeyboard.Models;

namespace MinimalMusicKeyboard.Interfaces;

/// <summary>
/// Contract for the audio synthesis engine. Implementation owned by Faye.
/// Jet wires this up from MidiDeviceService events via MidiMessageRouter (Spike's design).
/// </summary>
public interface IAudioEngine : IDisposable
{
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
}
