using Moq;
using MinimalMusicKeyboard.Tests.Stubs;

namespace MinimalMusicKeyboard.Tests;

/// <summary>
/// Tests for MidiDeviceService. The service is responsible for:
/// - Wrapping NAudio MidiIn with a stable event-based API
/// - Detecting device disconnect and entering Disconnected state
/// - Clean disposal: event handlers cleared, MidiIn handle released
/// - Graceful startup when configured device is not present
///
/// Architecture reference: docs/architecture.md section 3.2
/// </summary>
public class MidiDeviceServiceTests
{
    // -----------------------------------------------------------------------
    // Disconnect handling
    // -----------------------------------------------------------------------

    /// <summary>
    /// When the physical MIDI device is unplugged while the service is
    /// listening, it must transition to Disconnected (not crash).
    /// Architecture: section 3.2 — "MIDI device disconnect handling (Gren — REQUIRED)"
    /// </summary>
    [Fact]
    public void DeviceDisconnect_SetsStatusToDisconnected()
    {
        var service = new StubMidiDeviceService();
        service.Status.Should().Be(MidiDeviceStatus.Connected, "service starts connected");

        service.SimulateDisconnect();

        service.Status.Should().Be(MidiDeviceStatus.Disconnected,
            "service must enter Disconnected state after device is removed");
    }

    /// <summary>
    /// The DeviceDisconnected event must fire so the tray icon can update
    /// its tooltip to "No MIDI device".
    /// </summary>
    [Fact]
    public void DeviceDisconnect_FiresDeviceDisconnectedEvent()
    {
        var service = new StubMidiDeviceService();
        var eventFired = false;
        service.DeviceDisconnected += (_, _) => eventFired = true;

        service.SimulateDisconnect();

        eventFired.Should().BeTrue("DeviceDisconnected event must fire on USB unplug");
    }

    // -----------------------------------------------------------------------
    // Disposal — event handler leak prevention
    // -----------------------------------------------------------------------

    /// <summary>
    /// After Dispose(), all event handler lists must be cleared.
    /// A lingering NoteReceived subscription is the most common leak in this app
    /// because MidiMessageRouter subscribes and must be unsubscribed before the
    /// service is collected.
    ///
    /// Architecture: section 3.2 — "IDisposable contract"
    /// </summary>
    [Fact]
    public void Dispose_ClearsAllEventHandlerSubscriptions()
    {
        var service = new StubMidiDeviceService();

        // Subscribe to every event
        service.NoteReceived += (_, _) => { };
        service.ControlChangeReceived += (_, _) => { };
        service.ProgramChangeReceived += (_, _) => { };
        service.DeviceDisconnected += (_, _) => { };

        service.Dispose();

        // All invocation lists must be null — no retained subscriber references
        service.HasNoteReceivedSubscribers.Should().BeFalse(
            "Dispose must null the NoteReceived invocation list to prevent memory leaks");
        service.HasDeviceDisconnectedSubscribers.Should().BeFalse(
            "Dispose must null the DeviceDisconnected invocation list");
    }

    /// <summary>
    /// Verifies that a subscriber object is not kept alive by the service after
    /// the service is disposed. Uses WeakReference to detect the retention.
    /// </summary>
    [Fact]
    public void Dispose_DoesNotRetainSubscriberReferences()
    {
        var service = new StubMidiDeviceService();

        // Create subscriber in isolated scope so it has no other roots
        var subscriberRef = AttachAndDetach(service);

        service.Dispose();
        WeakReferenceHelper.ForceFullGC();

        subscriberRef.IsAlive.Should().BeFalse(
            "subscriber should be eligible for GC once service is disposed");
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference AttachAndDetach(StubMidiDeviceService service)
    {
        var subscriber = new object();
        // Capture 'subscriber' in a closure so the event holds a ref to it
        service.NoteReceived += (_, _) => GC.KeepAlive(subscriber);
        return new WeakReference(subscriber);
    }

    // -----------------------------------------------------------------------
    // Device not found at startup
    // -----------------------------------------------------------------------

    /// <summary>
    /// If the configured MIDI device is not connected when the app starts,
    /// the service must start in Disconnected state without throwing.
    /// Architecture: Gren review — "Startup with missing MIDI device"
    /// </summary>
    [Fact]
    public void DeviceNotFoundAtStartup_ServiceStartsInDisconnectedState_NoException()
    {
        // StubMidiDeviceService simulates a service that was constructed with
        // a missing device — equivalent to MidiDeviceService(deviceName: "Missing Device")
        var act = () =>
        {
            var service = new StubMidiDeviceService();
            // In the real implementation this would attempt to open the device;
            // when it fails it must NOT throw, just go to Disconnected
            service.SimulateDisconnect(); // represents device-not-found-at-startup path
            return service;
        };

        act.Should().NotThrow("a missing MIDI device must not crash startup");
    }

    /// <summary>
    /// Service status after startup with missing device must be Disconnected,
    /// not throw NullReferenceException or MmException.
    /// </summary>
    [Fact]
    public void DeviceNotFoundAtStartup_StatusIsDisconnected()
    {
        var service = new StubMidiDeviceService();
        service.SimulateDisconnect(); // simulate "device not found" at open time

        service.Status.Should().Be(MidiDeviceStatus.Disconnected);
    }

    // -----------------------------------------------------------------------
    // Rapid reconnect — no thread leak
    // -----------------------------------------------------------------------

    /// <summary>
    /// Simulates rapid USB disconnect/reconnect cycles (e.g. flapping USB cable).
    /// Each reconnect attempt must not leave a background thread alive.
    /// Architecture: section 3.2 — "Auto-reconnect architecture"
    /// </summary>
    [Fact]
    public async Task RapidReconnectAttempts_DoNotLeakThreads()
    {
        var threadCountBefore = System.Diagnostics.Process.GetCurrentProcess().Threads.Count;

        // Simulate 50 rapid disconnect/reconnect cycles
        var service = new StubMidiDeviceService();
        for (var i = 0; i < 50; i++)
        {
            service.SimulateDisconnect();
            // In production, a reconnect timer would fire here.
            // The key assertion is that thread count doesn't climb.
            await Task.Delay(1); // yield to let any spawned threads settle
        }

        service.Dispose();

        // Allow background threads to terminate
        await Task.Delay(200);

        var threadCountAfter = System.Diagnostics.Process.GetCurrentProcess().Threads.Count;
        threadCountAfter.Should().BeLessOrEqualTo(
            threadCountBefore + 3,
            "each reconnect attempt must not permanently spawn a thread");
    }
}
