using System.Text.Json.Serialization;

namespace MinimalMusicKeyboard.Models;

public enum MidiTriggerType { Note, ControlChange }

/// <summary>
/// Maps a physical MIDI button/pad to an instrument slot.
/// Stored as part of AppSettings (8 slots, indices 0–7).
/// </summary>
public sealed class InstrumentButtonMapping
{
    [JsonPropertyName("slotIndex")]
    public int SlotIndex { get; set; }

    /// <summary>Catalog instrument Id, or null if this slot is unassigned.</summary>
    [JsonPropertyName("instrumentId")]
    public string? InstrumentId { get; set; }

    /// <summary>MIDI channel to listen on. -1 = match any channel.</summary>
    [JsonPropertyName("triggerChannel")]
    public int TriggerChannel { get; set; } = -1;

    [JsonPropertyName("triggerType")]
    public MidiTriggerType TriggerType { get; set; } = MidiTriggerType.Note;

    /// <summary>MIDI note or CC number. -1 = unmapped.</summary>
    [JsonPropertyName("triggerNumber")]
    public int TriggerNumber { get; set; } = -1;

    [JsonIgnore]
    public bool IsUnmapped => TriggerNumber < 0 || string.IsNullOrEmpty(InstrumentId);

    /// <summary>Returns true when the given MIDI event should trigger this slot.</summary>
    public bool Matches(int channel, MidiTriggerType type, int number)
    {
        if (IsUnmapped) return false;
        if (TriggerType != type) return false;
        if (TriggerNumber != number) return false;
        if (TriggerChannel >= 0 && TriggerChannel != channel) return false;
        return true;
    }

    /// <summary>Human-readable trigger description shown in the Settings UI.</summary>
    [JsonIgnore]
    public string TriggerLabel => TriggerNumber < 0
        ? "—"
        : TriggerType == MidiTriggerType.Note
            ? $"Note: {MidiNoteToName(TriggerNumber)}"
            : $"CC: {TriggerNumber}";

    private static string MidiNoteToName(int note)
    {
        string[] names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
        int octave = (note / 12) - 1;
        return $"{names[note % 12]}{octave}";
    }
}
