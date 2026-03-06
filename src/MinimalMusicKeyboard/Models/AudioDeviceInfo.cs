namespace MinimalMusicKeyboard.Models;

/// <summary>Represents an available WASAPI audio output device.</summary>
public sealed record AudioDeviceInfo(string Id, string Name);
