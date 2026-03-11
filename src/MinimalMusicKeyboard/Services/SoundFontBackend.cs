using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MeltySynth;
using MinimalMusicKeyboard.Interfaces;
using MinimalMusicKeyboard.Models;
using NAudio.Wave;

namespace MinimalMusicKeyboard.Services;

/// <summary>
/// SoundFont-backed instrument implementation using MeltySynth.
/// </summary>
public sealed class SoundFontBackend : IInstrumentBackend, ISampleProvider
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;

    private readonly Dictionary<string, SoundFont> _soundFontCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _soundFontCacheLock = new();
    private string? _currentSoundFontPath;

    private Synthesizer? _synthesizer;
    private float[] _leftBuffer = Array.Empty<float>();
    private float[] _rightBuffer = Array.Empty<float>();

    public SoundFontBackend(string soundFontPath)
    {
        _currentSoundFontPath = soundFontPath;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);
    }

    public string DisplayName => "MeltySynth SF2";

    public InstrumentType BackendType => InstrumentType.SoundFont;

    public bool IsReady => Volatile.Read(ref _synthesizer) is not null;

    internal string? CurrentSoundFontPath => Volatile.Read(ref _currentSoundFontPath);

    public WaveFormat WaveFormat { get; }

    public ISampleProvider GetSampleProvider() => this;

    /// <summary>
    /// ISampleProvider.Read invoked by the audio render thread. No allocations or locks.
    /// </summary>
    public int Read(float[] buffer, int offset, int count)
    {
        var synth = Volatile.Read(ref _synthesizer);
        if (synth is null)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }

        int frameCount = count / Channels;
        EnsureRenderBuffers(frameCount);

        synth.Render(_leftBuffer.AsSpan(0, frameCount), _rightBuffer.AsSpan(0, frameCount));

        for (int i = 0; i < frameCount; i++)
        {
            buffer[offset + i * 2] = _leftBuffer[i];
            buffer[offset + i * 2 + 1] = _rightBuffer[i];
        }

        return count;
    }

    public Task LoadAsync(InstrumentDefinition instrument, CancellationToken cancellation = default)
    {
        return Task.Run(() =>
        {
            try
            {
                cancellation.ThrowIfCancellationRequested();

                var soundFontPath = instrument.SoundFontPath;
                if (string.IsNullOrWhiteSpace(soundFontPath))
                    return;

                var newSynth = CreateSynthesizer(soundFontPath);

                if (instrument.BankNumber != 0)
                {
                    newSynth.ProcessMidiMessage(0, 0xB0, 0, instrument.BankNumber >> 7);  // CC#0 bank MSB
                    newSynth.ProcessMidiMessage(0, 0xB0, 32, instrument.BankNumber & 0x7F); // CC#32 bank LSB
                }
                newSynth.ProcessMidiMessage(0, 0xC0, instrument.ProgramNumber, 0); // Program Change

                Volatile.Write(ref _synthesizer!, newSynth);
                Volatile.Write(ref _currentSoundFontPath!, soundFontPath);

                GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SoundFontBackend] Failed to load soundfont '{instrument.SoundFontPath}': {ex.Message}");
            }
        }, cancellation);
    }

    /// <summary>MIDI note-on. Called from the audio render thread only.</summary>
    public void NoteOn(int channel, int note, int velocity)
        => Volatile.Read(ref _synthesizer)?.NoteOn(channel, note, velocity);

    /// <summary>MIDI note-off. Called from the audio render thread only.</summary>
    public void NoteOff(int channel, int note)
        => Volatile.Read(ref _synthesizer)?.NoteOff(channel, note);

    /// <summary>Silence all voices immediately. Called from the audio render thread only.</summary>
    public void NoteOffAll()
        => Volatile.Read(ref _synthesizer)?.NoteOffAll(immediate: false);

    /// <summary>MIDI program/bank change within this backend's domain.</summary>
    public void SetProgram(int channel, int bank, int program)
    {
        var synth = Volatile.Read(ref _synthesizer);
        if (synth is null) return;

        if (bank != 0)
        {
            synth.ProcessMidiMessage(channel, 0xB0, 0, bank >> 7);  // CC#0 bank MSB
            synth.ProcessMidiMessage(channel, 0xB0, 32, bank & 0x7F); // CC#32 bank LSB
        }
        synth.ProcessMidiMessage(channel, 0xC0, program, 0); // Program Change
    }

    public void Dispose()
    {
        Volatile.Read(ref _synthesizer)?.NoteOffAll(immediate: true);
        Volatile.Write(ref _synthesizer, null);
        Volatile.Write(ref _currentSoundFontPath, null);

        lock (_soundFontCacheLock)
        {
            _soundFontCache.Clear();
        }
    }

    private Synthesizer CreateSynthesizer(string soundFontPath)
    {
        var soundFont = GetOrLoadSoundFont(soundFontPath);
        var settings = new SynthesizerSettings(SampleRate);
        return new Synthesizer(soundFont, settings);
    }

    private SoundFont GetOrLoadSoundFont(string path)
    {
        lock (_soundFontCacheLock)
        {
            if (_soundFontCache.TryGetValue(path, out var cached))
                return cached;
        }

        SoundFont loaded;
        using (var stream = File.OpenRead(path))
        {
            loaded = new SoundFont(stream);
        }

        lock (_soundFontCacheLock)
        {
            _soundFontCache[path] = loaded;
            return loaded;
        }
    }

    private void EnsureRenderBuffers(int frameCount)
    {
        if (_leftBuffer.Length < frameCount)
        {
            _leftBuffer = new float[frameCount];
            _rightBuffer = new float[frameCount];
        }
    }
}
