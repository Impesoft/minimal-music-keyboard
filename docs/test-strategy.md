# Minimal Music Keyboard — Test Strategy

**Author:** Ed (QA)  
**Date:** 2026-03-01  
**Status:** Approved  
**Scope:** Unit tests, integration tests, long-running stability plan

---

## 1. Test Pyramid

```
        ┌─────────────────────┐
        │    Manual / E2E     │  (5%)
        │  tray smoke, MIDI   │
        │  device live tests  │
        └─────────────────────┘
       ┌───────────────────────┐
       │   Integration Tests   │  (20%)
       │  AppLifecycle wiring  │
       │  MidiRouter ↔ Audio   │
       │  settings persistence │
       └───────────────────────┘
     ┌─────────────────────────────┐
     │       Unit Tests            │  (75%)
     │  per-component, fast, mocked│
     │  concurrency, disposal,     │
     │  catalog parsing, edge cases│
     └─────────────────────────────┘
```

### Rationale
This is a background tray app with hardware I/O (MIDI, WASAPI). True end-to-end tests require physical hardware and are not automatable on CI. The unit layer must therefore cover the correctness and stability guarantees that E2E cannot easily reach — especially threading, disposal, and long-running degradation.

---

## 2. Component Coverage Map

| Component | Test Class | Priority | Key Risk |
|-----------|-----------|----------|---------|
| `MidiDeviceService` | `MidiDeviceServiceTests` | **Critical** | USB disconnect crashes, event handler leak |
| `AudioEngine` | `AudioEngineTests` | **Critical** | Concurrent NoteOn vs Render deadlock, soundfont leak |
| `InstrumentCatalog` | `InstrumentCatalogTests` | High | Corrupt JSON, missing file, null returns |
| `InstrumentSwitcher` | `InstrumentSwitcherTests` (future) | High | Rapid switch during note, preset state corruption |
| `AppLifecycleManager` | `AppLifecycleTests` (integration) | High | Disposal order, partial-shutdown resilience |
| `SettingsWindow` | `SettingsWindowTests` (integration) | Medium | Event handler leak on close, COM reference |
| `TrayIconService` | Manual | Low | Ghost icon (Windows shell limitation) |
| `MidiMessageRouter` | `MidiMessageRouterTests` (future) | Medium | Bank select accumulation, PC routing |

---

## 3. Test Tooling

| Tool | Purpose | Version |
|------|---------|---------|
| **xUnit 2.7** | Test runner and assertion primitives | `2.7.*` |
| **Moq 4.20** | Mocking interfaces (IMidiInput, IAudioEngine, IInstrumentCatalog) | `4.20.*` |
| **FluentAssertions 6.12** | Readable assertion messages (`status.Should().Be(...)`) | `6.12.*` |
| **Microsoft.NET.Test.Sdk** | VS/CI test discovery | `17.9.*` |

**Not used:** Microsoft Fakes, NSubstitute (Moq is sufficient and familiar).  
**Memory profiling (off-CI):** JetBrains dotMemory for snapshot diffs; WinDbg `!dumpheap -stat` for Gen2 analysis. See Section 5.

---

## 4. Memory Leak Detection Approach

### 4.1 WeakReference Pattern (In-Process, CI-Safe)

For every `IDisposable`, write a disposal verification test using the WeakReference trick:

```csharp
[MethodImpl(MethodImplOptions.NoInlining)]
private static WeakReference CreateAndDispose<T>(Func<T> factory) where T : IDisposable
{
    var instance = factory();
    var weakRef = new WeakReference(instance);
    instance.Dispose();
    return weakRef;
}

[Fact]
public void Component_AfterDispose_IsCollectedByGC()
{
    var weakRef = CreateAndDispose(() => new SomeComponent());
    GC.Collect(2, GCCollectionMode.Forced, blocking: true);
    GC.WaitForPendingFinalizers();
    GC.Collect(2, GCCollectionMode.Forced, blocking: true);
    weakRef.IsAlive.Should().BeFalse("component should be GC-collected after Dispose");
}
```

