using System.Text.Json.Serialization;

namespace MinimalMusicKeyboard.Models;

public enum InstrumentType
{
    SoundFont = 0,
    Vst3 = 1,
}

/// <summary>
/// Immutable definition of a single instrument preset. JSON-serializable for settings persistence.
/// </summary>
public sealed record InstrumentDefinition
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("displayName")]
    public required string DisplayName { get; init; }

    /// <summary>Discriminator. Defaults to SoundFont when absent in JSON (backward compat).</summary>
    [JsonPropertyName("type")]
    public InstrumentType Type { get; init; } = InstrumentType.SoundFont;

    /// <summary>
    /// Absolute or app-relative path to the SF2 file.
    /// Use "[SoundFont Not Configured]" as a placeholder when no soundfont has been assigned.
    /// </summary>
    [JsonPropertyName("soundFontPath")]
    public string? SoundFontPath { get; init; }

    /// <summary>General MIDI bank number (0 for GM standard bank).</summary>
    [JsonPropertyName("bankNumber")]
    public int BankNumber { get; init; }

    /// <summary>Path to the .vst3 bundle directory.</summary>
    [JsonPropertyName("vst3PluginPath")]
    public string? Vst3PluginPath { get; init; }

    /// <summary>Optional .vstpreset file for initial state.</summary>
    [JsonPropertyName("vst3PresetPath")]
    public string? Vst3PresetPath { get; init; }

    /// <summary>General MIDI program number (0–127).</summary>
    [JsonPropertyName("programNumber")]
    public int ProgramNumber { get; init; }

    [JsonPropertyName("category")]
    public string Category { get; init; } = string.Empty;

    public InstrumentType GetEffectiveType()
    {
        if (!string.IsNullOrWhiteSpace(Vst3PluginPath))
            return InstrumentType.Vst3;

        return InstrumentType.SoundFont;
    }
}
