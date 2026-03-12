using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using MinimalMusicKeyboard.Interfaces;
using MinimalMusicKeyboard.Models;

namespace MinimalMusicKeyboard.Services;

/// <summary>
/// MeltySynth + NAudio WASAPI audio engine.
///
/// Threading model (per Gren's review):
///   - The audio render thread (WASAPI callback) is the sole owner of backend NoteOn/NoteOff calls.
///   - The MIDI callback thread enqueues commands via a ConcurrentQueue — never touches backends directly.
///   - Instrument swaps (new SF2) run on a background Task and swap the Synthesizer reference via
///     Volatile.Write in SoundFontBackend. The audio callback snapshots the active backend before draining.
///   - SoundFont objects are cached in SoundFontBackend. SF2 files are loaded via a `using` block
///     on FileStream — no file handle is held after load.
/// </summary>
public sealed class AudioEngine : IAudioEngine
{
    // ── Audio constants ────────────────────────────────────────────────────────
    private const int SampleRate = 48_000;
    private const int BufferMs   = 20;    // good latency/stability balance
    private const int Channels   = 2;     // stereo
    private const string PlaceholderSoundFontPath = "[SoundFont Not Configured]";

    private readonly MixingSampleProvider _mixer;
    private readonly SoundFontBackend _sfBackend;
    private readonly Vst3BridgeBackend _vst3Backend;
    private IInstrumentBackend? _activeBackend;

    // ── MIDI command queue (MIDI thread → audio thread, lock-free) ────────────
    private readonly ConcurrentQueue<MidiCommand> _commandQueue = new();

    // ── WASAPI output (swappable on device change) ─────────────────────────────
    private WasapiOut _wasapiOut;
    private readonly object _wasapiLock = new(); // serialises device swaps

    private readonly int[] _pendingBankMsb = new int[16];
    private readonly int[] _pendingBankLsb = new int[16];

    private bool _disposed;

