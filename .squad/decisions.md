# Team Decisions

_Append-only. Managed by Scribe. Agents write to .squad/decisions/inbox/ — Scribe merges here._

<!-- Entries appended below in reverse-chronological order -->

## Session: 2026-03-01 — Self-Contained Windows App Runtime

### Jet: Self-Contained Windows App Runtime Deployment
# Decision: Self-Contained Windows App Runtime Deployment

**Author:** Jet (Windows Dev)
**Date:** 2026-03-01
**Requested by:** Ward Impe
**Status:** Merged by Scribe

---

## Decision

Added two properties to `MinimalMusicKeyboard.csproj` `<PropertyGroup>`:

```xml
<!-- Bundle Windows App Runtime into output — no external installer required -->
<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>
<SelfContained>true</SelfContained>
```

---

## Context

Without these flags, the app requires the **Windows App Runtime** to be installed separately on the user's machine (via the bootstrapper or a system-wide MSIX). This is an external dependency Ward explicitly wants eliminated.

---

## Rationale

- `WindowsAppSDKSelfContained=true` — bundles the Windows App Runtime DLLs into the app's output directory. Users run the app directly without any prior installation.
- `SelfContained=true` — required prerequisite for `WindowsAppSDKSelfContained`. Instructs the .NET SDK to include runtime dependencies in the output.
- The `Microsoft.WindowsAppSDK` NuGet package reference is **kept** — it provides the build-time MSBuild targets, XAML tooling, and headers. It does not represent an installer dependency; only the presence of Windows App Runtime on the OS does.

---

## Trade-offs

| Aspect | Impact |
|--------|--------|
| Publish output size | Larger — Windows App Runtime DLLs bundled (~50–100 MB additional) |
| User experience | Better — zero prerequisites, xcopy-deployable |
| NuGet reference | Unchanged — still required for build-time tooling |
| Memory at runtime | No change — same DLLs loaded, just from app directory instead of system |

---

## Build Verification

Build verified clean after change:
- Tool: MSBuild from VS 18 Insiders
- Configuration: Debug, x64
- Result: **0 errors**, output `MinimalMusicKeyboard.dll` produced successfully
- NETSDK1057 (preview SDK notice) is informational only

---

## Session: 2026-03-01 — Architecture & Scaffold

### Ed: Test Strategy (xUnit pyramid, 37 tests)
# Ed — Test Strategy Decisions

**Author:** Ed (QA)  
**Date:** 2026-03-01  
**Status:** Ready for Scribe merge  

---

## Decision: Test tooling

**xUnit 2.7 + Moq 4.20 + FluentAssertions 6.12**

Rationale: xUnit is the idiomatic choice for .NET 8 (used by ASP.NET Core, Windows App SDK samples). Moq is sufficient for our small interface surface; no Fakes or NSubstitute needed. FluentAssertions improves failure messages significantly for nullable/collection assertions.

---

## Decision: Interface-first test project (no production project reference yet)

Test project defines minimal interface stubs (`IMidiDeviceService`, `IAudioEngine`, `IInstrumentCatalog`, `IMidiInput`) under the production namespaces (`MinimalMusicKeyboard.Midi`, etc.). Tests compile and run against test doubles (stubs) today. When production code ships, a `<ProjectReference>` replaces the stubs — no test rewrites needed.

---

## Decision: WeakReference pattern for all IDisposable disposal tests

Every `IDisposable` component gets a disposal verification test using the WeakReference trick:

1. Create the instance in a `[MethodImpl(NoInlining)]` helper (prevents JIT rooting)
2. Dispose
3. `GC.Collect(2, Forced, blocking)` × 2 with `WaitForPendingFinalizers` between
4. Assert `weakRef.IsAlive == false`

This catches the most dangerous leaks (event handler subscriptions, Synthesizer byte arrays, COM references on WinUI3 windows) without requiring dotMemory or WinDbg.

**`[NoInlining]` is mandatory** — without it, the JIT may keep a register root alive and the test produces a false-negative (object appears to not leak when it does).

---

## Decision: Event handler leak is the primary memory risk

`MidiMessageRouter`, `SettingsWindow`, and `InstrumentSwitcher` all subscribe to `MidiDeviceService` and `AudioEngine` events. If `Dispose()` does not null the invocation list, the subscriber objects are kept alive indefinitely.

