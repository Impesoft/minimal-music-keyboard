using MinimalMusicKeyboard.Interfaces;
using MinimalMusicKeyboard.Models;

namespace MinimalMusicKeyboard.Services;

/// <summary>
/// Listens to MIDI events from <see cref="IMidiDeviceService"/> and routes them to the audio engine:
///   • NoteOn/NoteOff  → audio engine (unless the note is a mapped instrument-button trigger)
///   • Program Change  → catalog lookup → instrument switch
///   • Control Change  → bank-select accumulation (CC#0/CC#32) or mapped button trigger
///
/// Button mappings are updated live via <see cref="UpdateButtonMappings"/>.
/// </summary>
public sealed class MidiInstrumentSwitcher : IDisposable
{
    private readonly InstrumentCatalog _catalog;
    private readonly IAudioEngine _audioEngine;
    private readonly IMidiDeviceService _midiSource;

    // Accumulated bank select state (MIDI spec: CC#0 MSB + CC#32 LSB precede a PC)
    private int _pendingBankMsb;
    private int _pendingBankLsb;

    // Button mappings — swapped atomically so the MIDI callback thread always sees a consistent snapshot.
    private InstrumentButtonMapping[] _buttonMappings = [];

    /// <summary>
    /// Raised on the MIDI callback thread whenever the active instrument changes.
    /// Subscribers must marshal to the UI thread if they update controls.
    /// </summary>
    public event EventHandler<InstrumentDefinition?>? ActiveInstrumentChanged;

    private bool _disposed;

    public MidiInstrumentSwitcher(
        InstrumentCatalog catalog,
        IAudioEngine audioEngine,
        IMidiDeviceService midiSource)
    {
        _catalog     = catalog     ?? throw new ArgumentNullException(nameof(catalog));
        _audioEngine = audioEngine ?? throw new ArgumentNullException(nameof(audioEngine));
        _midiSource  = midiSource  ?? throw new ArgumentNullException(nameof(midiSource));

        _midiSource.NoteOnReceived        += OnNoteOnReceived;
        _midiSource.NoteOffReceived       += OnNoteOffReceived;
        _midiSource.ProgramChangeReceived += OnProgramChangeReceived;
        _midiSource.ControlChangeReceived += OnControlChangeReceived;
    }

    /// <summary>
    /// Replaces the active button mappings. Thread-safe — can be called from the UI thread
    /// while MIDI events are arriving on the callback thread.
    /// </summary>
    public void UpdateButtonMappings(InstrumentButtonMapping[] mappings)
        => Volatile.Write(ref _buttonMappings, mappings ?? []);

    // ── Event handlers (called on the MIDI callback thread) ───────────────────

    private void OnNoteOnReceived(object? sender, MidiNoteEventArgs e)
    {
        // If this note is assigned as an instrument-button trigger, switch and consume.
        if (TryTriggerButton(e.Channel, MidiTriggerType.Note, e.Note)) return;
        _audioEngine.NoteOn(e.Channel, e.Note, e.Velocity);
    }

    private void OnNoteOffReceived(object? sender, MidiNoteEventArgs e)
    {
        // Suppress NoteOff for mapped trigger notes so no lingering voice is sent.
        if (IsMappedTrigger(e.Channel, MidiTriggerType.Note, e.Note)) return;
        _audioEngine.NoteOff(e.Channel, e.Note);
    }

    private void OnControlChangeReceived(object? sender, MidiControlEventArgs e)
    {
        // CC button triggers fire on value > 0 (button press); value == 0 is release — ignore.
        if (e.Value > 0 && TryTriggerButton(e.Channel, MidiTriggerType.ControlChange, e.ControlNumber))
            return;

        switch (e.ControlNumber)
        {
            case 0:  _pendingBankMsb = e.Value; break; // Bank Select MSB
            case 32: _pendingBankLsb = e.Value; break; // Bank Select LSB
        }
    }

    private void OnProgramChangeReceived(object? sender, MidiProgramEventArgs e)
    {
        int bank    = (_pendingBankMsb << 7) | _pendingBankLsb;
        int program = e.ProgramNumber;

        var instrument = _catalog.GetByProgramNumber(program);
        if (instrument is not null)
        {
            _audioEngine.SelectInstrument(instrument);
            ActiveInstrumentChanged?.Invoke(this, instrument);
        }
        else
        {
            _audioEngine.SetPreset(e.Channel, bank, program);
            ActiveInstrumentChanged?.Invoke(this, null);
        }
    }

    // ── Button mapping helpers ─────────────────────────────────────────────────

    private bool TryTriggerButton(int channel, MidiTriggerType type, int number)
    {
        var mappings = Volatile.Read(ref _buttonMappings);
        foreach (var m in mappings)
        {
            if (!m.Matches(channel, type, number)) continue;

            var instrument = _catalog.GetById(m.InstrumentId!);
            if (instrument is not null)
            {
                _audioEngine.SelectInstrument(instrument);
                ActiveInstrumentChanged?.Invoke(this, instrument);
            }
            return true; // consumed regardless
        }
        return false;
    }

    private bool IsMappedTrigger(int channel, MidiTriggerType type, int number)
    {
        var mappings = Volatile.Read(ref _buttonMappings);
        foreach (var m in mappings)
            if (m.Matches(channel, type, number)) return true;
        return false;
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _midiSource.NoteOnReceived        -= OnNoteOnReceived;
        _midiSource.NoteOffReceived       -= OnNoteOffReceived;
        _midiSource.ProgramChangeReceived -= OnProgramChangeReceived;
        _midiSource.ControlChangeReceived -= OnControlChangeReceived;
    }
}
