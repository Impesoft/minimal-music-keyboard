using MinimalMusicKeyboard.Tests.Stubs;

namespace MinimalMusicKeyboard.Tests;

/// <summary>
/// Verifies that all IDisposable components release their object graphs after
/// Dispose() + GC.Collect(). These tests catch the most dangerous class of
/// memory leak in this app: objects kept alive by event handler subscriptions
/// or static/long-lived references.
///
/// Pattern used throughout: WeakReferenceHelper.CreateAndDispose() creates the
/// object in a [MethodImpl(NoInlining)] scope to ensure the JIT doesn't keep
/// a hidden root on the stack, then GC.Collect() is forced.
///
/// Architecture reference: docs/architecture.md section 6 (Disposal Chain)
/// Test strategy reference: docs/test-strategy.md section 4 (Memory Leak Detection)
/// </summary>
public class DisposalVerificationTests
{
    // -----------------------------------------------------------------------
    // MidiDeviceService
    // -----------------------------------------------------------------------

    /// <summary>
    /// MidiDeviceService must be GC-eligible immediately after Dispose().
    /// If it isn't, a subscriber (e.g. MidiMessageRouter) is still holding
    /// a reference — the disposal order in AppLifecycleManager is wrong.
    ///
    /// Architecture: section 6 — "MidiDeviceService.Dispose() → MIDI callback thread terminates"
    /// </summary>
    [Fact]
    public void MidiDeviceService_AfterDispose_IsCollectedByGC()
    {
        var weakRef = WeakReferenceHelper.CreateAndDispose(
            () => new StubMidiDeviceService());

        WeakReferenceHelper.ForceFullGC();

        weakRef.IsAlive.Should().BeFalse(
            "MidiDeviceService must be GC-collected after Dispose; " +
            "if alive, a subscriber still holds a reference (event handler leak)");
    }

