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
/// Commands (host → bridge) are sent as JSON lines over a <see cref="NamedPipeServerStream"/> (host is server).
/// Audio is transferred via a <see cref="MemoryMappedFile"/> ring buffer (host creates, bridge writes).</para>
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
/// MidiCommand structs into an unbounded <see cref="Channel{T}"/> — no allocations, no blocking.
/// A dedicated background <see cref="Task"/> drains the channel, serializes to JSON, and writes to the named pipe.
/// <see cref="Read"/> is also called on the audio render thread and copies from shared memory into
/// the output buffer without allocating.</para>
/// </summary>
public sealed class Vst3BridgeBackend : IInstrumentBackend, IEditorCapable, ISampleProvider
{
    // ── Bridge state machine ──────────────────────────────────────────────────

    private enum BridgeState { NotStarted, Starting, Running, Faulted, Disposed }

    private BridgeState _state = BridgeState.NotStarted;
    private readonly object _stateLock = new();
    private volatile bool _isReady;
    private volatile bool _disposed;
    private volatile int _lastReadPos = -1;
    private volatile bool _supportsEditor;
    private string _editorAvailabilityDescription = "VST3 plugin has not been loaded yet.";

    // ── IPC resources ─────────────────────────────────────────────────────────

    private Process? _bridgeProcess;
    private NamedPipeServerStream? _pipeServer;
    private StreamWriter? _pipeWriter;
    private StreamReader? _pipeReader;
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _mmfView;
    private readonly object _bridgeDiagnosticsLock = new();
    private readonly StringBuilder _bridgeDiagnostics = new();

    // ── Command channel (audio thread → pipe writer task, lock-free) ──────────

    private readonly struct MidiCommand
    {
        public enum Kind : byte { NoteOn, NoteOff, NoteOffAll, SetProgram, Load, Shutdown, OpenEditor, CloseEditor }
        public Kind CommandKind { get; init; }
        public int Channel { get; init; }
        public int Pitch { get; init; }
        public int Velocity { get; init; }
        public int Program { get; init; }
        public string? Path { get; init; }
        public string? Preset { get; init; }
    }

