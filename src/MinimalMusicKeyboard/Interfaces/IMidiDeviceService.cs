using MinimalMusicKeyboard.Models;

namespace MinimalMusicKeyboard.Interfaces;

/// <summary>
/// Minimal surface of MidiDeviceService that MidiInstrumentSwitcher needs.
/// Jet implements this on MidiDeviceService; Faye consumes it in MidiInstrumentSwitcher.
/// </summary>
public interface IMidiDeviceService
{
    event EventHandler<MidiNoteEventArgs>?    NoteOnReceived;
    event EventHandler<MidiNoteEventArgs>?    NoteOffReceived;
    event EventHandler<MidiProgramEventArgs>? ProgramChangeReceived;
    event EventHandler<MidiControlEventArgs>? ControlChangeReceived;
}
