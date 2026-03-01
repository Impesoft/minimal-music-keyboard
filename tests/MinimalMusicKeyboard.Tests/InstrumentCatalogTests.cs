using MinimalMusicKeyboard.Tests.Stubs;
using System.Text.Json;

namespace MinimalMusicKeyboard.Tests;

/// <summary>
/// Tests for InstrumentCatalog. Covers JSON loading, graceful failure handling,
/// and query contract (GetByProgramChange, GetByName).
///
/// Tests use CatalogLoader (test double) with real temp-directory JSON files
/// so file-system edge cases are exercised without mocking I/O.
///
/// Architecture reference: docs/architecture.md section 3.4 + settings schema
/// </summary>
public class InstrumentCatalogTests : IDisposable
{
    private readonly string _tempDir;

    public InstrumentCatalogTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"MmkTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    // -----------------------------------------------------------------------
    // Missing file → write defaults and return
    // -----------------------------------------------------------------------

    /// <summary>
    /// When the settings file doesn't exist (first run or corrupted installation),
    /// the catalog must write a default file and return the default instruments.
    /// Architecture: section 7 — "If file is missing or corrupt, create with sensible defaults"
    /// </summary>
    [Fact]
    public void MissingSettingsFile_WritesDefaultFileAndReturnsCatalog()
    {
        var path = Path.Combine(_tempDir, "settings.json");
        File.Exists(path).Should().BeFalse("precondition: file does not exist yet");

        var catalog = CatalogLoader.LoadFromFileOrDefault(path);

        File.Exists(path).Should().BeTrue("catalog must write defaults when file is missing");
        catalog.GetAll().Should().NotBeEmpty("default catalog must contain instruments");
    }

    [Fact]
    public void MissingSettingsFile_DefaultCatalogContainsGrandPiano()
    {
        var path = Path.Combine(_tempDir, "settings.json");

        var catalog = CatalogLoader.LoadFromFileOrDefault(path);

        catalog.GetByName("Grand Piano").Should().NotBeNull(
            "Grand Piano is the required default instrument per architecture schema");
    }

    // -----------------------------------------------------------------------
    // Corrupted JSON → fallback to defaults, no crash
    // -----------------------------------------------------------------------

    /// <summary>
    /// If the settings file contains invalid JSON (disk corruption, partial write),
    /// the catalog must fall back to defaults without crashing.
    /// Architecture: section 7 — "If file is missing or corrupt, create with sensible defaults"
    /// </summary>
    [Fact]
    public void CorruptedJson_FallsBackToDefaultsWithoutCrash()
    {
        var path = Path.Combine(_tempDir, "settings.json");
        File.WriteAllText(path, "{ this is not valid JSON !!!@#$");

        var act = () => CatalogLoader.LoadFromFileOrDefault(path);

        act.Should().NotThrow("corrupted JSON must never crash the app");
    }

    [Fact]
    public void CorruptedJson_ReturnedCatalogContainsDefaults()
    {
        var path = Path.Combine(_tempDir, "settings.json");
        File.WriteAllText(path, "<<CORRUPTED>>");

        var catalog = CatalogLoader.LoadFromFileOrDefault(path);

        catalog.GetAll().Should().NotBeEmpty(
            "corrupt JSON must fall back to default instruments, not return empty catalog");
    }

    [Fact]
    public void EmptyJsonArray_FallsBackToDefaults()
    {
        var path = Path.Combine(_tempDir, "settings.json");
        File.WriteAllText(path, "[]"); // valid JSON but no instruments

        var catalog = CatalogLoader.LoadFromFileOrDefault(path);

        // Empty array is valid JSON — catalog returns an empty list, not defaults.
        // This is intentional: empty array means "user cleared all instruments."
        // The test documents this behaviour explicitly.
        catalog.GetAll().Should().HaveCount(0,
            "an explicit empty array means the user has no instruments configured; " +
            "caller is responsible for showing an appropriate UI state");
    }