    private readonly Channel<MidiCommand> _commandChannel = Channel.CreateUnbounded<MidiCommand>(
        new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });

    private Task? _pipeWriterTask;
    private CancellationTokenSource? _writerCts;

    // Serialises pipe writes between RunPipeWriterAsync (audio thread) and
    // OpenEditorAsync / CloseEditorAsync (UI thread).
    private readonly SemaphoreSlim _pipeLock = new(1, 1);

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
            // Check if bridge has written a new frame since last read
            int writePos = view.ReadInt32(12);   // writePos is at MMF header offset 12
            if (writePos == _lastReadPos)
            {
                // Bridge hasn't written a new frame yet — return silence
                Array.Clear(buffer, offset, count);
                return count;
            }
            _lastReadPos = writePos;

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

        // Check for bridge exe — raise BridgeFaulted when absent so callers can surface the error
        var bridgeExePath = Path.Combine(AppContext.BaseDirectory, "mmk-vst3-bridge.exe");
        if (!File.Exists(bridgeExePath))
        {
            var reason = $"Bridge executable not found at '{bridgeExePath}'. Deploy mmk-vst3-bridge.exe to enable VST3 support.";
            _editorAvailabilityDescription = reason;
            Debug.WriteLine($"[Vst3BridgeBackend] {reason}");
            BridgeFaulted?.Invoke(this, new BridgeFaultedEventArgs(reason));
            return;
        }

        bool needsReset;
        lock (_stateLock)
        {
            needsReset = _state is BridgeState.Running or BridgeState.Faulted or BridgeState.Starting;
        }
        if (needsReset)
            await ResetForReloadAsync().ConfigureAwait(false);

        lock (_stateLock)
        {
            _state = BridgeState.Starting;
        }

        ResetBridgeDiagnostics();

        try
        {
            // ── Step 1: Create IPC resources (host is server) ─────────────────
            int hostPid = Process.GetCurrentProcess().Id;
            var pipeName = $"mmk-vst3-{hostPid}";
            var mmfName = $"mmk-vst3-audio-{hostPid}";

            // Host creates named pipe server
            _pipeServer = new NamedPipeServerStream(
                pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            // Host creates shared-memory audio buffer
            // Header (16 bytes) + audio data (960 frames * 2 channels * 4 bytes = 7680 bytes)
            const int frameSize = 960;
            long mmfSize = MmfHeaderSize + (frameSize * AudioChannels * sizeof(float));
            _mmf = MemoryMappedFile.CreateNew(mmfName, mmfSize, MemoryMappedFileAccess.ReadWrite);
            _mmfView = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);

            // Write MMF header
            _mmfView.Write(0, MmfMagic);      // magic
            _mmfView.Write(4, 1);              // version
            _mmfView.Write(8, frameSize);      // frameSize
            _mmfView.Write(12, 0);             // writePos (initial)

            _frameSize = frameSize;
            _audioWorkBuffer = new float[frameSize * AudioChannels];

            // ── Step 2: Launch bridge process ─────────────────────────────────
            var psi = new ProcessStartInfo
            {
                FileName        = bridgeExePath,
                Arguments       = $"{hostPid}",
                UseShellExecute = false,
                CreateNoWindow  = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            _bridgeProcess = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null for bridge process.");
            StartBridgeDiagnosticsCapture(_bridgeProcess);

            // ── Step 3: Wait for bridge to connect to pipe ────────────────────
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            connectCts.CancelAfter(TimeSpan.FromSeconds(5));
            await _pipeServer.WaitForConnectionAsync(connectCts.Token).ConfigureAwait(false);

            _pipeWriter = new StreamWriter(_pipeServer, Encoding.UTF8, bufferSize: 4096, leaveOpen: true)
            {
                AutoFlush = false,
            };
            _pipeReader = new StreamReader(_pipeServer, Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);

            // ── Step 4: Send load command ─────────────────────────────────────
            var loadCmd = new MidiCommand
            {
                CommandKind = MidiCommand.Kind.Load,
                Path = instrument.Vst3PluginPath,
                Preset = instrument.Vst3PresetPath ?? string.Empty
            };
            var loadJson = SerializeCommand(loadCmd);
            await _pipeWriter.WriteLineAsync(loadJson.AsMemory(), cancellation).ConfigureAwait(false);
            await _pipeWriter.FlushAsync(cancellation).ConfigureAwait(false);

            // ── Step 5: Await load ack (5 second timeout) ─────────────────────
            using var ackCts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            ackCts.CancelAfter(TimeSpan.FromSeconds(5));
            var ackLine = await _pipeReader.ReadLineAsync(ackCts.Token).ConfigureAwait(false);

            if (!ParseLoadAck(ackLine, out var errorMessage, out var supportsEditor, out var editorDiagnostics))
                throw new InvalidOperationException(
                    $"Bridge rejected load command: {DescribeLoadAckFailure(ackLine, errorMessage)}");

            _supportsEditor = supportsEditor;
            _editorAvailabilityDescription = string.IsNullOrWhiteSpace(editorDiagnostics)
                ? (supportsEditor ? "Plugin editor is available." : "Plugin editor is not available.")
                : editorDiagnostics;

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
            var msg = DescribeBridgeFailure("Timed out waiting for bridge to respond.");
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
        _commandChannel.Writer.TryWrite(new MidiCommand
        {
            CommandKind = MidiCommand.Kind.NoteOn,
            Channel = channel,
            Pitch = note,
            Velocity = velocity
        });
    }

    /// <inheritdoc/>
    public void NoteOff(int channel, int note)
    {
        if (!_isReady) return;
        _commandChannel.Writer.TryWrite(new MidiCommand
        {
            CommandKind = MidiCommand.Kind.NoteOff,
            Channel = channel,
            Pitch = note
        });
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Safe to call even when the backend is partially disposed (e.g. from
    /// <c>AudioEngine.Dispose()</c>). Silently drops the command if disposed.
    /// </remarks>
    public void NoteOffAll()
    {
        if (_disposed) return;
        _commandChannel.Writer.TryWrite(new MidiCommand
        {
            CommandKind = MidiCommand.Kind.NoteOffAll
        });
    }

    /// <inheritdoc/>
    public void SetProgram(int channel, int bank, int program)
    {
        if (!_isReady) return;
        _commandChannel.Writer.TryWrite(new MidiCommand
        {
            CommandKind = MidiCommand.Kind.SetProgram,
            Channel = channel,
            Program = program
        });
    }

    // ── IEditorCapable ────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public bool SupportsEditor => _isReady && _supportsEditor;

    /// <summary>Human-readable description of the current editor availability state.</summary>
    public string EditorAvailabilityDescription => _editorAvailabilityDescription;

    /// <inheritdoc/>
    public async Task OpenEditorAsync()
    {
        if (!_isReady)
            throw new InvalidOperationException("Cannot open editor: bridge is not ready.");
        if (!_supportsEditor)
            throw new InvalidOperationException(_editorAvailabilityDescription);

        ThrowIfDisposed();

        try
        {
            var openCmd = new MidiCommand { CommandKind = MidiCommand.Kind.OpenEditor };
            var json = SerializeCommand(openCmd);

            if (_pipeWriter is not { } writer)
                throw new InvalidOperationException("Pipe writer is not available.");

            await _pipeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await writer.WriteLineAsync(json.AsMemory()).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
            }
            finally
            {
                _pipeLock.Release();
            }

            // Await editor_opened ACK (5 second timeout)
            using var ackCts = new CancellationTokenSource();
            ackCts.CancelAfter(TimeSpan.FromSeconds(5));

            if (_pipeReader is not { } reader)
                throw new InvalidOperationException("Pipe reader is not available.");

            var ackLine = await reader.ReadLineAsync(ackCts.Token).ConfigureAwait(false);
            if (!ParseEditorAck(ackLine, "editor_opened"))
                throw new InvalidOperationException(
                    $"Failed to open editor: {ExtractBridgeError(ackLine)}");
            Debug.WriteLine("[Vst3BridgeBackend] Editor opened successfully.");
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("Timed out waiting for editor_opened ACK.");
        }
    }

    /// <inheritdoc/>
    public async Task CloseEditorAsync()
    {
        if (!_isReady)
            throw new InvalidOperationException("Cannot close editor: bridge is not ready.");

        ThrowIfDisposed();

        try
        {
            var closeCmd = new MidiCommand { CommandKind = MidiCommand.Kind.CloseEditor };
            var json = SerializeCommand(closeCmd);

            if (_pipeWriter is not { } writer)
                throw new InvalidOperationException("Pipe writer is not available.");

            await _pipeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await writer.WriteLineAsync(json.AsMemory()).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
            }
            finally
            {
                _pipeLock.Release();
            }

            // Await editor_closed ACK (5 second timeout)
            using var ackCts = new CancellationTokenSource();
            ackCts.CancelAfter(TimeSpan.FromSeconds(5));

            if (_pipeReader is not { } reader)
                throw new InvalidOperationException("Pipe reader is not available.");

            var ackLine = await reader.ReadLineAsync(ackCts.Token).ConfigureAwait(false);
            if (!ParseEditorAck(ackLine, "editor_closed"))
                throw new InvalidOperationException(
                    $"Failed to close editor: {ExtractBridgeError(ackLine)}");
            Debug.WriteLine("[Vst3BridgeBackend] Editor closed successfully.");
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("Timed out waiting for editor_closed ACK.");
        }
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// Sends a <c>shutdown</c> command, waits for graceful exit, then kills if necessary.
    /// Safe to call multiple times.
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
        _supportsEditor = false;
        _lastReadPos = -1;
        _editorAvailabilityDescription = "VST3 plugin backend has been disposed.";

        // Enqueue shutdown command and complete channel
        _commandChannel.Writer.TryWrite(new MidiCommand { CommandKind = MidiCommand.Kind.Shutdown });
        _commandChannel.Writer.Complete();

        // Wait for writer task to drain the shutdown command (500ms timeout)
        try { _pipeWriterTask?.Wait(TimeSpan.FromMilliseconds(500)); }
        catch { }

        // Give bridge process graceful exit time (2s timeout)
        if (_bridgeProcess is { HasExited: false })
        {
            if (!_bridgeProcess.WaitForExit(2000))
            {
                try { _bridgeProcess.Kill(); }
                catch { }
            }
        }

        // Cancel CTS (cleanup, writer task already exited)
        _writerCts?.Cancel();

        // Dispose IPC resources in order: writers first, then underlying streams and handles
        _pipeWriter?.Dispose();
        _pipeReader?.Dispose();
        _pipeServer?.Dispose();
        _mmfView?.Dispose();
        _mmf?.Dispose();
        _bridgeProcess?.Dispose();
        _writerCts?.Dispose();
        _pipeLock.Dispose();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Tears down a previous bridge session without permanently closing the command channel.
    /// Called at the start of <see cref="LoadAsync"/> when the backend is being reloaded.
    /// Unlike <see cref="Dispose"/>, this leaves the instance usable for a subsequent load.
    /// </summary>
    private async Task ResetForReloadAsync()
    {
        _isReady = false;
        _supportsEditor = false;
        _lastReadPos = -1;
        _editorAvailabilityDescription = "VST3 plugin is reloading.";

        // Stop the background writer task first
        _writerCts?.Cancel();
        if (_pipeWriterTask is { } writerTask)
        {
            try { await writerTask.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false); }
            catch { /* cancelled or timed out — safe to continue */ }
            _pipeWriterTask = null;
        }
        _writerCts?.Dispose();
        _writerCts = null;

        // Kill the old bridge process
        if (_bridgeProcess is { HasExited: false } proc)
        {
            try { proc.Kill(); } catch { }
            try { proc.WaitForExit(500); } catch { }
        }

        // Dispose IPC resources — order matters: writers first, then streams, then handles
        _pipeWriter?.Dispose(); _pipeWriter = null;
        _pipeReader?.Dispose(); _pipeReader = null;
        _pipeServer?.Dispose(); _pipeServer = null;
        _mmfView?.Dispose();   _mmfView   = null;
        _mmf?.Dispose();       _mmf       = null;
        _bridgeProcess?.Dispose(); _bridgeProcess = null;

        lock (_stateLock)
        {
            _state = BridgeState.NotStarted;
        }

        ResetBridgeDiagnostics();
    }


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
                _supportsEditor = false;
                raised = true;
            }
        }
        if (raised)
        {
            _editorAvailabilityDescription = reason;
            Debug.WriteLine($"[Vst3BridgeBackend] Faulted: {reason}");
            BridgeFaulted?.Invoke(this, new BridgeFaultedEventArgs(reason, ex));
        }
    }

    /// <summary>
    /// Background task: drains the command channel, serializes to JSON, and writes to the named pipe.
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
                    var json = SerializeCommand(cmd);
                    await _pipeLock.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        await writer.WriteLineAsync(json.AsMemory(), ct).ConfigureAwait(false);
                        await writer.FlushAsync(ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        _pipeLock.Release();
                    }
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

    private static string SerializeCommand(MidiCommand cmd)
    {
        return cmd.CommandKind switch
        {
            MidiCommand.Kind.NoteOn => $"{{\"cmd\":\"noteOn\",\"channel\":{cmd.Channel},\"pitch\":{cmd.Pitch},\"velocity\":{cmd.Velocity}}}",
            MidiCommand.Kind.NoteOff => $"{{\"cmd\":\"noteOff\",\"channel\":{cmd.Channel},\"pitch\":{cmd.Pitch}}}",
            MidiCommand.Kind.NoteOffAll => "{\"cmd\":\"noteOffAll\"}",
            MidiCommand.Kind.SetProgram => $"{{\"cmd\":\"setProgram\",\"program\":{cmd.Program}}}",
            MidiCommand.Kind.Load => $"{{\"cmd\":\"load\",\"path\":{JsonSerializer.Serialize(cmd.Path)},\"preset\":{JsonSerializer.Serialize(cmd.Preset)}}}",
            MidiCommand.Kind.Shutdown => "{\"cmd\":\"shutdown\"}",
            MidiCommand.Kind.OpenEditor => "{\"cmd\":\"openEditor\"}",
            MidiCommand.Kind.CloseEditor => "{\"cmd\":\"closeEditor\"}",
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    private static bool ParseLoadAck(string? line, out string? errorMessage, out bool supportsEditor, out string? editorDiagnostics)
    {
        errorMessage = null;
        supportsEditor = false;
        editorDiagnostics = null;
        if (line is null) return false;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ack", out var ack))
                return false;

            var ackValue = ack.GetString();
            if (ackValue != "load" && ackValue != "load_ack")
                return false;

            if (root.TryGetProperty("supportsEditor", out var supportsEditorJson) &&
                supportsEditorJson.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                supportsEditor = supportsEditorJson.GetBoolean();
            }

            if (root.TryGetProperty("editorDiagnostics", out var editorDiagnosticsJson) &&
                editorDiagnosticsJson.ValueKind == JsonValueKind.String)
            {
                editorDiagnostics = editorDiagnosticsJson.GetString();
            }

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

    private static bool ParseEditorAck(string? response, string expectedAck)
    {
        if (string.IsNullOrWhiteSpace(response))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ack", out var ack))
                return false;
            if (ack.GetString() != expectedAck)
                return false;
            // If the bridge explicitly reports ok:false, treat as failure.
            if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
                return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Extracts the "error" field from a bridge ACK JSON line, or returns the raw line.</summary>
    private static string ExtractBridgeError(string? response)
    {
        if (string.IsNullOrWhiteSpace(response)) return "<no response>";
        try
        {
            using var doc = JsonDocument.Parse(response);
            if (doc.RootElement.TryGetProperty("error", out var err))
                return err.GetString() ?? response;
        }
        catch { }
        return response;
    }

    private void StartBridgeDiagnosticsCapture(Process bridgeProcess)
    {
        _ = Task.Run(() => PumpBridgeDiagnosticsAsync(bridgeProcess.StandardError, "bridge stderr"));
        _ = Task.Run(() => PumpBridgeDiagnosticsAsync(bridgeProcess.StandardOutput, "bridge stdout"));
    }

    private async Task PumpBridgeDiagnosticsAsync(StreamReader reader, string source)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    AppendBridgeDiagnostic($"{source}: {line.Trim()}");
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
    }

    private void ResetBridgeDiagnostics()
    {
        lock (_bridgeDiagnosticsLock)
        {
            _bridgeDiagnostics.Clear();
        }
    }

    private void AppendBridgeDiagnostic(string message)
    {
        const int maxLength = 4096;

        lock (_bridgeDiagnosticsLock)
        {
            if (_bridgeDiagnostics.Length > 0)
                _bridgeDiagnostics.Append(' ');

            _bridgeDiagnostics.Append(message);
            if (_bridgeDiagnostics.Length > maxLength)
                _bridgeDiagnostics.Remove(0, _bridgeDiagnostics.Length - maxLength);
        }
    }

    private string? GetBridgeDiagnosticsSnapshot()
    {
        lock (_bridgeDiagnosticsLock)
        {
            return _bridgeDiagnostics.Length == 0 ? null : _bridgeDiagnostics.ToString();
        }
    }

    private string DescribeLoadAckFailure(string? ackLine, string? errorMessage)
    {
        if (!string.IsNullOrWhiteSpace(errorMessage))
            return errorMessage;

        if (!string.IsNullOrWhiteSpace(ackLine))
            return ackLine;

        return DescribeBridgeFailure("<no response>");
    }

    private string DescribeBridgeFailure(string fallbackMessage)
    {
        var diagnostics = GetBridgeDiagnosticsSnapshot();
        if (_bridgeProcess is { HasExited: true } process)
        {
            var exitMessage = $"Bridge process exited with code 0x{process.ExitCode:X8}.";
            return string.IsNullOrWhiteSpace(diagnostics)
                ? $"{fallbackMessage} {exitMessage}"
                : $"{fallbackMessage} {exitMessage} {diagnostics}";
        }

        return string.IsNullOrWhiteSpace(diagnostics)
            ? fallbackMessage
            : $"{fallbackMessage} {diagnostics}";
    }
}