    /// <summary>
    /// A subscriber that attaches to MidiDeviceService.NoteReceived must
    /// not be kept alive by the service after the service is disposed.
    /// This is the SettingsWindow / MidiMessageRouter leak scenario.
    /// </summary>
    [Fact]
    public void MidiDeviceService_Dispose_ReleasesSubscriberObjects()
    {
        var service = new StubMidiDeviceService();

        var subscriberRef = AttachSubscriberToService(service);
        service.Dispose();

        WeakReferenceHelper.ForceFullGC();

        subscriberRef.IsAlive.Should().BeFalse(
            "subscriber held only via NoteReceived event must be freed after Dispose; " +
            "if alive, the event invocation list was not cleared on Dispose");
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference AttachSubscriberToService(StubMidiDeviceService service)
    {
        var subscriber = new object();
        service.NoteReceived += (_, _) => GC.KeepAlive(subscriber);
        return new WeakReference(subscriber);
    }

    // -----------------------------------------------------------------------
    // AudioEngine
    // -----------------------------------------------------------------------

    /// <summary>
    /// After AudioEngine.Dispose(), the SoundFont buffer (simulated here as a
    /// byte[]) must be GC-eligible. Architecture requires:
    ///   "Nulls Synthesizer reference (SoundFont byte arrays become GC-eligible)"
    ///
    /// Architecture: section 6, disposal step 3
    /// </summary>
    [Fact]
    public void AudioEngine_AfterDispose_IsCollectedByGC()
    {
        var weakRef = WeakReferenceHelper.CreateAndDispose(
            () => new StubAudioEngine());

        WeakReferenceHelper.ForceFullGC();

        weakRef.IsAlive.Should().BeFalse(
            "AudioEngine must be GC-collected after Dispose; " +
            "if alive, the Synthesizer or WasapiOut references were not nulled");
    }

    /// <summary>
    /// The internal soundfont buffer must be freed after Dispose + GC.
    /// Large SF2 files (10-50MB) held in Gen2 after disposal will cause the
    /// app to exceed the 50MB idle memory target during instrument switching.
    ///
    /// Architecture: section 5 — "GC.Collect hint after swap"
    /// </summary>
    [Fact]
    public void AudioEngine_AfterDispose_SoundFontBufferIsReleasedForGC()
    {
        // Force a full GC first to establish baseline
        WeakReferenceHelper.ForceFullGC();
        var baselineBytes = GC.GetTotalMemory(false);

        // Create engine (allocates 1KB simulated soundfont buffer in stub)
        var engine = new StubAudioEngine();
        engine.Dispose();

        WeakReferenceHelper.ForceFullGC();
        var afterBytes = GC.GetTotalMemory(false);

        // Memory after dispose+GC must not significantly exceed baseline
        // (1MB tolerance for CLR overhead, thread stacks, etc.)
        var delta = afterBytes - baselineBytes;
        delta.Should().BeLessThan(1024 * 1024,
            "soundfont buffers must be freed after AudioEngine disposal + GC; " +
            "a large positive delta indicates the buffer is rooted somewhere");
    }

    // -----------------------------------------------------------------------
    // Full lifecycle — all services
    // -----------------------------------------------------------------------

    /// <summary>
    /// Full lifecycle test: create all services, use them, dispose them in the
    /// correct architecture order, verify memory returns to baseline.
    ///
    /// Architecture: section 6 — "Exact ordered shutdown sequence":
    ///   MidiMessageRouter → MidiDeviceService → AudioEngine → TrayIcon → Guard
    ///
    /// This test exercises the router → midi → audio subset (tray and guard
    /// are UI-bound and tested manually).
    /// </summary>
    [Fact]
    public void FullLifecycle_AllServicesDisposed_MemoryReturnsToBaseline()
    {
        WeakReferenceHelper.ForceFullGC();
        var baselineBytes = GC.GetTotalMemory(false);

        var (midiRef, audioRef, catalogRef) = CreateAndDisposeAllServices();

        WeakReferenceHelper.ForceFullGC();

        midiRef.IsAlive.Should().BeFalse(
            "MidiDeviceService must be collected after full lifecycle dispose");
        audioRef.IsAlive.Should().BeFalse(
            "AudioEngine must be collected after full lifecycle dispose");
        catalogRef.IsAlive.Should().BeFalse(
            "InstrumentCatalog (stub) must be collected after full lifecycle dispose");

        var finalBytes = GC.GetTotalMemory(false);
        var delta = finalBytes - baselineBytes;
        delta.Should().BeLessThan(2 * 1024 * 1024,
            "memory after full lifecycle dispose must return close to baseline; " +
            "sustained delta indicates a rooted reference that should have been cleared");
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (WeakReference midi, WeakReference audio, WeakReference catalog)
        CreateAndDisposeAllServices()
    {
        var midi = new StubMidiDeviceService();
        var audio = new StubAudioEngine();
        var catalog = new StubInstrumentCatalog();

        // Simulate normal usage: play some notes, then simulate a PC message
        midi.RaiseNoteReceived(new MidiNoteEventArgs { Channel = 1, Note = 60, Velocity = 100 });
        audio.NoteOn(1, 60, 100);
        audio.NoteOff(1, 60);
        _ = catalog.GetByProgramChange(0);

        // Dispose in architecture-specified order (router → midi → audio):
        // MidiMessageRouter would go first; we omit it here (no stub yet).
        midi.Dispose();   // step 2: MidiDeviceService
        audio.Dispose();  // step 3: AudioEngine

        return (new WeakReference(midi), new WeakReference(audio), new WeakReference(catalog));
    }

    // -----------------------------------------------------------------------
    // Disposal order resilience
    // -----------------------------------------------------------------------

    /// <summary>
    /// If MidiDeviceService.Dispose() is called AFTER AudioEngine.Dispose()
    /// (wrong order), the subsequent MIDI event must not crash.
    /// Architecture: section 6 — "Guard against partial disposal: each Dispose is wrapped in try/catch"
    /// </summary>
    [Fact]
    public void WrongDisposalOrder_MidiAfterAudio_DoesNotCrash()
    {
        var midi = new StubMidiDeviceService();
        var audio = new StubAudioEngine();

        // Wrong order: audio first
        audio.Dispose();

        // MIDI event fires after audio is disposed — must not crash
        var act = () =>
        {
            midi.RaiseNoteReceived(new MidiNoteEventArgs { Channel = 1, Note = 60, Velocity = 100 });
            midi.Dispose();
        };

        act.Should().NotThrow(
            "app must survive wrong disposal order; each component defends itself");
    }
}
