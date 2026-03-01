using System.Text.Json.Serialization;

namespace MinimalMusicKeyboard.Models;

/// <summary>
/// Immutable definition of a single instrument preset. JSON-serializable for settings persistence.
/// </summary>
public sealed record InstrumentDefinition
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("displayName")]
    public required string DisplayName { get; init; }

    /// <summary>
    /// Absolute or app-relative path to the SF2 file.
    /// Use "[SoundFont Not Configured]" as a placeholder when no soundfont has been assigned.
    /// </summary>
    [JsonPropertyName("soundFontPath")]
    public required string SoundFontPath { get; init; }

    /// <summary>General MIDI bank number (0 for GM standard bank).</summary>
    [JsonPropertyName("bankNumber")]
    public int BankNumber { get; init; }

    /// <summary>General MIDI program number (0–127).</summary>
    [JsonPropertyName("programNumber")]
    public int ProgramNumber { get; init; }

    [JsonPropertyName("category")]
    public string Category { get; init; } = string.Empty;
}