**Critical:** The factory method MUST be `[MethodImpl(MethodImplOptions.NoInlining)]` to ensure the stack frame is fully popped before `GC.Collect` runs. Without this, the JIT may keep a local root alive, causing a false positive `IsAlive == true`.

### 4.2 Event Handler Leak Detection (Spy Pattern)

Event handler leaks are the most common slow leak in this app (SettingsWindow subscribing to long-lived services). Verify unsubscription by counting delegate invocation list length:

```csharp
[Fact]
public void Service_AfterDispose_EventHandlerListIsEmpty()
{
    var service = new StubMidiDeviceService();
    var spy = new object(); // subscriber target
    service.NoteReceived += (_, _) => _ = spy;
    
    service.Dispose();
    
    // Verify via reflection or expose handler count in test mode
    service.HasNoteReceivedSubscribers.Should().BeFalse();
}
```

### 4.3 dotMemory Snapshots (Manual, Off-CI)

For long-running stability validation:

1. Attach dotMemory to the running app
2. Take baseline snapshot after 5-minute warmup
3. Run rapid instrument switching loop for 30 minutes (MIDI PC messages, ~1/sec)
4. Take comparison snapshot
5. Assert: **Gen2 heap size delta < 1MB**; no `MeltySynth.Synthesizer` or `NAudio.Midi.MidiIn` instances surviving in Gen2

### 4.4 WinDbg !dumpheap (Deep Analysis)

When a leak is suspected but not yet localized:

```
!dumpheap -stat              # Find top types by count
!dumpheap -type MidiIn       # Find all MidiIn instances
!gcroot <address>            # Find what's holding the reference
```

Look specifically for:
- `NAudio.Midi.MidiIn` instances — should be 0 after disposal
- `MeltySynth.SoundFont` byte arrays — should be 0 after soundfont switch + GC
- `System.EventHandler` delegates rooted through long-lived services

---

## 5. Long-Running Stability Test Plan

### Goal
App runs in tray for 1+ hours under load without measurable memory growth or audio degradation.

### Automated Endurance Test (CI-unsafe, manual execution)

```
Duration:    60 minutes
Instrument switches: 1 per 2 seconds (via simulated PC messages), 1800 total switches
Chord bursts: 6-note chords every 500ms on MIDI callback thread
Measurement interval: Every 5 minutes — record GC Gen2 size, thread count
```

**Pass criteria:**
- Gen2 heap stays flat (±2MB tolerance for background CLR activity)
- Thread count stays flat (no thread leak from reconnect logic)
- No unhandled exceptions logged
- Audio output is continuous (WasapiOut not stopped/restarted unexpectedly)

### GC Gen2 Flatness Test (Automated, in-process)

```csharp
[Fact]
public async Task RapidInstrumentSwitching_1000Switches_Gen2StaysFlat()
{
    var gen2Before = GC.CollectionCount(2);
    var baselineBytes = GC.GetTotalMemory(forceFullCollection: true);

    for (int i = 0; i < 1000; i++)
    {
        await engine.SelectInstrumentAsync(...);
    }

    GC.Collect(2, GCCollectionMode.Forced, blocking: true);
    var finalBytes = GC.GetTotalMemory(false);
    
    (finalBytes - baselineBytes).Should().BeLessThan(5 * 1024 * 1024, 
        "1000 instrument switches should not grow Gen2 by more than 5MB");
}
```

---

## 6. Critical Edge Cases Per Component

### MidiDeviceService

