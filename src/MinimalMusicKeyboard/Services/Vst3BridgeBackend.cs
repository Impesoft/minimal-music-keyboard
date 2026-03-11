using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MinimalMusicKeyboard.Interfaces;
using MinimalMusicKeyboard.Models;
using NAudio.Wave;

namespace MinimalMusicKeyboard.Services;

/// <summary>
/// VST3 instrument backend that delegates audio synthesis to <c>mmk-vst3-bridge.exe</c>.
///
/// <para><b>IPC transport:</b><br/>
/// Commands (host → bridge) are sent as JSON lines over a <see cref="NamedPipeClientStream"/>.
/// Audio is transferred via a <see cref="MemoryMappedFile"/> ring buffer (read-only from this side).</para>
///
/// <para><b>Bridge state machine:</b>
/// <c>NotStarted → Starting → Running → Faulted → Disposed</c>.
/// If the bridge exe is missing, <see cref="IsReady"/> stays <see langword="false"/> and no exception
/// is thrown. Any IPC failure after startup transitions to <c>Faulted</c> and raises
/// <see cref="BridgeFaulted"/>.</para>
///
/// <para><b>Threading contract (inherited from <see cref="IInstrumentBackend"/>):</b><br/>
/// <see cref="NoteOn"/>, <see cref="NoteOff"/>, <see cref="NoteOffAll"/>, and
/// <see cref="SetProgram"/> are called from the WASAPI audio render thread. They enqueue
/// JSON command strings into an unbounded <see cref="Channel{T}"/> — no allocations, no blocking.
/// A dedicated background <see cref="Task"/> drains the channel and writes to the named pipe.
/// <see cref="Read"/> is also called on the audio render thread and copies from shared memory into
/// the output buffer without allocating.</para>
/// </summary>
public sealed class Vst3BridgeBackend : IInstrumentBackend, ISampleProvider
{
    // ── Bridge state machine ──────────────────────────────────────────────────

    private enum BridgeState { NotStarted, Starting, Running, Faulted, Disposed }

    private BridgeState _state = BridgeState.NotStarted;
    private readonly object _stateLock = new();
    private volatile bool _isReady;
    private volatile bool _disposed;

    // ── IPC resources ─────────────────────────────────────────────────────────

    private Process? _bridgeProcess;
    private NamedPipeClientStream? _pipeClient;
    private StreamWriter? _pipeWriter;
    private StreamReader? _pipeReader;
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _mmfView;

    // ── Command channel (audio thread → pipe writer task, lock-free) ──────────

