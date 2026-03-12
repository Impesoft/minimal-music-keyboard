using MinimalMusicKeyboard.Models;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace MinimalMusicKeyboard.Interfaces;

/// <summary>
/// Abstraction for an audio synthesis backend (SF2 soundfont, VST3 plugin, etc.).
/// </summary>
public interface IInstrumentBackend : IDisposable
{
    /// <summary>Human-readable name for diagnostics.</summary>
    string DisplayName { get; }

    /// <summary>The instrument type this backend handles.</summary>
    InstrumentType BackendType { get; }

    /// <summary>
    /// Load/initialize the backend for the given instrument definition.
    /// This is called from a background task and may perform blocking work.
    /// </summary>
    Task LoadAsync(InstrumentDefinition instrument, CancellationToken cancellation = default);

    /// <summary>Whether the backend has successfully loaded and can produce audio.</summary>
    bool IsReady { get; }

    /// <summary>
    /// Returns the ISampleProvider that produces audio for this backend.
    /// </summary>
    ISampleProvider GetSampleProvider();

    /// <summary>MIDI note-on. Called from the audio render thread only.</summary>
    /// <remarks>Called from the audio thread only. Do not call from the MIDI callback thread.</remarks>
    void NoteOn(int channel, int note, int velocity);

    /// <summary>MIDI note-off. Called from the audio render thread only.</summary>
    /// <remarks>Called from the audio thread only. Do not call from the MIDI callback thread.</remarks>
    void NoteOff(int channel, int note);

    /// <summary>Silence all voices immediately. Called from the audio render thread only.</summary>
    /// <remarks>Called from the audio thread only. Do not call from the MIDI callback thread.</remarks>
    void NoteOffAll();

    /// <summary>MIDI program/bank change within this backend's domain.</summary>
    void SetProgram(int channel, int bank, int program);
}

/// <summary>
/// Optional interface for backends that support opening a plugin editor GUI.
/// Implement alongside IInstrumentBackend when the backend has a visual editor.
/// </summary>
public interface IEditorCapable
{
    /// <summary>True if this backend's plugin has an editor GUI.</summary>
    bool SupportsEditor { get; }

    /// <summary>Opens the plugin's editor window.</summary>
    Task OpenEditorAsync();

    /// <summary>Closes the plugin's editor window.</summary>
    Task CloseEditorAsync();
}
