using System.IO;
using System.Text.Json;

namespace MinimalMusicKeyboard.Models;

/// <summary>
/// User-facing settings persisted to %LOCALAPPDATA%\MinimalMusicKeyboard\settings.json.
/// </summary>
public sealed class AppSettings
{
    public string? MidiDeviceName { get; set; }
    public string? SoundFontPath  { get; set; }

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
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
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