    private readonly Channel<string> _commandChannel = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });

    private Task? _pipeWriterTask;
    private CancellationTokenSource? _writerCts;

    // ── Shared-memory audio ring buffer ───────────────────────────────────────

    // Header layout (little-endian, all int32):
    //   Offset  0: magic     = 0x4D4D4B56 ('M','M','K','V')
    //   Offset  4: version   = 1
    //   Offset  8: frameSize (sample count per render block, mono)
    //   Offset 12: writePos  (atomic int32; bridge advances this after each block)
    // Data starts at offset 16: float32 stereo-interleaved, frameSize * 2 floats.
    private const int MmfHeaderSize = 16;
    private const int MmfMagic = 0x4D4D4B56;

    private float[] _audioWorkBuffer = Array.Empty<float>(); // pre-allocated in LoadAsync
    private int _frameSize;

    // ── Audio constants ───────────────────────────────────────────────────────

    private const int SampleRate = 48_000;
    private const int AudioChannels = 2;

    // ── IInstrumentBackend ────────────────────────────────────────────────────

    /// <inheritdoc/>
    public string DisplayName => "VST3 Bridge";

    /// <inheritdoc/>
    public InstrumentType BackendType => InstrumentType.Vst3;

    /// <inheritdoc/>
    /// <remarks>Backed by a <see langword="volatile"/> field; safe to read from any thread.</remarks>
    public bool IsReady => _isReady;

    /// <inheritdoc/>
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, AudioChannels);

    /// <summary>
    /// Raised on the thread that detected the fault (typically the pipe writer task) when
    /// the bridge process crashes or the IPC connection is severed.
    /// </summary>
    public event EventHandler<BridgeFaultedEventArgs>? BridgeFaulted;

    /// <inheritdoc/>
    public ISampleProvider GetSampleProvider() => this;

    // ── ISampleProvider.Read — AUDIO RENDER THREAD — NO ALLOCATIONS ──────────

    /// <summary>
    /// Copies audio from the bridge's shared-memory ring buffer into <paramref name="buffer"/>.
    /// Fills with silence if the bridge is not ready or has faulted. Never allocates.
    /// </summary>
    /// <param name="buffer">Output sample buffer (IEEE float, stereo interleaved).</param>
    /// <param name="offset">Starting index within <paramref name="buffer"/>.</param>
    /// <param name="count">Number of float samples requested.</param>
    /// <returns><paramref name="count"/> (always fulfils the full request).</returns>
    public int Read(float[] buffer, int offset, int count)
    {
        if (!_isReady)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }

        var view = _mmfView; // snapshot — if Dispose races, ReadArray will throw and we catch it
        if (view is null)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }

        try
        {
            int samplesToCopy = Math.Min(count, _audioWorkBuffer.Length);
            view.ReadArray(MmfHeaderSize, _audioWorkBuffer, 0, samplesToCopy);
            Array.Copy(_audioWorkBuffer, 0, buffer, offset, samplesToCopy);
            if (samplesToCopy < count)
                Array.Clear(buffer, offset + samplesToCopy, count - samplesToCopy);
        }
        catch
        {
            Array.Clear(buffer, offset, count);
        }

        return count;
    }

    // ── LoadAsync ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Launches the bridge process, connects the named pipe and shared-memory buffer,
    /// then sends the <c>load</c> command and waits for the ack.
    /// </summary>
    /// <remarks>
    /// If <c>mmk-vst3-bridge.exe</c> is not found at
    /// <c>{AppContext.BaseDirectory}\mmk-vst3-bridge.exe</c>, this method logs a warning and
    /// returns without throwing; <see cref="IsReady"/> remains <see langword="false"/>.<br/>
    /// Any other failure transitions the backend to <c>Faulted</c> and raises
    /// <see cref="BridgeFaulted"/>.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The backend has been disposed.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="instrument"/> is not a <see cref="InstrumentType.Vst3"/> entry,
    /// or <see cref="InstrumentDefinition.Vst3PluginPath"/> is null or empty.
    /// </exception>
    public async Task LoadAsync(InstrumentDefinition instrument, CancellationToken cancellation = default)
    {
        ThrowIfDisposed();

        if (instrument.Type != InstrumentType.Vst3)
        {
            Debug.WriteLine("[Vst3BridgeBackend] LoadAsync called with non-VST3 instrument; ignoring.");
            return;
        }

        if (string.IsNullOrWhiteSpace(instrument.Vst3PluginPath))
        {
            Debug.WriteLine("[Vst3BridgeBackend] Vst3PluginPath is null or empty; ignoring.");
            return;
        }

        // Check for bridge exe — graceful no-op when absent (Phase 2: bridge not yet built)
        var bridgeExePath = Path.Combine(AppContext.BaseDirectory, "mmk-vst3-bridge.exe");
        if (!File.Exists(bridgeExePath))
        {
            Debug.WriteLine(
                $"[Vst3BridgeBackend] Bridge executable not found at '{bridgeExePath}'. " +
                "VST3 support unavailable until mmk-vst3-bridge.exe is deployed.");
            return;
        }

        lock (_stateLock)
        {
            _state = BridgeState.Starting;
        }

        try
        {
            // ── Step 1: Launch bridge process ─────────────────────────────────
            var psi = new ProcessStartInfo
            {
                FileName        = bridgeExePath,
                UseShellExecute = false,
                CreateNoWindow  = true,
            };
            _bridgeProcess = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null for bridge process.");
            int pid = _bridgeProcess.Id;

            // ── Step 2: Connect named pipe (bridge is the server) ─────────────
            _pipeClient = new NamedPipeClientStream(
                ".", $"mmk-vst3-{pid}", PipeDirection.InOut, PipeOptions.Asynchronous);

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            connectCts.CancelAfter(TimeSpan.FromSeconds(5));
            await _pipeClient.ConnectAsync(connectCts.Token).ConfigureAwait(false);

            _pipeWriter = new StreamWriter(_pipeClient, Encoding.UTF8, bufferSize: 4096, leaveOpen: true)
            {
                AutoFlush = false,
            };
            _pipeReader = new StreamReader(_pipeClient, Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);

            // ── Step 3: Open shared-memory audio buffer (bridge creates the MMF) ──
            _mmf = MemoryMappedFile.OpenExisting(
                $"mmk-vst3-audio-{pid}", MemoryMappedFileRights.Read);
            _mmfView = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

            // Validate magic and read frame size from header
            int magic = _mmfView.ReadInt32(0);
            if (magic != MmfMagic)
                throw new InvalidDataException(
                    $"Unexpected shared-memory magic: 0x{magic:X8} (expected 0x{MmfMagic:X8}).");

            _frameSize = _mmfView.ReadInt32(8);
            if (_frameSize <= 0)
                throw new InvalidDataException($"Bridge reported invalid frameSize: {_frameSize}.");

            // Pre-allocate audio work buffer — no further allocation during Read()
            _audioWorkBuffer = new float[_frameSize * AudioChannels];

            // ── Step 4: Send load command ─────────────────────────────────────
            var pluginPathJson = JsonSerializer.Serialize(instrument.Vst3PluginPath);
            var presetPathJson = JsonSerializer.Serialize(instrument.Vst3PresetPath ?? string.Empty);
            var loadCmd = $"{{\"cmd\":\"load\",\"path\":{pluginPathJson},\"preset\":{presetPathJson}}}";

            await _pipeWriter.WriteLineAsync(loadCmd.AsMemory(), cancellation).ConfigureAwait(false);
            await _pipeWriter.FlushAsync(cancellation).ConfigureAwait(false);

            // ── Step 5: Await load ack (5 second timeout) ─────────────────────
            using var ackCts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            ackCts.CancelAfter(TimeSpan.FromSeconds(5));
            var ackLine = await _pipeReader.ReadLineAsync(ackCts.Token).ConfigureAwait(false);

            if (!ParseLoadAck(ackLine, out var errorMessage))
                throw new InvalidOperationException(
                    $"Bridge rejected load command: {errorMessage ?? ackLine ?? "<no response>"}");

            // ── Step 6: Start dedicated pipe writer task ──────────────────────
            _writerCts = new CancellationTokenSource();
            _pipeWriterTask = Task.Run(
                () => RunPipeWriterAsync(_writerCts.Token), CancellationToken.None);

            // ── Step 7: Transition to Running ─────────────────────────────────
            lock (_stateLock)
            {
                _state = BridgeState.Running;
            }
            _isReady = true;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            // Timed-out (not caller-cancelled) — treat as fault
            var msg = "Timed out waiting for bridge to respond.";
            Debug.WriteLine($"[Vst3BridgeBackend] {msg}");
            TransitionToFaulted(msg);
        }
        catch (OperationCanceledException)
        {
            // Caller cancelled — clean up Starting state without raising BridgeFaulted
            lock (_stateLock)
            {
                if (_state == BridgeState.Starting)
                    _state = BridgeState.NotStarted;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Vst3BridgeBackend] LoadAsync failed: {ex.Message}");
            TransitionToFaulted($"Failed to start or connect to bridge: {ex.Message}", ex);
        }
    }

    // ── MIDI commands — AUDIO RENDER THREAD ───────────────────────────────────
    // All methods enqueue a JSON string via Channel.Writer.TryWrite — non-blocking, no allocation.

    /// <inheritdoc/>
    public void NoteOn(int channel, int note, int velocity)
    {
        if (!_isReady) return;
        _commandChannel.Writer.TryWrite(
            $"{{\"cmd\":\"noteOn\",\"channel\":{channel},\"pitch\":{note},\"velocity\":{velocity}}}");
    }

    /// <inheritdoc/>
    public void NoteOff(int channel, int note)
    {
        if (!_isReady) return;
        _commandChannel.Writer.TryWrite(
            $"{{\"cmd\":\"noteOff\",\"channel\":{channel},\"pitch\":{note}}}");
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Safe to call even when the backend is partially disposed (e.g. from
    /// <c>AudioEngine.Dispose()</c>). Silently drops the command if disposed.
    /// </remarks>
    public void NoteOffAll()
    {
        if (_disposed) return;
        _commandChannel.Writer.TryWrite("{\"cmd\":\"noteOffAll\"}");
    }

    /// <inheritdoc/>
    public void SetProgram(int channel, int bank, int program)
    {
        if (!_isReady) return;
        _commandChannel.Writer.TryWrite($"{{\"cmd\":\"setProgram\",\"program\":{program}}}");
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// Sends a <c>shutdown</c> command (fire-and-forget), kills the bridge process, and releases
    /// all IPC resources. Safe to call multiple times.
    /// </remarks>
    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_state == BridgeState.Disposed) return;
            _state = BridgeState.Disposed;
            _disposed = true;
        }
        _isReady = false;

        // Fire-and-forget shutdown: queue the command, then complete the channel so the
        // writer task drains and exits naturally before or after the kill.
        _commandChannel.Writer.TryWrite("{\"cmd\":\"shutdown\"}");
        _commandChannel.Writer.TryComplete();

        // Cancel the writer task
        _writerCts?.Cancel();

        // Kill bridge process (spec: Kill + WaitForExit(1000))
        if (_bridgeProcess is { HasExited: false })
        {
            try { _bridgeProcess.Kill(); }
            catch { /* process may have already exited */ }
        }
        try { _bridgeProcess?.WaitForExit(1000); }
        catch { }

        // Dispose IPC resources in order: writers first, then underlying streams and handles
        _pipeWriter?.Dispose();
        _pipeReader?.Dispose();
        _pipeClient?.Dispose();
        _mmfView?.Dispose();
        _mmf?.Dispose();
        _bridgeProcess?.Dispose();
        _writerCts?.Dispose();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(Vst3BridgeBackend));
    }

    private void TransitionToFaulted(string reason, Exception? ex = null)
    {
        bool raised = false;
        lock (_stateLock)
        {
            if (_state is BridgeState.Running or BridgeState.Starting)
            {
                _state = BridgeState.Faulted;
                _isReady = false;
                raised = true;
            }
        }
        if (raised)
        {
            Debug.WriteLine($"[Vst3BridgeBackend] Faulted: {reason}");
            BridgeFaulted?.Invoke(this, new BridgeFaultedEventArgs(reason, ex));
        }
    }

    /// <summary>
    /// Background task: drains the command channel and writes JSON lines to the named pipe.
    /// Transitions to <c>Faulted</c> on any pipe I/O error.
    /// </summary>
    private async Task RunPipeWriterAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var cmd in _commandChannel.Reader.ReadAllAsync(ct))
            {
                if (_pipeWriter is not { } writer) break;
                try
                {
                    await writer.WriteLineAsync(cmd.AsMemory(), ct).ConfigureAwait(false);
                    await writer.FlushAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    TransitionToFaulted("IPC named-pipe write failed.", ex);
                    break;
                }
            }
        }
        catch (OperationCanceledException) { /* normal shutdown path */ }
    }

    private static bool ParseLoadAck(string? line, out string? errorMessage)
    {
        errorMessage = null;
        if (line is null) return false;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ack", out var ack) || ack.GetString() != "load")
                return false;
            if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            {
                if (root.TryGetProperty("error", out var err))
                    errorMessage = err.GetString();
                return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
