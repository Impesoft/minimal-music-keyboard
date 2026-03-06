using NAudio.Midi;
using MinimalMusicKeyboard.Midi;
using MinimalMusicKeyboard.Models;
using MinimalMusicKeyboard.Interfaces;
using System.Diagnostics;

namespace MinimalMusicKeyboard.Services;

/// <summary>
/// Owns the NAudio MIDI input device lifecycle: enumeration, opening, listening,
/// disconnect handling, and reconnect polling.
///
/// Threading: NAudio fires MessageReceived/ErrorReceived on its own Win32 callback thread.
/// Event handlers on this class are therefore called from that callback thread — callers
/// that need to update UI must marshal via DispatcherQueue.TryEnqueue().
///
/// Implements IMidiDeviceService so MidiMessageRouter (Spike) can depend on the interface.
/// </summary>
public sealed class MidiDeviceService : IMidiDeviceService, IDisposable
{
    public enum ConnectionState { Disconnected, Connected, Error }

    private MidiIn? _midiIn;
    private int _deviceIndex = -1;
    private string? _deviceName;
    private CancellationTokenSource? _reconnectCts;
    private ConnectionState _state = ConnectionState.Disconnected;
    private bool _disposed;

    // Protects _midiIn open/close operations from concurrent reconnect and dispose.
    private readonly object _deviceLock = new();

    public ConnectionState State => _state;
    public string? DeviceName => _deviceName;

    /// <summary>Fired on the NAudio callback thread when a MIDI message arrives.</summary>
    public event EventHandler<MidiInMessageEventArgs>? MidiMessageReceived;

    /// <inheritdoc cref="IMidiDeviceService.NoteOnReceived"/>
    public event EventHandler<MidiNoteEventArgs>? NoteOnReceived;

    /// <inheritdoc cref="IMidiDeviceService.NoteOffReceived"/>
    public event EventHandler<MidiNoteEventArgs>? NoteOffReceived;

    /// <inheritdoc cref="IMidiDeviceService.ProgramChangeReceived"/>
    public event EventHandler<MidiProgramEventArgs>? ProgramChangeReceived;

    /// <inheritdoc cref="IMidiDeviceService.ControlChangeReceived"/>
    public event EventHandler<MidiControlEventArgs>? ControlChangeReceived;

    /// <summary>Fired when the device is physically disconnected.</summary>
    public event EventHandler? DeviceDisconnected;

    /// <summary>Fired when a device connection is (re-)established.</summary>
    public event EventHandler? DeviceConnected;

    // -------------------------------------------------------------------------
    // Device enumeration
    // -------------------------------------------------------------------------

    public IReadOnlyList<MidiDeviceInfo> EnumerateDevices()
    {
        var devices = new List<MidiDeviceInfo>(MidiIn.NumberOfDevices);
        for (int i = 0; i < MidiIn.NumberOfDevices; i++)
        {
            devices.Add(new MidiDeviceInfo(i, MidiIn.DeviceInfo(i).ProductName));
        }
        return devices;
    }

    // -------------------------------------------------------------------------
    // Open / connect
    // -------------------------------------------------------------------------

    /// <summary>
    /// Tries to open the first device whose name matches <paramref name="deviceName"/>.
    /// Returns false (and sets state to Disconnected) if the device is not present —
    /// the reconnect loop will retry automatically (architecture Section 3.2).
    /// </summary>
    public bool TryOpen(string deviceName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _deviceName = deviceName; // remember for reconnect

        for (int i = 0; i < MidiIn.NumberOfDevices; i++)
        {
            if (MidiIn.DeviceInfo(i).ProductName == deviceName)
                return TryOpenByIndex(i);
        }

        Debug.WriteLine($"[MidiDeviceService] Device '{deviceName}' not found — starting reconnect loop.");
        ScheduleReconnect();
        return false;
    }

