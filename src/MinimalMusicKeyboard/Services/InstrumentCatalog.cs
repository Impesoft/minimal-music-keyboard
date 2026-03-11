using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MinimalMusicKeyboard.Models;

namespace MinimalMusicKeyboard.Services;

/// <summary>
/// Manages the instrument catalog. Loads from %LOCALAPPDATA%\MinimalMusicKeyboard\instruments.json,
/// writing a default catalog if the file is absent. Thread-safe for concurrent reads.
/// </summary>
public sealed class InstrumentCatalog
{
    private const string PlaceholderSoundFontPath = "[SoundFont Not Configured]";

    private List<InstrumentDefinition> _instruments;
    private readonly Dictionary<string, InstrumentDefinition> _byId;
    private readonly Dictionary<int, InstrumentDefinition> _byProgramNumber;

    public InstrumentCatalog()
    {
        _instruments     = LoadOrCreateDefault();
        _byId            = new Dictionary<string, InstrumentDefinition>(StringComparer.OrdinalIgnoreCase);
        _byProgramNumber = new Dictionary<int, InstrumentDefinition>();

        foreach (var inst in _instruments)
        {
            _byId[inst.Id] = inst;
            _byProgramNumber[inst.ProgramNumber] = inst;
        }
    }

    /// <summary>Returns the full ordered instrument list (thread-safe; list is immutable after construction).</summary>
    public IReadOnlyList<InstrumentDefinition> GetAll() => _instruments;

    /// <summary>
    /// Replaces the SoundFont path on every instrument in the catalog with <paramref name="newPath"/>
    /// and persists the updated catalog to disk. Call from the UI thread after the user picks a new SF2.
    /// </summary>
    public void UpdateAllSoundFontPaths(string newPath)
    {
        var updated = _instruments.Select(i => i with { SoundFontPath = newPath }).ToList();

        SaveCatalog(GetCatalogPath(), updated);

        _instruments = updated;

        _byId.Clear();
        _byProgramNumber.Clear();
        foreach (var inst in updated)
        {
            _byId[inst.Id] = inst;
            _byProgramNumber[inst.ProgramNumber] = inst;
        }
    }

    /// <summary>
    /// Updates the SoundFont path for a single instrument and persists the catalog.
    /// </summary>
    public void UpdateInstrumentSoundFont(string id, string newPath)
    {
        var updated = _instruments
            .Select(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase) ? i with { SoundFontPath = newPath } : i)
            .ToList();

        SaveCatalog(GetCatalogPath(), updated);

        _instruments = updated;
        _byId.Clear();
        _byProgramNumber.Clear();
        foreach (var inst in updated)
        {
            _byId[inst.Id] = inst;
            _byProgramNumber[inst.ProgramNumber] = inst;
        }
    }

    /// <summary>
    /// Adds or updates a VST3 instrument definition in the catalog and persists it.
    /// Used for slot-specific VST3 instruments configured in the UI.
    /// </summary>
    public void AddOrUpdateVst3Instrument(InstrumentDefinition instrument)
    {
        var updated = _instruments.Where(i => !string.Equals(i.Id, instrument.Id, StringComparison.OrdinalIgnoreCase)).ToList();
        updated.Add(instrument);

        SaveCatalog(GetCatalogPath(), updated);

        _instruments = updated;
        _byId.Clear();
        _byProgramNumber.Clear();
        foreach (var inst in updated)
        {
            _byId[inst.Id] = inst;
            _byProgramNumber[inst.ProgramNumber] = inst;
        }
    }

    /// <summary>Looks up by instrument id (case-insensitive). Returns null if not found.</summary>
    public InstrumentDefinition? GetById(string id)
    {
        _byId.TryGetValue(id, out var result);
        return result;
    }

    /// <summary>Looks up by General MIDI program number. Returns null if not mapped.</summary>
    public InstrumentDefinition? GetByProgramNumber(int programNumber)
    {
        _byProgramNumber.TryGetValue(programNumber, out var result);
        return result;
    }

    private static List<InstrumentDefinition> LoadOrCreateDefault()
    {
        var path = GetCatalogPath();

        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                var list = JsonSerializer.Deserialize<List<InstrumentDefinition>>(json);
                if (list is { Count: > 0 })
                    return list;
            }
            catch
            {
                // Corrupt or unreadable — fall through to defaults
            }
        }

        var defaults = BuildDefaultCatalog();
        SaveCatalog(path, defaults);
        return defaults;
    }

    private static List<InstrumentDefinition> BuildDefaultCatalog() =>
    [
        new() { Id = "grand-piano",    DisplayName = "Grand Piano",    SoundFontPath = PlaceholderSoundFontPath, BankNumber = 0, ProgramNumber = 0,  Category = "Piano"   },
        new() { Id = "bright-piano",   DisplayName = "Bright Piano",   SoundFontPath = PlaceholderSoundFontPath, BankNumber = 0, ProgramNumber = 1,  Category = "Piano"   },
        new() { Id = "electric-piano", DisplayName = "Electric Piano", SoundFontPath = PlaceholderSoundFontPath, BankNumber = 0, ProgramNumber = 4,  Category = "Piano"   },
        new() { Id = "strings",        DisplayName = "Strings",        SoundFontPath = PlaceholderSoundFontPath, BankNumber = 0, ProgramNumber = 48, Category = "Strings" },
        new() { Id = "organ",          DisplayName = "Organ",          SoundFontPath = PlaceholderSoundFontPath, BankNumber = 0, ProgramNumber = 16, Category = "Organ"   },
        new() { Id = "pad",            DisplayName = "Pad",            SoundFontPath = PlaceholderSoundFontPath, BankNumber = 0, ProgramNumber = 88, Category = "Pad"     },
        new() { Id = "fingered-bass",  DisplayName = "Fingered Bass",  SoundFontPath = PlaceholderSoundFontPath, BankNumber = 0, ProgramNumber = 33, Category = "Bass"    },
        new() { Id = "choir",          DisplayName = "Choir Aahs",     SoundFontPath = PlaceholderSoundFontPath, BankNumber = 0, ProgramNumber = 52, Category = "Choir"   },
    ];

    private static void SaveCatalog(string path, List<InstrumentDefinition> instruments)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(instruments, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch
        {
            // Non-fatal — app can still run without a persisted catalog file
        }
    }

    private static string GetCatalogPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "MinimalMusicKeyboard", "instruments.json");
    }
}
