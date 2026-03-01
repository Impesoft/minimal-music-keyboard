// Minimal interface stubs used by tests.
// These match the contracts specified in docs/architecture.md.
// When production code is ready, tests should reference the production project
// and these stubs can be removed.

namespace MinimalMusicKeyboard.Midi
{
    public enum MidiDeviceStatus { Connected, Disconnected }

    public class MidiNoteEventArgs : EventArgs
    {
        public int Channel { get; init; }
        public int Note { get; init; }
        public int Velocity { get; init; }
    }

    public class MidiControlEventArgs : EventArgs
    {
        public int Channel { get; init; }
        public int Controller { get; init; }
        public int Value { get; init; }
    }

    public class MidiProgramEventArgs : EventArgs
    {
        public int Channel { get; init; }
        public int Program { get; init; }
    }

    /// <summary>
    /// Abstraction over NAudio.Midi.MidiIn — enables mocking in tests.
    /// Production implementation wraps MidiIn directly.
    /// </summary>
    public interface IMidiInput : IDisposable
    {
        void Start();
        void Stop();
        event EventHandler<MidiNoteEventArgs>? NoteReceived;
        event EventHandler<MidiControlEventArgs>? ControlChangeReceived;
        event EventHandler<MidiProgramEventArgs>? ProgramChangeReceived;
    }

    /// <summary>Core MIDI device service contract.</summary>
    public interface IMidiDeviceService : IDisposable
    {
        MidiDeviceStatus Status { get; }
        event EventHandler<MidiNoteEventArgs>? NoteReceived;
        event EventHandler<MidiControlEventArgs>? ControlChangeReceived;
        event EventHandler<MidiProgramEventArgs>? ProgramChangeReceived;
        event EventHandler? DeviceDisconnected;
    }
}

namespace MinimalMusicKeyboard.Audio
{
    public interface IAudioEngine : IDisposable
    {
        void NoteOn(int channel, int key, int velocity);
        void NoteOff(int channel, int key);
        void SetPreset(int channel, int bank, int preset);
        void LoadSoundFont(string path);
        void SelectInstrument(string soundFontPath, int bank, int preset);
    }
}

namespace MinimalMusicKeyboard.Instruments
{
    public sealed class InstrumentDefinition
    {
        public string Name { get; init; } = string.Empty;
        public string SoundFontPath { get; init; } = string.Empty;
        public int Bank { get; init; }
        public int Preset { get; init; }
        public int ProgramChangeNumber { get; init; }
    }

    public interface IInstrumentCatalog
    {
        IReadOnlyList<InstrumentDefinition> GetAll();
        InstrumentDefinition? GetByProgramChange(int programNumber);
        InstrumentDefinition? GetByName(string name);
        InstrumentDefinition? CurrentInstrument { get; }
    }
}
