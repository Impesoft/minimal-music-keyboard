using System.IO;
using System.Text.Json;

namespace MinimalMusicKeyboard.Models;

/// <summary>
/// User-facing settings persisted to %LOCALAPPDATA%\MinimalMusicKeyboard\settings.json.
/// </summary>
public sealed class AppSettings
{
    public string? MidiDeviceName      { get; set; }
    public string? AudioOutputDeviceId { get; set; }
    public float   Volume              { get; set; } = 1.0f;

    /// <summary>8 MIDI-button-to-instrument mappings (slots 0–7).</summary>
    public InstrumentButtonMapping[] ButtonMappings { get; set; } = CreateDefaultMappings();

    public static InstrumentButtonMapping[] CreateDefaultMappings()
    {
        var m = new InstrumentButtonMapping[8];
        for (int i = 0; i < 8; i++)
            m[i] = new InstrumentButtonMapping { SlotIndex = i };
        return m;
    }

    // -------------------------------------------------------------------------
    // Persistence
    // -------------------------------------------------------------------------

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MinimalMusicKeyboard", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var s = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                // Ensure exactly 8 slots exist (forward-compat if old settings file has fewer)
                if (s.ButtonMappings == null || s.ButtonMappings.Length < 8)
                {
                    var full = CreateDefaultMappings();
                    if (s.ButtonMappings != null)
                        Array.Copy(s.ButtonMappings, full, Math.Min(s.ButtonMappings.Length, 8));
                    s.ButtonMappings = full;
                }
                return s;
            }
        }
        catch { /* fall through to defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* non-fatal — app runs fine without persisting */ }
    }
}
