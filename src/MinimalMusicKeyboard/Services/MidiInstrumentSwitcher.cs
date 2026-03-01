using MinimalMusicKeyboard.Interfaces;
using MinimalMusicKeyboard.Models;

namespace MinimalMusicKeyboard.Services;

/// <summary>
/// Listens to MIDI Program Change and Control Change events from <see cref="IMidiDeviceService"/>
/// and triggers instrument switches on the <see cref="IAudioEngine"/>.
///
/// Bank Select support (MIDI standard):
///   CC#0  (Bank Select MSB) — accumulated until the next PC message
///   CC#32 (Bank Select LSB) — accumulated until the next PC message
///   PC    (Program Change)  — triggers the switch with the accumulated bank values
///
/// Bank values are "sticky" per MIDI spec — no timeout needed.
/// </summary>
public sealed class MidiInstrumentSwitcher : IDisposable
{
    private readonly InstrumentCatalog _catalog;
    private readonly IAudioEngine _audioEngine;
    private readonly IMidiDeviceService _midiSource;

    // Accumulated bank select state (MIDI spec: CC#0 MSB + CC#32 LSB precede a PC)
    private int _pendingBankMsb;
    private int _pendingBankLsb;

    private bool _disposed;

    public MidiInstrumentSwitcher(
        InstrumentCatalog catalog,
        IAudioEngine audioEngine,
        IMidiDeviceService midiSource)
    {
        _catalog     = catalog     ?? throw new ArgumentNullException(nameof(catalog));
        _audioEngine = audioEngine ?? throw new ArgumentNullException(nameof(audioEngine));
        _midiSource  = midiSource  ?? throw new ArgumentNullException(nameof(midiSource));

        _midiSource.ProgramChangeReceived += OnProgramChangeReceived;
        _midiSource.ControlChangeReceived += OnControlChangeReceived;
    }

    // ── Event handlers (called on the MIDI callback thread) ───────────────────

    private void OnControlChangeReceived(object? sender, MidiControlEventArgs e)
    {
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

        // Try catalog lookup first; fall back to a raw preset change on the current soundfont
        var instrument = _catalog.GetByProgramNumber(program);
        if (instrument is not null)
        {
            _audioEngine.SelectInstrument(instrument);
        }
        else
        {
            // No catalog entry — send bank+program change directly so General MIDI presets work
            // even without a catalog entry
            _audioEngine.SetPreset(e.Channel, bank, program);
        }
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _midiSource.ProgramChangeReceived -= OnProgramChangeReceived;
        _midiSource.ControlChangeReceived -= OnControlChangeReceived;
    }
}