Each `IDisposable.Dispose()` implementation MUST null all event backing fields. Tests verify this via the `HasNoteReceivedSubscribers` helper on stubs — no reflection required.

---

## Decision: InstrumentCatalog tests use real temp files

File-system edge cases (missing file, corrupted JSON) are tested with real temp directories rather than `IFileSystem` mocks. Reasons:
- `CatalogLoader` is small and its JSON parsing is a real correctness concern
- Mocking `IFileSystem` would test our mock, not the actual JSON handling
- Temp directory cleanup is trivial in `IDisposable` test class

---

## Decision: Long-running stability tests are CI-excluded

Tests tagged `[Trait("Category", "Stability")]` (1h run, Gen2 flatness, thread count) are excluded from PR CI gates and run on a nightly schedule. Hardware tests (`[Trait("Category", "Hardware")]`) require a physical MIDI device and are never run in CI.

Minimum PR coverage gate: **80% line coverage** on `Midi/`, `Audio/`, `Instruments/` namespaces.

---

## Decision: dotMemory + WinDbg for off-CI leak hunting

When in-process WeakReference tests pass but users report memory growth after hours:
1. **dotMemory snapshot diffs** — compare Gen2 heap at T=0 vs T=30min during rapid instrument switching. Target: Gen2 delta < 1MB.
2. **WinDbg `!dumpheap -stat`** — find surviving `NAudio.Midi.MidiIn` or `MeltySynth.SoundFont` instances; use `!gcroot` to find the retaining reference.

---

## Critical edge cases catalogued (all require test coverage)

| Component | Edge Case | Risk |
|-----------|-----------|------|
| MidiDeviceService | USB disconnect mid-session | App crash — Gren marked REQUIRED |
| MidiDeviceService | Device missing at startup | Startup failure |
| MidiDeviceService | Rapid USB flapping | Thread leak |
| AudioEngine | NoteOn from 20+ concurrent threads | Deadlock (MeltySynth thread safety unverified) |
| AudioEngine | SelectInstrument while notes playing | Hanging notes / state corruption |
| AudioEngine | Missing soundfont file | App crash |
| AudioEngine | SoundFont swap during render | Stale reference (Volatile pattern required) |
| InstrumentCatalog | Missing settings.json | Startup failure |
| InstrumentCatalog | Corrupted settings.json | App crash |
| All IDisposables | Wrong disposal order | Exception in shutdown sequence |
| SettingsWindow | Close without explicit event unsub | Slow COM/event handler leak |


---

### Faye: Audio Engine (MeltySynth + WASAPI, thread-safe queue)
# Audio Engine Decisions — Faye

**Date:** 2026-03-01  
**Author:** Faye (Audio Dev)  
**Status:** Proposed — pending Scribe merge

---

## Decision 1: ConcurrentQueue for MIDI → Audio Thread Handoff

**Context:** Gren flagged that MeltySynth's thread safety is unverified. NoteOn/NoteOff from the MIDI callback thread and Render() on the audio callback thread must not run concurrently.

**Decision:** The MIDI thread never touches the Synthesizer directly. It enqueues `MidiCommand` structs into a `ConcurrentQueue`. The audio thread drains the queue at the top of each `ISampleProvider.Read()` call before invoking `synth.Render()`. The audio thread is the sole owner of the Synthesizer.

**Rationale:** Lock-free on the hot path. No blocking on the audio callback. Note events are applied at render-block boundaries, which is exactly when a synthesizer is designed to process them. A simple `lock` was considered but rejected because it would block the audio thread waiting for the MIDI thread, which adds jitter.

---

## Decision 2: Volatile.Read/Write for Synthesizer Swap

**Context:** Instrument switching may require loading a new SF2 file (100–500ms). During loading the old instrument must keep playing. After loading, the swap must be visible to the audio thread without a lock.

**Decision:** Synthesizer reference stored in a field. Background task calls `Volatile.Write(ref _synthesizer!, newSynth)`. Audio callback calls `var synth = Volatile.Read(ref _synthesizer!)` once per render call and uses the local `synth` for all subsequent operations in that call.

**Rationale:** Per Gren's required pattern. The local snapshot prevents the JIT from re-reading the field mid-render and ensures the old Synthesizer stays rooted (alive) for the full duration of any in-progress render even if a swap occurs concurrently.