| Edge Case | Expected Behaviour | Test |
|-----------|-------------------|------|
| USB MIDI device disconnected while listening | Catch `MmException`/`InvalidOperationException`, enter `Disconnected` state, fire `DeviceDisconnected` event, dispose stale `MidiIn` handle | `DeviceDisconnect_SetsStatusToDisconnected` |
| Device not present at startup | Service starts in `Disconnected` state, no exception | `DeviceNotFoundAtStartup_DoesNotThrow` |
| Rapid disconnect/reconnect (USB flapping) | No thread leak; event handlers remain stable | `RapidReconnectAttempts_NoThreadLeak` |
| Dispose while actively receiving MIDI events | No exception; MIDI callback thread terminates cleanly | `Dispose_WhileReceivingEvents_IsClean` (future) |
| Two simultaneous `Connect` calls | Second call is ignored or serialized — no double-open | (future) |

### AudioEngine

| Edge Case | Expected Behaviour | Test |
|-----------|-------------------|------|
| NoteOn from 20 threads simultaneously | No deadlock; all notes accepted | `NoteOn_ConcurrentThreads_NoDeadlock` |
| `SelectInstrument` while notes are playing | All playing notes silenced via NoteOffAll, then preset changes | `SelectInstrument_WhileNotesPlaying_NoException` |
| SoundFont file missing | Graceful handling; no crash; engine in degraded state | `LoadSoundFont_MissingFile_HandledGracefully` |
| SoundFont switch while audio render is running | Old Synthesizer stays alive until render completes (Volatile.Read pattern) | `SoundFontSwap_AudioThreadSafe` (future) |
| Dispose called twice | Second Dispose is a no-op; no exception | (future) |

### InstrumentCatalog

| Edge Case | Expected Behaviour | Test |
|-----------|-------------------|------|
| Settings file missing | Default catalog written to disk and returned | `MissingSettingsFile_WritesDefaultsAndReturns` |
| Corrupt/invalid JSON | Fallback to defaults; no crash; error logged | `CorruptedJson_FallsBackToDefaults` |
| `GetByName` with unknown name | Returns `null` | `GetByName_UnknownId_ReturnsNull` |
| `GetAll` on fresh catalog | Returns Grand Piano and Electric Piano defaults | `GetAll_ReturnsExpectedDefaults` |
| PC number collision (two instruments same PC number) | First match wins; no crash | (future) |

### Disposal Chain (AppLifecycleManager)

| Edge Case | Expected Behaviour | Test |
|-----------|-------------------|------|
| MidiDeviceService.Dispose() throws | Disposal continues to AudioEngine etc.; exception logged | `DisposalChain_MidiThrows_ContinuesToAudio` (future) |
| AudioEngine.Dispose() called before MidiDeviceService | MIDI events can still fire; must be caught gracefully | Prevented by disposal order in architecture |
| Full lifecycle round-trip | All WeakReferences dead after Dispose() + GC.Collect | `FullLifecycle_AllRefsDeadAfterDispose` |

---

## 7. Concurrency Test Patterns

### Pattern: N concurrent callers via Task.WhenAll
```csharp
var tasks = Enumerable.Range(0, 20)
    .Select(_ => Task.Run(() => engine.NoteOn(1, 60, 100)));
var act = () => Task.WhenAll(tasks);
await act.Should().CompleteWithinAsync(TimeSpan.FromSeconds(5));
```

### Pattern: Verify no thread leak after dispose
```csharp
var threadsBefore = Process.GetCurrentProcess().Threads.Count;
// ... create service, do work, dispose
await Task.Delay(500); // give threads time to exit
var threadsAfter = Process.GetCurrentProcess().Threads.Count;
threadsAfter.Should().BeLessOrEqualTo(threadsBefore + 2);
```

---

## 8. CI Integration

- All unit tests in `MinimalMusicKeyboard.Tests` run on every PR
- Tests marked `[Trait("Category", "Stability")]` are excluded from PR builds (run nightly)
- Tests marked `[Trait("Category", "Hardware")]` are excluded from CI entirely (require MIDI device)
- Minimum coverage gate: **80% line coverage** on `Midi/`, `Audio/`, `Instruments/` namespaces
