# Ed — History

## Core Context (Project Day 1)

**Project:** Minimal Music Keyboard — lightweight WinUI3 MIDI player for Windows 11
**Requested by:** Ward Impe
**Stack:** WinUI3 (Windows App SDK), C#/.NET
**Primary MIDI device:** Arturia KeyLab 88 MkII

**What it does:**
- Lives in the Windows 11 system tray (notification area)
- Listens continuously to a configured MIDI device in the background
- Routes MIDI note/CC/PC input to a selected software instrument (soundfonts/synthesis)
- Users switch instruments via MIDI commands — no need to open the settings UI
- Settings page: MIDI device selection, instrument configuration, startup options
- Must be memory-leak-free — will run continuously for hours/days
- Exit option from tray context menu

**Ed's QA focus:**
- Long-running stability (app in tray for hours/days without degrading)
- Resource disposal verification (all IDisposable patterns complete)
- MIDI device lifecycle (plug/unplug during operation)
- Concurrent access safety (MIDI thread vs. UI thread vs. audio thread)
- Rapid instrument switching, sustained chord bursts, edge case MIDI messages

## Learnings

<!-- append new learnings below -->

### 2026-03-01 — Test Strategy + Scaffolding (Ward Impe task)

**Test pyramid decision:** 75% unit / 20% integration / 5% manual. Hardware I/O (MIDI, WASAPI) is untestable in CI, so unit layer must cover all correctness and stability guarantees. Integration tests cover AppLifecycle wiring and settings persistence.

**Tooling chosen:** xUnit 2.7 + Moq 4.20 + FluentAssertions 6.12. No Fakes or NSubstitute — Moq is sufficient. Test project targets `net8.0-windows10.0.22621.0` matching production.

**Interface-first scaffolding:** Production code is written in parallel; test project defines minimal interface stubs (`IMidiDeviceService`, `IAudioEngine`, `IInstrumentCatalog`, `IMidiInput`) under production namespaces (`MinimalMusicKeyboard.Midi` etc.). When production project is added, stubs are removed and replaced with `<ProjectReference>`.

**WeakReference disposal pattern:** `[MethodImpl(NoInlining)]` is REQUIRED on the factory helper. Without it the JIT keeps a hidden root and `IsAlive` returns a false positive. `GC.Collect(2, Forced, blocking: true)` called twice (with `WaitForPendingFinalizers` between) guarantees generational promotion.

**Event handler leak is the primary risk:** `MidiMessageRouter`, `SettingsWindow`, `InstrumentSwitcher` all subscribe to `MidiDeviceService` events. If `Dispose()` doesn't null the invocation list, subscriber objects root through the event. The `HasNoteReceivedSubscribers` test helper exposes this without reflection.

**InstrumentCatalog file tests:** Used temp directories with real JSON files rather than mocking `IFileSystem`. More realistic, catches JSON serialization bugs, and `CatalogLoader` test helper is small enough to justify.

**Key edge cases catalogued:**
- USB MIDI disconnect mid-session (Gren marked as REQUIRED in architecture)
- `SelectInstrument` while notes are playing → NoteOffAll required before preset change
- SoundFont file missing → engine must stay alive in degraded state, not crash
- Corrupted settings JSON → always fall back to defaults, never throw
- Rapid connect/disconnect cycles → no thread leak (each reconnect attempt must not spawn a permanent thread)
- Double Dispose → must be safe no-op on all components
- Wrong disposal order (audio before MIDI) → each component defends itself

**Memory budget tests:** `FullLifecycle_AllServicesDisposed_MemoryReturnsToBaseline` verifies <2MB delta after a full create/use/dispose cycle. Long-running stability tests (1h+ runs, Gen2 flatness) are tagged `[Trait("Category", "Stability")]` and excluded from PR CI.

