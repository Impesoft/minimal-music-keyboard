// Minimal test doubles used by disposal and concurrency tests.
// These implement the correct architecture-specified contracts
// so tests exercise real disposal and threading behaviour.

using System.Runtime.CompilerServices;
using MinimalMusicKeyboard.Audio;
using MinimalMusicKeyboard.Instruments;
using MinimalMusicKeyboard.Midi;

namespace MinimalMusicKeyboard.Tests.Stubs;

// ---------------------------------------------------------------------------
// StubMidiDeviceService
// ---------------------------------------------------------------------------

/// <summary>
/// Test double for MidiDeviceService. Implements the correct disconnect and
/// disposal behaviour described in architecture section 3.2.
/// </summary>
internal sealed class StubMidiDeviceService : IMidiDeviceService
{
    private EventHandler<MidiNoteEventArgs>? _noteReceived;
    private EventHandler<MidiControlEventArgs>? _controlChangeReceived;
    private EventHandler<MidiProgramEventArgs>? _programChangeReceived;
    private EventHandler? _deviceDisconnected;
    private bool _disposed;

    public MidiDeviceStatus Status { get; private set; } = MidiDeviceStatus.Connected;

    public event EventHandler<MidiNoteEventArgs>? NoteReceived
    {
        add => _noteReceived += value;
        remove => _noteReceived -= value;
    }

    public event EventHandler<MidiControlEventArgs>? ControlChangeReceived
    {
        add => _controlChangeReceived += value;
        remove => _controlChangeReceived -= value;
    }

    public event EventHandler<MidiProgramEventArgs>? ProgramChangeReceived
    {
        add => _programChangeReceived += value;
        remove => _programChangeReceived -= value;
    }

    public event EventHandler? DeviceDisconnected
    {
        add => _deviceDisconnected += value;
        remove => _deviceDisconnected -= value;
    }

    // Test helpers — fire events from tests
    public void RaiseNoteReceived(MidiNoteEventArgs args) => _noteReceived?.Invoke(this, args);
    public void SimulateDisconnect()
    {
        Status = MidiDeviceStatus.Disconnected;
        _deviceDisconnected?.Invoke(this, EventArgs.Empty);
    }

    // Expose delegate counts for event-leak assertions
    public bool HasNoteReceivedSubscribers => _noteReceived is not null;
    public bool HasDeviceDisconnectedSubscribers => _deviceDisconnected is not null;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Clear all event handler lists on dispose — matching production contract
        _noteReceived = null;
        _controlChangeReceived = null;
        _programChangeReceived = null;
        _deviceDisconnected = null;
    }
}

// ---------------------------------------------------------------------------
// StubAudioEngine
// ---------------------------------------------------------------------------

/// <summary>
/// Test double for AudioEngine. Holds a simulated soundfont byte buffer so
/// disposal/GC tests can verify the buffer is released.
/// </summary>
internal sealed class StubAudioEngine : IAudioEngine
{
    // Simulates the memory held by a loaded SoundFont (architecture section 6)
    private byte[]? _soundFontBuffer = new byte[1024];
    private bool _disposed;

    public int NoteOnCallCount { get; private set; }
    public bool IsDisposed => _disposed;

    public void NoteOn(int channel, int key, int velocity)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        NoteOnCallCount++;
    }

    public void NoteOff(int channel, int key)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void SetPreset(int channel, int bank, int preset)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void LoadSoundFont(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("SoundFont file not found.", path);
        _soundFontBuffer = File.ReadAllBytes(path);
    }

    public void SelectInstrument(string soundFontPath, int bank, int preset)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // In production this would load the soundfont on a background thread;
        // here we just record the call
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _soundFontBuffer = null; // release soundfont memory — architecture requirement
    }
}

// ---------------------------------------------------------------------------
// StubInstrumentCatalog
// ---------------------------------------------------------------------------

/// <summary>
/// Test double for InstrumentCatalog. Returns the two defaults from the
/// architecture schema (Grand Piano, Electric Piano).
/// </summary>
internal sealed class StubInstrumentCatalog : IInstrumentCatalog
{
    private readonly List<InstrumentDefinition> _instruments;

    public static readonly InstrumentDefinition GrandPiano = new()
    {
        Name = "Grand Piano",
        SoundFontPath = @"soundfonts\GeneralUser.sf2",
        Bank = 0,
        Preset = 0,
        ProgramChangeNumber = 0
    };

    public static readonly InstrumentDefinition ElectricPiano = new()
    {
        Name = "Electric Piano",
        SoundFontPath = @"soundfonts\GeneralUser.sf2",
        Bank = 0,
        Preset = 4,
        ProgramChangeNumber = 4
    };

    public StubInstrumentCatalog(IEnumerable<InstrumentDefinition>? instruments = null)
    {
        _instruments = instruments?.ToList() ?? new List<InstrumentDefinition>
        {
            GrandPiano,
            ElectricPiano
        };
    }

    public IReadOnlyList<InstrumentDefinition> GetAll() => _instruments.AsReadOnly();

    public InstrumentDefinition? CurrentInstrument => _instruments.FirstOrDefault();

    public InstrumentDefinition? GetByProgramChange(int programNumber) =>
        _instruments.FirstOrDefault(i => i.ProgramChangeNumber == programNumber);

    public InstrumentDefinition? GetByName(string name) =>
        _instruments.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
}

// ---------------------------------------------------------------------------
// CatalogLoader — minimal real loader for file-based InstrumentCatalog tests
// ---------------------------------------------------------------------------

/// <summary>
/// Minimal JSON-backed catalog loader. Tests file-missing and corrupt-JSON
/// edge cases without requiring the production InstrumentCatalog class.
/// </summary>
internal static class CatalogLoader
{
    private static readonly InstrumentDefinition[] DefaultInstruments =
    {
        StubInstrumentCatalog.GrandPiano,
        StubInstrumentCatalog.ElectricPiano
    };

    public static IInstrumentCatalog LoadFromFileOrDefault(string path)
    {
        if (!File.Exists(path))
        {
            WriteDefaults(path);
            return new StubInstrumentCatalog();
        }

        try
        {
            var json = File.ReadAllText(path);
            var instruments = System.Text.Json.JsonSerializer.Deserialize<List<InstrumentDefinition>>(json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return new StubInstrumentCatalog(instruments ?? DefaultInstruments.ToList());
        }
        catch (System.Text.Json.JsonException)
        {
            return new StubInstrumentCatalog(); // fallback to defaults — architecture requirement
        }
    }

    private static void WriteDefaults(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir is not null) Directory.CreateDirectory(dir);
        var json = System.Text.Json.JsonSerializer.Serialize(DefaultInstruments,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }
}

// ---------------------------------------------------------------------------
// WeakReferenceHelper — disposal verification utility
// ---------------------------------------------------------------------------

internal static class WeakReferenceHelper
{
    /// <summary>
    /// Creates and immediately disposes an IDisposable, returning a WeakReference.
    /// Must be NoInlining so the stack frame is fully popped before GC runs —
    /// otherwise the JIT may keep a root alive.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static WeakReference CreateAndDispose<T>(Func<T> factory) where T : IDisposable
    {
        var instance = factory();
        var weakRef = new WeakReference(instance);
        instance.Dispose();
        return weakRef;
    }

    public static void ForceFullGC()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
    }
}
