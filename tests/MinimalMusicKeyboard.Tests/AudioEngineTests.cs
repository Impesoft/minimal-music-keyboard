using MinimalMusicKeyboard.Tests.Stubs;

namespace MinimalMusicKeyboard.Tests;

/// <summary>
/// Tests for AudioEngine. Covers concurrency safety, graceful degradation,
/// and disposal correctness.
///
/// Critical risks (per architecture):
/// - MeltySynth NoteOn vs Render may require a lock (thread safety unverified)
/// - Synthesizer swap pattern requires Volatile.Read/Write
/// - SoundFont byte arrays must be null'd on Dispose for GC eligibility
///
/// Architecture reference: docs/architecture.md sections 3.3, 5, 6
/// </summary>
public class AudioEngineTests
{
    // -----------------------------------------------------------------------
    // Concurrency — NoteOn from multiple threads
    // -----------------------------------------------------------------------

    /// <summary>
    /// NoteOn is called from the MIDI callback thread while the audio render
    /// thread is calling Render(). 20 concurrent callers must not deadlock.
    ///
    /// Architecture: section 5 — "MIDI thread → AudioEngine: verify thread safety"
    /// If MeltySynth is not thread-safe, production code must add a SpinLock.
    /// This test will surface a deadlock by timing out.
    /// </summary>
    [Fact]
    public async Task NoteOn_From20ConcurrentThreads_DoesNotDeadlock()
    {
        var engine = new StubAudioEngine();

        var tasks = Enumerable.Range(0, 20)
            .Select(i => Task.Run(() => engine.NoteOn(1, 60 + (i % 12), 100)));

        var act = () => Task.WhenAll(tasks);

        await act.Should().CompleteWithinAsync(5.Seconds(),
            "NoteOn must not deadlock when called from 20 concurrent threads");
    }

    [Fact]
    public async Task NoteOn_ConcurrentCallsWithNoteOff_DoesNotDeadlock()
    {
        var engine = new StubAudioEngine();

        var notesOn = Enumerable.Range(0, 10)
            .Select(i => Task.Run(() => engine.NoteOn(1, 60 + i, 100)));
        var notesOff = Enumerable.Range(0, 10)
            .Select(i => Task.Run(() => engine.NoteOff(1, 60 + i)));

        var act = () => Task.WhenAll(notesOn.Concat(notesOff));

        await act.Should().CompleteWithinAsync(5.Seconds(),
            "interleaved NoteOn and NoteOff from concurrent threads must not deadlock");
    }

    [Fact]
    public async Task NoteOn_AllCallsComplete_CountMatchesExpected()
    {
        var engine = new StubAudioEngine();
        const int callCount = 20;

        await Task.WhenAll(Enumerable.Range(0, callCount)
            .Select(_ => Task.Run(() => engine.NoteOn(1, 60, 100))));

        engine.NoteOnCallCount.Should().Be(callCount,
            "all concurrent NoteOn calls must be processed without losing any");
    }

    // -----------------------------------------------------------------------
    // Instrument selection while notes are playing
    // -----------------------------------------------------------------------

    /// <summary>
    /// SelectInstrument is called from the MIDI callback (program change message)
    /// while the audio thread may be mid-render and notes are active.
    /// Must not throw; the architecture requires NoteOffAll before preset change.
    ///
    /// Architecture: section 5 — "MIDI thread → InstrumentSwitcher → AudioEngine.SetPreset"
    /// </summary>
    [Fact]
    public void SelectInstrument_WhileNotesAreActive_DoesNotThrow()
    {
        var engine = new StubAudioEngine();

        // Simulate notes held down
        engine.NoteOn(1, 60, 100);
        engine.NoteOn(1, 64, 100);
        engine.NoteOn(1, 67, 100);

        var act = () => engine.SelectInstrument(@"soundfonts\GeneralUser.sf2", bank: 0, preset: 4);

        act.Should().NotThrow(
            "instrument selection must never throw even when notes are currently playing");
    }