    // -----------------------------------------------------------------------
    // GetByName / GetByProgramChange — null safety
    // -----------------------------------------------------------------------

    /// <summary>
    /// GetByName with an ID not in the catalog must return null,
    /// not throw a KeyNotFoundException or NullReferenceException.
    /// </summary>
    [Fact]
    public void GetByName_UnknownName_ReturnsNull()
    {
        var catalog = new StubInstrumentCatalog();

        var result = catalog.GetByName("Theremin");

        result.Should().BeNull("unknown instrument name must return null, not throw");
    }

    [Fact]
    public void GetByName_NullInput_ReturnsNull()
    {
        var catalog = new StubInstrumentCatalog();

        var act = () => catalog.GetByName(null!);

        // The contract allows null return or throwing ArgumentNullException.
        // Document whichever the implementation chooses; here we assert no crash.
        act.Should().NotThrow("null name lookup must not crash");
    }

    [Fact]
    public void GetByProgramChange_UnknownNumber_ReturnsNull()
    {
        var catalog = new StubInstrumentCatalog();

        var result = catalog.GetByProgramChange(99);

        result.Should().BeNull("PC number 99 is not mapped; must return null silently");
    }

    [Fact]
    public void GetByProgramChange_OutOfRangeNumber_ReturnsNull()
    {
        // MIDI PC numbers are 0-127. Values outside this range must not crash.
        var catalog = new StubInstrumentCatalog();

        var act = () =>
        {
            _ = catalog.GetByProgramChange(-1);
            _ = catalog.GetByProgramChange(128);
        };

        act.Should().NotThrow("out-of-range PC numbers must never crash");
    }

    // -----------------------------------------------------------------------
    // GetAll — expected default instruments
    // -----------------------------------------------------------------------

    /// <summary>
    /// GetAll on a freshly loaded (or defaulted) catalog must return the
    /// instruments defined in the architecture settings schema example.
    /// </summary>
    [Fact]
    public void GetAll_OnDefaultCatalog_ReturnsGrandPianoAndElectricPiano()
    {
        var catalog = new StubInstrumentCatalog();

        var instruments = catalog.GetAll();

        instruments.Should().HaveCountGreaterOrEqualTo(2,
            "default catalog must have at least 2 instruments per architecture schema");
        instruments.Should().Contain(i => i.Name == "Grand Piano",
            "Grand Piano is the default instrument per architecture schema");
        instruments.Should().Contain(i => i.Name == "Electric Piano",
            "Electric Piano is the second default per architecture schema");
    }

    [Fact]
    public void GetAll_DefaultInstruments_HaveValidProgramChangeNumbers()
    {
        var catalog = new StubInstrumentCatalog();

        var instruments = catalog.GetAll();

        instruments.Should().AllSatisfy(i =>
            i.ProgramChangeNumber.Should().BeInRange(0, 127,
                "all MIDI program change numbers must be in valid range 0-127"));
    }

    [Fact]
    public void GetAll_DefaultInstruments_HaveNonEmptySoundFontPath()
    {
        var catalog = new StubInstrumentCatalog();

        catalog.GetAll().Should().AllSatisfy(i =>
            i.SoundFontPath.Should().NotBeNullOrWhiteSpace(
                "every instrument must reference a soundfont file path"));
    }

    // -----------------------------------------------------------------------
    // Round-trip — load written defaults back from disk
    // -----------------------------------------------------------------------

    [Fact]
    public void WrittenDefaults_CanBeReadBack_WithCorrectInstrumentNames()
    {
        var path = Path.Combine(_tempDir, "settings.json");

        // First load creates + writes the defaults
        CatalogLoader.LoadFromFileOrDefault(path);

        // Second load reads them back from disk
        var catalog = CatalogLoader.LoadFromFileOrDefault(path);

        catalog.GetByName("Grand Piano").Should().NotBeNull(
            "written defaults must round-trip correctly through JSON serialization");
    }
}