    private bool TryOpenByIndex(int index)
    {
        lock (_deviceLock)
        {
            CloseCurrentDevice();

            try
            {
                _midiIn = new MidiIn(index);
                _midiIn.MessageReceived += OnMessageReceived;
                _midiIn.ErrorReceived += OnErrorReceived;
                _midiIn.Start();

                _deviceIndex = index;
                _state = ConnectionState.Connected;

                Debug.WriteLine($"[MidiDeviceService] Opened device {index}: {MidiIn.DeviceInfo(index).ProductName}");
                DeviceConnected?.Invoke(this, EventArgs.Empty);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MidiDeviceService] Failed to open device {index}: {ex.Message}");
                CloseCurrentDevice();
                _state = ConnectionState.Error;
                return false;
            }
        }
    }

    // -------------------------------------------------------------------------
    // MIDI callbacks (called on NAudio's Win32 callback thread)
    // -------------------------------------------------------------------------

    private void OnMessageReceived(object? sender, MidiInMessageEventArgs e)
    {
        try
        {
            MidiMessageReceived?.Invoke(this, e);
            DispatchTypedEvent(e);
        }
        catch (Exception ex)
        {
            // Never let a subscriber exception kill the MIDI callback thread.
            Debug.WriteLine($"[MidiDeviceService] Error in MidiMessageReceived handler: {ex.Message}");
        }
    }

    /// <summary>
    /// Parses the raw NAudio event and fires the appropriate typed event.
    /// Typed events are what MidiMessageRouter (Spike) and other consumers use.
    /// </summary>
    private void DispatchTypedEvent(MidiInMessageEventArgs e)
    {
        if (e.MidiEvent is null) return;

        switch (e.MidiEvent.CommandCode)
        {
            case MidiCommandCode.NoteOn when e.MidiEvent is NoteOnEvent noteOn:
                // NAudio uses NoteOn with velocity=0 to mean NoteOff (MIDI spec allows this).
                if (noteOn.Velocity > 0)
                    NoteOnReceived?.Invoke(this, new MidiNoteEventArgs
                    {
                        Channel  = noteOn.Channel,
                        Note     = noteOn.NoteNumber,
                        Velocity = noteOn.Velocity,
                    });
                else
                    NoteOffReceived?.Invoke(this, new MidiNoteEventArgs
                    {
                        Channel  = noteOn.Channel,
                        Note     = noteOn.NoteNumber,
                        Velocity = 0,
                    });
                break;

            case MidiCommandCode.NoteOff when e.MidiEvent is NoteEvent noteOff:
                NoteOffReceived?.Invoke(this, new MidiNoteEventArgs
                {
                    Channel  = noteOff.Channel,
                    Note     = noteOff.NoteNumber,
                    Velocity = 0,
                });
                break;

            case MidiCommandCode.PatchChange when e.MidiEvent is PatchChangeEvent pc:
                ProgramChangeReceived?.Invoke(this, new MidiProgramEventArgs
                {
                    Channel = pc.Channel,
                    ProgramNumber = pc.Patch,
                });
                break;

            case MidiCommandCode.ControlChange when e.MidiEvent is ControlChangeEvent cc:
                ControlChangeReceived?.Invoke(this, new MidiControlEventArgs
                {
                    Channel = cc.Channel,
                    ControlNumber = (int)cc.Controller,
                    Value = cc.ControllerValue,
                });
                break;
        }
    }

    private void OnErrorReceived(object? sender, MidiInMessageEventArgs e)
    {
        // NAudio fires ErrorReceived when the Win32 MIDI callback reports an error —
        // the most common cause is USB device physical disconnect.
        Debug.WriteLine("[MidiDeviceService] MIDI error received — device likely disconnected.");
        HandleDisconnect();
    }

    // -------------------------------------------------------------------------
    // Disconnect / reconnect
    // -------------------------------------------------------------------------

    private void HandleDisconnect()
    {
        bool wasConnected;

        lock (_deviceLock)
        {
            wasConnected = _state == ConnectionState.Connected;
            if (!wasConnected) return; // already handling disconnect

            _state = ConnectionState.Disconnected;
            CloseCurrentDevice();
        }

        DeviceDisconnected?.Invoke(this, EventArgs.Empty);
        ScheduleReconnect();
    }

    private void ScheduleReconnect()
    {
        if (_deviceName is null || _disposed) return;

        // Cancel any existing reconnect attempt before starting a new one.
        _reconnectCts?.Cancel();
        _reconnectCts?.Dispose();
        _reconnectCts = new CancellationTokenSource();
        var token = _reconnectCts.Token;
        var targetDevice = _deviceName;

        // Run the reconnect loop on a background thread.
        // It polls every 2s as specified in the architecture (Section 3.2).
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested && !_disposed)
            {
                try
                {
                    await Task.Delay(2_000, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (_disposed) break;

                if (TryOpenByIndex(FindDeviceIndex(targetDevice)))
                {
                    Debug.WriteLine($"[MidiDeviceService] Reconnected to '{targetDevice}'.");
                    break;
                }
            }
        }, token);
    }

    private static int FindDeviceIndex(string name)
    {
        for (int i = 0; i < MidiIn.NumberOfDevices; i++)
        {
            if (MidiIn.DeviceInfo(i).ProductName == name)
                return i;
        }
        return -1;
    }

    // -------------------------------------------------------------------------
    // Close helpers
    // -------------------------------------------------------------------------

    /// <summary>Must be called inside _deviceLock.</summary>
    private void CloseCurrentDevice()
    {
        if (_midiIn is null) return;

        try
        {
            _midiIn.MessageReceived -= OnMessageReceived;
            _midiIn.ErrorReceived -= OnErrorReceived;
            _midiIn.Stop();
            _midiIn.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MidiDeviceService] Error closing MidiIn: {ex.Message}");
        }
        finally
        {
            _midiIn = null;
            _deviceIndex = -1;
        }
    }

    // -------------------------------------------------------------------------
    // IDisposable
    // -------------------------------------------------------------------------

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Stop reconnect loop first so it doesn't race with CloseCurrentDevice.
        _reconnectCts?.Cancel();
        _reconnectCts?.Dispose();
        _reconnectCts = null;

        lock (_deviceLock)
        {
            CloseCurrentDevice();
        }

        // Explicitly null event multicast chains to release subscriber references.
        MidiMessageReceived = null;
        NoteOnReceived = null;
        NoteOffReceived = null;
        ProgramChangeReceived = null;
        ControlChangeReceived = null;
        DeviceDisconnected = null;
        DeviceConnected = null;

        GC.SuppressFinalize(this);
    }
}