    [Fact]
    public void SelectInstrument_CalledRepeatedly_DoesNotThrow()
    {
        var engine = new StubAudioEngine();

        var act = () =>
        {
            for (var i = 0; i < 100; i++)
                engine.SelectInstrument(@"soundfonts\GeneralUser.sf2", bank: 0, preset: i % 128);
        };

        act.Should().NotThrow("rapid repeated instrument switches must not crash");
    }

    // -----------------------------------------------------------------------
    // Missing soundfont file — graceful degradation
    // -----------------------------------------------------------------------

    /// <summary>
    /// If the soundfont path in settings points to a file that no longer exists
    /// (moved, deleted, USB drive unplugged), AudioEngine must not crash.
    /// Architecture: Gren review — "SoundFont switching across different SF2 files"
    /// </summary>
    [Fact]
    public void LoadSoundFont_MissingFile_ThrowsFileNotFoundException()
    {
        var engine = new StubAudioEngine();
        var missingPath = Path.Combine(Path.GetTempPath(), $"nonexistent_{Guid.NewGuid():N}.sf2");

        // Production code must catch FileNotFoundException internally.
        // StubAudioEngine throws it to surface the failure mode for the test.
        var act = () => engine.LoadSoundFont(missingPath);

        act.Should().Throw<FileNotFoundException>(
            "attempting to load a nonexistent soundfont must surface as FileNotFoundException — " +
            "production AudioEngine must catch this and enter a degraded (no-sound) state");
    }

    [Fact]
    public void LoadSoundFont_MissingFile_EngineRemainsUsable()
    {
        // After a failed soundfont load, the engine must still respond to
        // NoteOn/NoteOff without crashing (old soundfont remains active).
        var engine = new StubAudioEngine();
        var missingPath = Path.Combine(Path.GetTempPath(), "missing.sf2");

        try { engine.LoadSoundFont(missingPath); }
        catch (FileNotFoundException) { /* expected */ }

        var act = () =>
        {
            engine.NoteOn(1, 60, 100);
            engine.NoteOff(1, 60);
        };

        act.Should().NotThrow(
            "engine must remain functional after a failed soundfont load");
    }

    // -----------------------------------------------------------------------
    // Dispose — no lingering threads, soundfont buffer released
    // -----------------------------------------------------------------------

    /// <summary>
    /// After Dispose(), the audio render thread must have stopped within 500ms.
    /// Thread leak here would cause Gen2 growth over long runs.
    ///
    /// Architecture: section 6 — "WasapiOut.Stop() + WasapiOut.Dispose() → audio render thread terminates"
    /// </summary>
    [Fact]
    public async Task Dispose_AudioThreadTerminates_Within500ms()
    {
        var threadsBefore = System.Diagnostics.Process.GetCurrentProcess().Threads.Count;

        var engine = new StubAudioEngine();
        engine.NoteOn(1, 60, 100); // simulate active playback

        engine.Dispose();

        await Task.Delay(500); // give render thread time to exit

        var threadsAfter = System.Diagnostics.Process.GetCurrentProcess().Threads.Count;
        threadsAfter.Should().BeLessOrEqualTo(threadsBefore + 2,
            "no additional threads should persist after AudioEngine is disposed");
    }

    [Fact]
    public void Dispose_MarksEngineAsDisposed()
    {
        var engine = new StubAudioEngine();

        engine.Dispose();

        engine.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public void NoteOn_AfterDispose_ThrowsObjectDisposedException()
    {
        var engine = new StubAudioEngine();
        engine.Dispose();

        var act = () => engine.NoteOn(1, 60, 100);

        act.Should().Throw<ObjectDisposedException>(
            "all methods must throw ObjectDisposedException after Dispose");
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var engine = new StubAudioEngine();
        engine.Dispose();

        var act = () => engine.Dispose();

        act.Should().NotThrow("double Dispose must be a safe no-op");
    }
}
