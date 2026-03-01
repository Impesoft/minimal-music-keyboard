using System.Collections.Concurrent;
using System.Diagnostics;
using MeltySynth;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using MinimalMusicKeyboard.Interfaces;
using MinimalMusicKeyboard.Models;

namespace MinimalMusicKeyboard.Services;

/// <summary>
/// MeltySynth + NAudio WASAPI audio engine.
///
/// Threading model (per Gren's review):
///   - The audio render thread (WASAPI callback) is the sole owner of the Synthesizer instance.
///   - The MIDI callback thread enqueues commands via a ConcurrentQueue — never touches Synthesizer directly.
///   - Instrument swaps (new SF2) run on a background Task and swap the Synthesizer reference via
///     Volatile.Write. The audio callback snapshots the reference at the top of each Read() call so
///     the old Synthesizer stays alive for the full duration of any in-progress render.
///   - SoundFont objects are cached in a Dictionary keyed by path. Cached SoundFonts are disposed
///     (if IDisposable) when AudioEngine.Dispose() is called.
///   - SF2 files are loaded via a `using` block on FileStream — no file handle is held after load.
/// </summary>
public sealed class AudioEngine : IAudioEngine
{
    // ── Audio constants ────────────────────────────────────────────────────────
    private const int SampleRate = 48_000;
    private const int BufferMs   = 20;    // good latency/stability balance
    private const int Channels   = 2;     // stereo

    // ── Synthesizer (audio thread reads; swap thread writes via Volatile) ──────
    // Field is read by the audio callback via Volatile.Read; written by background swap via Volatile.Write.
    private Synthesizer? _synthesizer;

    // ── Soundfont cache ────────────────────────────────────────────────────────
    private readonly Dictionary<string, SoundFont> _soundFontCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _soundFontCacheLock = new();
    private string? _currentSoundFontPath;     // volatile read/write; see accesses below

    // ── MIDI command queue (MIDI thread → audio thread, lock-free) ────────────
    private readonly ConcurrentQueue<MidiCommand> _commandQueue = new();

    // ── WASAPI output ──────────────────────────────────────────────────────────
    private readonly WasapiOut _wasapiOut;

    // ── Render scratch buffers (allocated once, reused each callback) ─────────
    private float[] _leftBuffer  = Array.Empty<float>();
    private float[] _rightBuffer = Array.Empty<float>();

    private bool _disposed;

    public AudioEngine()
    {
        _wasapiOut = new WasapiOut(AudioClientShareMode.Shared, true, BufferMs);
        _wasapiOut.Init(new SynthesizerSampleProvider(this));
        _wasapiOut.Play();
    }

    // ── IAudioEngine ───────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void NoteOn(int channel, int note, int velocity)
        => _commandQueue.Enqueue(new MidiCommand(MidiCommandType.NoteOn, channel, note, velocity));

    /// <inheritdoc/>
    public void NoteOff(int channel, int note)
        => _commandQueue.Enqueue(new MidiCommand(MidiCommandType.NoteOff, channel, note, 0));

    /// <inheritdoc/>
    public void SelectInstrument(int programNumber)
        => _commandQueue.Enqueue(new MidiCommand(MidiCommandType.ProgramChange, 0, programNumber, 0));

    /// <inheritdoc/>
    public void SelectInstrument(InstrumentDefinition instrument)
    {
        if (instrument.SoundFontPath == "[SoundFont Not Configured]")
        {
            // No SF2 to load — just queue a program change; notes will play if any synth is loaded
            _commandQueue.Enqueue(new MidiCommand(MidiCommandType.ProgramChange, 0, instrument.ProgramNumber, 0));
            return;
        }

        var current = Volatile.Read(ref _currentSoundFontPath);
        if (string.Equals(current, instrument.SoundFontPath, StringComparison.OrdinalIgnoreCase))
        {
            // Same SF2 already loaded — just change the program, no file I/O needed
            _commandQueue.Enqueue(new MidiCommand(MidiCommandType.ProgramChange, 0, instrument.ProgramNumber, 0));
            return;
        }

        // New SF2 required — silence current notes immediately, then load on a background thread
        _commandQueue.Enqueue(new MidiCommand(MidiCommandType.NoteOffAll, 0, 0, 0));
        _ = Task.Run(() => SwapSynthesizerAsync(instrument));
    }

    /// <inheritdoc/>
    public void LoadSoundFont(string path)
    {
        _commandQueue.Enqueue(new MidiCommand(MidiCommandType.NoteOffAll, 0, 0, 0));
        _ = Task.Run(() => SwapSynthesizerAsync(new InstrumentDefinition
        {
            Id            = "__loadSoundFont__",
            DisplayName   = path,
            SoundFontPath = path,
            BankNumber    = 0,
            ProgramNumber = 0,
        }));
    }

    /// <inheritdoc/>
    public void SetPreset(int channel, int bank, int preset)
    {
        _commandQueue.Enqueue(new MidiCommand(MidiCommandType.ControlChange, channel, 0,  bank >> 7));   // CC#0 bank MSB
        _commandQueue.Enqueue(new MidiCommand(MidiCommandType.ControlChange, channel, 32, bank & 0x7F)); // CC#32 bank LSB
        _commandQueue.Enqueue(new MidiCommand(MidiCommandType.ProgramChange, channel, preset, 0));
    }

    // ── Synthesizer swap (background thread) ──────────────────────────────────