---

## Decision 3: SoundFont Object Cache (Keep In Memory, Share Across Synthesizers)

**Context:** Switching back to an already-used instrument should be instantaneous. SF2 files are 5–50 MB; reloading from disk each time would cause noticeable lag.

**Decision:** `Dictionary<string, SoundFont>` keyed by case-insensitive path. Populated on first load, retained for the lifetime of AudioEngine. When switching to a previously loaded SF2, only a new `Synthesizer` is constructed (sub-millisecond); no file I/O.

**Trade-off:** All cached SoundFonts occupy memory simultaneously. For typical use (2–6 instruments, one SF2 each at ~10 MB), this stays well under the 50 MB idle budget. Users with many large SF2s may exceed the budget — documented as a known limitation. A future LRU eviction policy could address this.

**Disposal:** On `AudioEngine.Dispose()`, all cached SoundFont references are cleared. `(sf as IDisposable)?.Dispose()` called defensively in case a future MeltySynth version adds IDisposable to SoundFont.

---

## Decision 4: FileStream `using` Block for SF2 Loading

**Context:** Gren required: "Use `using` on FileStream when loading SF2 files — file handle must not remain open after load."

**Decision:**
```csharp
using (var stream = File.OpenRead(path))
{
    loaded = new SoundFont(stream);
}
// stream closed here; SoundFont holds all data in managed arrays
```

**Rationale:** MeltySynth's `SoundFont(Stream)` constructor reads the entire SF2 into managed byte arrays. The stream (and its underlying file handle) is closed the moment the constructor returns. This satisfies Gren's requirement and eliminates any file handle leak.

---

## Decision 5: IMidiDeviceService Interface for Decoupling

**Context:** Faye (MidiInstrumentSwitcher) and Jet (MidiDeviceService) are working in parallel. MidiInstrumentSwitcher needs to subscribe to Program Change and Control Change events.

**Decision:** Faye defined `IMidiDeviceService` in `Interfaces/` with just two events:
```csharp
event EventHandler<MidiProgramEventArgs>? ProgramChangeReceived;
event EventHandler<MidiControlEventArgs>? ControlChangeReceived;
```
Jet implements this interface on `MidiDeviceService`. MidiInstrumentSwitcher takes `IMidiDeviceService` in its constructor.

**Rationale:** Loose coupling. Jet can implement the interface however they like (directly or via an adapter). Faye's code compiles without Jet's concrete class.

**Note:** `MidiProgramEventArgs` and `MidiControlEventArgs` are defined in `Models/MidiEventArgs.cs`. If Jet has already defined equivalent types, one set should be chosen and the other removed. Recommend consolidating to Jet's types since they own the MIDI layer.

---

## Decision 6: WASAPI Settings — 48 kHz, Stereo, 20 ms Buffer, Event Sync

**Context:** Balance between output latency and stability under Windows shared-mode WASAPI constraints.

**Decision:** 48 kHz sample rate, stereo (2 channels), 20 ms buffer, `useEventSync: true`.

