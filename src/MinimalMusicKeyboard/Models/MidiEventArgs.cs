namespace MinimalMusicKeyboard.Models;

/// <summary>Event args for a MIDI Program Change message.</summary>
public sealed class MidiProgramEventArgs : EventArgs
{
    public int Channel { get; init; }
    public int ProgramNumber { get; init; }
}

/// <summary>Event args for a MIDI Control Change message.</summary>
public sealed class MidiControlEventArgs : EventArgs
{
    public int Channel { get; init; }
    public int ControlNumber { get; init; }
    public int Value { get; init; }
}