    private void SwapSynthesizerAsync(InstrumentDefinition instrument)
    {
        try
        {
            var newSynth = CreateSynthesizer(instrument.SoundFontPath);

            // Apply bank + program change on the freshly created synthesizer before it goes live
            if (instrument.BankNumber != 0)
            {
                newSynth.ProcessMidiMessage(0, 0xB0, 0,  instrument.BankNumber >> 7);  // CC#0 bank MSB
                newSynth.ProcessMidiMessage(0, 0xB0, 32, instrument.BankNumber & 0x7F); // CC#32 bank LSB
            }
            newSynth.ProcessMidiMessage(0, 0xC0, instrument.ProgramNumber, 0); // Program Change

            // Atomic swap — audio callback picks up newSynth on its next Read() call.
            // The old Synthesizer stays alive until GC collects it; no explicit dispose needed
            // (MeltySynth Synthesizer holds only managed arrays).
            Volatile.Write(ref _synthesizer!, newSynth);
            Volatile.Write(ref _currentSoundFontPath!, instrument.SoundFontPath);

            // Hint to GC: SF2 data can be 10–50 MB; reclaim promptly after swap
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AudioEngine] Failed to load soundfont '{instrument.SoundFontPath}': {ex.Message}");
        }
    }

    private Synthesizer CreateSynthesizer(string soundFontPath)
    {
        var soundFont = GetOrLoadSoundFont(soundFontPath);
        var settings  = new SynthesizerSettings(SampleRate);
        return new Synthesizer(soundFont, settings);
    }

    private SoundFont GetOrLoadSoundFont(string path)
    {
        lock (_soundFontCacheLock)
        {
            if (_soundFontCache.TryGetValue(path, out var cached))
                return cached;
        }

        // Load outside the lock — file I/O should not block other readers
        // Per Gren: use `using` on FileStream so the handle is released after load
        SoundFont loaded;
        using (var stream = File.OpenRead(path))
        {
            loaded = new SoundFont(stream);
        }

        lock (_soundFontCacheLock)
        {
            // Another thread may have loaded the same path concurrently; last writer wins (both valid)
            _soundFontCache[path] = loaded;
            return loaded;
        }
    }

    // ── Audio callback (called on the WASAPI render thread) ───────────────────

    /// <summary>
    /// Called by the WASAPI callback (via SynthesizerSampleProvider.Read).
    /// This is the only place Synthesizer.Render() is called.
    /// </summary>
    internal int ReadSamples(float[] buffer, int offset, int count)
    {
        // Snapshot the synthesizer reference (Gren requirement: local variable prevents mid-render swap issues)
        var synth = Volatile.Read(ref _synthesizer!);

        if (synth is null)
        {
            // No soundfont loaded yet — output silence
            Array.Clear(buffer, offset, count);
            return count;
        }

        int frameCount = count / Channels;
        EnsureRenderBuffers(frameCount);

        // Drain MIDI command queue — all Synthesizer mutations happen here on the audio thread
        while (_commandQueue.TryDequeue(out var cmd))
        {
            switch (cmd.Type)
            {
                case MidiCommandType.NoteOn:        synth.NoteOn(cmd.Channel, cmd.Data1, cmd.Data2);                      break;
                case MidiCommandType.NoteOff:       synth.NoteOff(cmd.Channel, cmd.Data1);                                  break;
                case MidiCommandType.ProgramChange: synth.ProcessMidiMessage(cmd.Channel, 0xC0, cmd.Data1, 0);              break;
                case MidiCommandType.ControlChange: synth.ProcessMidiMessage(cmd.Channel, 0xB0, cmd.Data1, cmd.Data2);      break;
                case MidiCommandType.NoteOffAll:    synth.NoteOffAll(immediate: false);                                     break;
            }
        }

        // Render to separate left/right float arrays
        synth.Render(_leftBuffer.AsSpan(0, frameCount), _rightBuffer.AsSpan(0, frameCount));

        // Interleave into the WASAPI output buffer (L, R, L, R, ...)
        for (int i = 0; i < frameCount; i++)
        {
            buffer[offset + i * 2]     = _leftBuffer[i];
            buffer[offset + i * 2 + 1] = _rightBuffer[i];
        }

        return count;
    }

    private void EnsureRenderBuffers(int frameCount)
    {
        if (_leftBuffer.Length < frameCount)
        {
            _leftBuffer  = new float[frameCount];
            _rightBuffer = new float[frameCount];
        }
    }

    // ── Disposal ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Silence all voices before stopping output
        var synth = Volatile.Read(ref _synthesizer!);
        if (synth is not null)
        {
            try { synth.NoteOffAll(immediate: true); } catch { /* best-effort */ }
        }

        _wasapiOut.Stop();
        _wasapiOut.Dispose();

        // Null the synthesizer so the render thread outputs silence if called during shutdown
        Volatile.Write(ref _synthesizer!, null!);

        // MeltySynth SoundFont is not IDisposable in 2.4.0; just clear the cache to release refs.
        lock (_soundFontCacheLock)
        {
            _soundFontCache.Clear();
        }
    }

    // ── Inner types ────────────────────────────────────────────────────────────

    private enum MidiCommandType : byte
    {
        NoteOn,
        NoteOff,
        ProgramChange,
        ControlChange,
        NoteOffAll,
    }

    private readonly struct MidiCommand
    {
        public MidiCommandType Type    { get; }
        public int             Channel { get; }
        public int             Data1   { get; }
        public int             Data2   { get; }

        public MidiCommand(MidiCommandType type, int channel, int data1, int data2)
        {
            Type    = type;
            Channel = channel;
            Data1   = data1;
            Data2   = data2;
        }
    }

    /// <summary>
    /// NAudio ISampleProvider bridge. Produces interleaved IEEE float stereo at 48 kHz.
    /// </summary>
    private sealed class SynthesizerSampleProvider : ISampleProvider
    {
        private readonly AudioEngine _engine;

        public SynthesizerSampleProvider(AudioEngine engine)
        {
            _engine    = engine;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
            => _engine.ReadSamples(buffer, offset, count);
    }
}
