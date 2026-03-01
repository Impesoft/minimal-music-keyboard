namespace MinimalMusicKeyboard.Midi;

/// <summary>Immutable DTO describing a discovered MIDI input device.</summary>
public sealed record MidiDeviceInfo(int Index, string Name);