**Rationale:**
- 48 kHz: standard Windows audio subsystem rate; avoids resampling in the WASAPI shared mode mixer
- 20 ms: good latency/stability balance for a background app (architecture doc suggested 50 ms as a safe default, but 20 ms is achievable with event sync and doesn't require exclusive mode)
- Event sync: WASAPI signals the app when the buffer needs refilling (lower, more consistent latency than timer-based polling)

**Configurable:** `BufferMs` is a constant in AudioEngine; a future settings hook can expose it.

---

## Decision 7: Bank Select Accumulation Without Timeout

**Context:** Standard MIDI bank select sends CC#0 (MSB), CC#32 (LSB), then PC. Values are sticky — no timeout required.

**Decision:** `MidiInstrumentSwitcher` accumulates `_pendingBankMsb` and `_pendingBankLsb` as plain fields (set on CC#0 / CC#32 respectively). Both are applied on the next PC message. No reset, no timeout.

**Rationale:** Per Gren's confirmation: "bank select values are sticky per MIDI spec — no timeout needed." The Arturia KeyLab 88 MkII follows standard MIDI bank select; the accumulated values remain valid indefinitely until updated.


---

### Jet: WinUI3 Scaffold (20 files, 0 errors)
# Jet — Scaffold Decisions

**Date:** 2026-03-01
**Author:** Jet (Windows Dev)
**Context:** Initial project scaffold — session one

---

## Decision 1: Disposal order follows architecture Section 6, not task spec

**Task spec said:** TrayIconService → AudioEngine → MidiDeviceService
**Architecture Section 6 says:** MidiDeviceService → AudioEngine → TrayIconService

**Decision:** Follow architecture Section 6.

**Rationale:** The architecture's order has an explicit engineering reason documented by Gren in review: MIDI must stop first so no note-on events can arrive at an already-disposed audio engine. A note-on firing on a disposed MeltySynth synthesizer is a potential access violation. The task spec's "reverse startup order" phrasing appears to have been written without the architecture doc in hand. Architecture takes precedence.

---

## Decision 2: SingleInstanceGuard lives in Program.Main, not AppLifecycleManager

**Decision:** `SingleInstanceGuard` is a `using` variable in `Program.Main`, not a field in `AppLifecycleManager`.

**Rationale:** The mutex must be held for the entire process lifetime. `Application.Start()` blocks until the message loop exits, so the `using` block in `Main` naturally holds the mutex for the correct duration and releases it at step 5 of Section 6 (after `Application.Current.Exit()` terminates the loop). Threading it into `AppLifecycleManager` would require passing it as a constructor argument with no benefit.

---

## Decision 3: Services/ folder for TrayIconService, MidiDeviceService, AppLifecycleManager

**Architecture doc:** Uses `Tray/`, `Midi/`, `Core/` subfolders respectively.
**Task spec:** Explicitly says `Services/TrayIconService.cs`, `Services/MidiDeviceService.cs`, `Services/AppLifecycleManager.cs`.

**Decision:** Follow task spec — all three in `Services/`.

**Rationale:** The task spec is the implementation instruction; the architecture is a design document. The folder consolidation also reduces navigation friction for a small codebase at this stage. Namespaces still reflect the folder: `MinimalMusicKeyboard.Services`. The `Midi/` subfolder is kept for DTOs (`MidiDeviceInfo`) that are pure data types, not services.

---

## Decision 4: IAudioEngine in Interfaces/ folder

**Decision:** `Interfaces/IAudioEngine.cs` with namespace `MinimalMusicKeyboard.Interfaces`.

**Rationale:** Gives Faye a clean, unambiguous place to find the contract she needs to implement. Keeps the interface clearly separated from the service implementations.

---

## Decision 5: No DI container wiring at this stage

**Task spec adds:** `Microsoft.Extensions.DependencyInjection` package.
**Architecture:** Manual wiring confirmed by Gren.

**Decision:** Package is referenced in `.csproj` for future use and testability, but `AppLifecycleManager` wires dependencies manually via `new`. No `ServiceCollection` or `IServiceProvider` used in this scaffold.

**Rationale:** Seven objects don't warrant a container. Adding DI now without a real need would add complexity and memory overhead. The package being present lets Ed or Spike introduce it later if needed without a csproj change.

---

## Decision 7: Build requires MSBuild from VS 18.0 (not dotnet CLI)

**Observation:** `dotnet build` fails with `MSB4062: ExpandPriContent task not found` because the PRI generation task requires the AppxPackage tools that ship with Visual Studio, not with the standalone dotnet SDK.

**Fix:** Build with `msbuild` from `C:\Program Files\Microsoft Visual Studio\18\Insiders\MSBuild\Current\Bin\MSBuild.exe`. Build succeeds cleanly with 0 warnings, 0 errors.

**Why:** WinUI3 projects use XAML-to-XBF compilation and PRI file generation which are VS-specific build tasks. This is expected for WinUI3. CI should use a Windows agent with Visual Studio installed. The `dotnet` CLI will work once a version of the SDK that bundles these tools is released, or if `AppxPackage` VS workload is installed.

**Decision:** `MinimalMusicKeyboard.Tests.csproj` has no `<ProjectReference>` to the main project yet.

**Rationale:** The main project targets `net8.0-windows10.0.22621.0` with WinUI3, which requires a Windows runtime environment to reference correctly. Adding the reference now before Ed has defined the test strategy risks platform/SDK compatibility issues that block CI on non-Windows agents. Ed should add the reference when he's ready to write tests and can resolve the platform constraints.


---

### Gren: Architecture Review (6 changes approved)
# Decision: Architecture Review v1.0

**Author:** Gren (Supporting Architect)
**Date:** 2026-03-01
**Scope:** Full architecture document review (docs/architecture.md)
**Requested by:** Ward Impe

## Verdict

⚠️ **APPROVED WITH REQUIRED CHANGES**

The architecture is fundamentally sound — good library choices, clean component structure, well-reasoned disposal chain. Six required changes must be addressed before implementation begins.

## Required Changes

1. **[High] MidiDeviceService disconnect handling** — App must not crash on USB MIDI device disconnect. Implement disconnected state, catch exceptions, dispose stale handles.
2. **[High] Verify MeltySynth thread safety** — The assumption that NoteOn/NoteOff can be called concurrently with Render is unverified. Inspect source or add synchronization.
3. **[Medium] Volatile semantics for Synthesizer swap** — Use `Volatile.Read`/`Volatile.Write` for the audio thread's Synthesizer reference to prevent JIT caching stale references.
4. **[Medium] SettingsWindow lifecycle** — Track open window, explicitly unsubscribe service events on close, force-close during shutdown.
5. **[Low] FileStream disposal during SF2 load** — Use `using` statement on FileStream when loading SoundFont files.
6. **[Low] Thread-safe CurrentInstrument property** — InstrumentCatalog.CurrentInstrument is read/written from different threads.

## Accepted Decisions (No Objections)

- NAudio.Midi, MeltySynth, H.NotifyIcon.WinUI library choices
- Named Mutex single-instance pattern
- WASAPI shared mode at 50ms buffer
- MIDI Program Change for instrument switching
- Manual DI wiring (no Microsoft.Extensions.Hosting)
- Unpackaged deployment for MVP
- One SoundFont at a time for MVP

## Open Questions Resolved

- DI Container → Manual wiring confirmed
- MIDI Reconnect → Disconnect handling required for MVP; auto-reconnect deferred
- Multiple SoundFonts → One at a time, swap pattern supports this
- Packaged vs Unpackaged → Unpackaged for MVP

## Changes Applied Directly to Document

Sections 3.1, 3.2, 3.6, 5, and 6 of architecture.md were updated inline with the required changes and explanatory notes.


---

### Spike: Architecture v1 (8-section design)
# Decision: Architecture v1 — Core Stack and Component Design

**Author:** Spike (Lead Architect)
**Date:** 2026-03-01
**Status:** DRAFT — Pending Gren review

## Summary

Produced the initial architecture document for Minimal Music Keyboard at `docs/architecture.md`. Key decisions:

## Stack Decisions

| Area | Choice | Rationale |
|------|--------|-----------|
| MIDI I/O | NAudio.Midi | Pure managed, battle-tested, Arturia-compatible, active maintenance |
| Audio Synthesis | MeltySynth + NAudio WasapiOut | Pure C# SF2 synthesizer, zero native deps, lock-free hot path |
| System Tray | H.NotifyIcon.WinUI | Only WinUI3-compatible tray library |
| Single Instance | Named Mutex | Works packaged + unpackaged, no IPC needed |
| Instrument Switch | MIDI Program Change | MIDI standard, Arturia pads support natively |
| Settings Storage | JSON in %LOCALAPPDATA% | System.Text.Json, schema-versioned, not roaming |

## Architecture Decisions

- **6 core components:** AppLifecycleManager, MidiDeviceService, AudioEngine, InstrumentCatalog, TrayIconService, SettingsWindow
- **SettingsWindow on-demand:** Created fresh on open, GC'd on close. No persistent window in memory.
- **Disposal chain:** Router → MIDI → Audio → Tray → Mutex (strict reverse-construction order)
- **Threading:** 3 threads (UI STA, MIDI callback, WASAPI audio render). MeltySynth thread-safe for cross-thread note events.
- **Memory target:** Estimated 29-44MB idle, within 50MB budget.

## Open Questions for Gren

1. DI container (Microsoft.Extensions.DI) vs manual wiring — weight tradeoff
2. MIDI device reconnection strategy (polling vs manual)
3. Multi-SoundFont support in MVP or deferred
4. Packaged (MSIX) vs unpackaged deployment

## Needs Validation From

- **Faye:** MeltySynth + NAudio audio pipeline assumptions
- **Ed:** Disposal chain testability