    public AudioEngine(string? outputDeviceId = null)
    {
        _sfBackend = new SoundFontBackend(PlaceholderSoundFontPath);
        _vst3Backend = new Vst3BridgeBackend();
        _mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels))
        {
            ReadFully = true
        };
        _mixer.AddMixerInput(_sfBackend.GetSampleProvider());
        _mixer.AddMixerInput(_vst3Backend.GetSampleProvider());

        Volatile.Write(ref _activeBackend, _sfBackend);

        _wasapiOut = CreateWasapiOut(outputDeviceId);
        _wasapiOut.Init(new EngineSampleProvider(this));
        _wasapiOut.Play();
    }

    // ── Volume ────────────────────────────────────────────────────────────────
    private float _volume = 1.0f;

    /// <inheritdoc/>
    public void SetVolume(float volume)
        => Volatile.Write(ref _volume, Math.Clamp(volume, 0f, 2f));

    // ── Output device ─────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public IReadOnlyList<AudioDeviceInfo> EnumerateOutputDevices()
    {
        using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        return enumerator
            .EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active)
            .Select(d => new AudioDeviceInfo(d.ID, d.FriendlyName))
            .ToList();
    }

    /// <inheritdoc/>
    public void ChangeOutputDevice(string? deviceId)
    {
        lock (_wasapiLock)
        {
            if (_disposed) return;

            // Silence and tear down existing output
            try { _wasapiOut.Stop(); } catch { /* best-effort */ }
            try { _wasapiOut.Dispose(); } catch { /* best-effort */ }

            // Build and start the new output; synthesizer reference is preserved
            _wasapiOut = CreateWasapiOut(deviceId);
            _wasapiOut.Init(new EngineSampleProvider(this));
            _wasapiOut.Play();
        }
    }

    private static WasapiOut CreateWasapiOut(string? deviceId)
    {
        if (!string.IsNullOrEmpty(deviceId))
        {
            try
            {
                using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                var device = enumerator.GetDevice(deviceId);
                if (device is not null)
                    return new WasapiOut(device, AudioClientShareMode.Shared, true, BufferMs);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AudioEngine] Could not open device '{deviceId}': {ex.Message} — falling back to default.");
            }
        }
        return new WasapiOut(AudioClientShareMode.Shared, true, BufferMs);
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
        if (instrument.Type == InstrumentType.SoundFont)
        {
            HandleSoundFontInstrument(instrument);
        }
        else if (instrument.Type == InstrumentType.Vst3)
        {
            HandleVst3Instrument(instrument);
        }
        else
        {
            Debug.WriteLine($"[AudioEngine] Unsupported instrument type '{instrument.Type}'.");
        }
    }

    private void HandleSoundFontInstrument(InstrumentDefinition instrument)
    {
        if (string.IsNullOrWhiteSpace(instrument.SoundFontPath) || instrument.SoundFontPath == PlaceholderSoundFontPath)
        {
            // No SF2 to load — just queue a program change; notes will play if any synth is loaded
            _commandQueue.Enqueue(new MidiCommand(MidiCommandType.ProgramChange, 0, instrument.ProgramNumber, 0));
            return;
        }

        var soundFontPath = instrument.SoundFontPath!;
        var current = _sfBackend.CurrentSoundFontPath;
        if (string.Equals(current, soundFontPath, StringComparison.OrdinalIgnoreCase))
        {
            // Same SF2 already loaded — just change the program, no file I/O needed
            _commandQueue.Enqueue(new MidiCommand(MidiCommandType.ProgramChange, 0, instrument.ProgramNumber, 0));
            return;
        }

        // New SF2 required — silence current notes immediately, then load on a background thread
        _commandQueue.Enqueue(new MidiCommand(MidiCommandType.NoteOffAll, 0, 0, 0));
        Volatile.Write(ref _activeBackend, _sfBackend);
        _ = _sfBackend.LoadAsync(instrument);
    }

    private void HandleVst3Instrument(InstrumentDefinition instrument)
    {
        if (string.IsNullOrWhiteSpace(instrument.Vst3PluginPath))
        {
            Debug.WriteLine($"[AudioEngine] VST3 instrument '{instrument.DisplayName}' has no plugin path configured.");
            return;
        }

        // Silence current notes and switch to VST3 backend
        _commandQueue.Enqueue(new MidiCommand(MidiCommandType.NoteOffAll, 0, 0, 0));
        Volatile.Write(ref _activeBackend, _vst3Backend);
        _ = _vst3Backend.LoadAsync(instrument);
    }

    /// <inheritdoc/>
    public void LoadSoundFont(string path)
    {
        _commandQueue.Enqueue(new MidiCommand(MidiCommandType.NoteOffAll, 0, 0, 0));
        Volatile.Write(ref _activeBackend, _sfBackend);
        _ = _sfBackend.LoadAsync(new InstrumentDefinition
        {
            Id            = "__loadSoundFont__",
            DisplayName   = path,
            SoundFontPath = path,
            BankNumber    = 0,
            ProgramNumber = 0,
            Type          = InstrumentType.SoundFont,
        });
    }

    /// <inheritdoc/>
    public void SetPreset(int channel, int bank, int preset)
    {
        _commandQueue.Enqueue(new MidiCommand(MidiCommandType.ControlChange, channel, 0,  bank >> 7));   // CC#0 bank MSB
        _commandQueue.Enqueue(new MidiCommand(MidiCommandType.ControlChange, channel, 32, bank & 0x7F)); // CC#32 bank LSB
        _commandQueue.Enqueue(new MidiCommand(MidiCommandType.ProgramChange, channel, preset, 0));
    }

    // ── Audio callback (called on the WASAPI render thread) ───────────────────

    /// <summary>
    /// Called by the WASAPI callback (via EngineSampleProvider.Read).
    /// This is the only place the command queue is drained.
    /// </summary>
    internal int ReadSamples(float[] buffer, int offset, int count)
    {
        var backend = Volatile.Read(ref _activeBackend);
        if (backend is null || !backend.IsReady)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }

        // Drain MIDI command queue — all backend mutations happen here on the audio thread
        while (_commandQueue.TryDequeue(out var cmd))
        {
            switch (cmd.Type)
            {
                case MidiCommandType.NoteOn:
                    backend.NoteOn(cmd.Channel, cmd.Data1, cmd.Data2);
                    break;
                case MidiCommandType.NoteOff:
                    backend.NoteOff(cmd.Channel, cmd.Data1);
                    break;
                case MidiCommandType.ProgramChange:
                    int bank = GetPendingBank(cmd.Channel);
                    backend.SetProgram(cmd.Channel, bank, cmd.Data1);
                    break;
                case MidiCommandType.ControlChange:
                    UpdatePendingBank(cmd.Channel, cmd.Data1, cmd.Data2);
                    break;
                case MidiCommandType.NoteOffAll:
                    backend.NoteOffAll();
                    break;
            }
        }

        int read = _mixer.Read(buffer, offset, count);

        float vol = Volatile.Read(ref _volume);
        if (Math.Abs(vol - 1f) > float.Epsilon)
        {
            for (int i = 0; i < read; i++)
            {
                buffer[offset + i] *= vol;
            }
        }

        return read;
    }

    private int GetPendingBank(int channel)
    {
        if ((uint)channel >= _pendingBankMsb.Length)
            return 0;

        return (_pendingBankMsb[channel] << 7) | _pendingBankLsb[channel];
    }

    private void UpdatePendingBank(int channel, int controller, int value)
    {
        if ((uint)channel >= _pendingBankMsb.Length)
            return;

        switch (controller)
        {
            case 0:
                _pendingBankMsb[channel] = value;
                break;
            case 32:
                _pendingBankLsb[channel] = value;
                break;
        }
    }

    /// <inheritdoc/>
    public IInstrumentBackend? GetActiveBackend()
        => Volatile.Read(ref _activeBackend);

    // ── Disposal ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var backend = Volatile.Read(ref _activeBackend);
        try { backend?.NoteOffAll(); } catch { }

        lock (_wasapiLock)
        {
            _wasapiOut.Stop();
            _wasapiOut.Dispose();
        }

        _sfBackend.Dispose();
        _vst3Backend.Dispose();
        (_mixer as IDisposable)?.Dispose();
    }

    /// <summary>
    /// NAudio ISampleProvider bridge. Produces interleaved IEEE float stereo at 48 kHz.
    /// </summary>
    private sealed class EngineSampleProvider : ISampleProvider
    {
        private readonly AudioEngine _engine;

        public EngineSampleProvider(AudioEngine engine)
        {
            _engine = engine;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
            => _engine.ReadSamples(buffer, offset, count);
    }
}

internal enum MidiCommandType : byte
{
    NoteOn,
    NoteOff,
    ProgramChange,
    ControlChange,
    NoteOffAll,
}

internal readonly struct MidiCommand
{
    public MidiCommandType Type { get; }
    public int Channel { get; }
    public int Data1 { get; }
    public int Data2 { get; }

    public MidiCommand(MidiCommandType type, int channel, int data1, int data2)
    {
        Type = type;
        Channel = channel;
        Data1 = data1;
        Data2 = data2;
    }
}
