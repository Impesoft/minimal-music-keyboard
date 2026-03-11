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

### 2026-03-11 — Test Baseline Report (Ward Impe task)

**Objective:** Establish test baseline before Phase 1 (AudioEngine refactor to IInstrumentBackend) begins.

**Findings:**
- **37 tests across 4 files:** AudioEngineTests (11), DisposalVerificationTests (6), InstrumentCatalogTests (13), MidiDeviceServiceTests (7)
- **All tests use stubs:** Test project does NOT reference production project (`MinimalMusicKeyboard.csproj`). All tests use interface stubs from `Stubs/Interfaces.cs` and test doubles from `Stubs/TestDoubles.cs`. This was an intentional scaffold (tests written before production code).
- **Zero integration tests:** No tests exercise the real `AudioEngine` implementation. Stubs cannot verify:
  - ConcurrentQueue command drain in `ReadSamples()` (AudioEngine.cs line 259)
  - `Volatile.Read(ref _synthesizer)` snapshot pattern (line 246)
  - WASAPI thread lifecycle (`WasapiOut.Init/Play/Stop/Dispose`)
  - `SwapSynthesizerAsync` background task + `Volatile.Write` swap (line 194)
- **Test execution blocked:** `dotnet test` fails with "Permission denied" (system/CI configuration issue)
- **Build successful:** `dotnet build` succeeds — tests compile correctly

**Critical gap for Phase 1:** Phase 1 refactors the audio hot path (command queue, volatile snapshot, WASAPI threading). We have zero tests that verify this real threading model. Regression risk is HIGH without integration tests.

**Deliverables:**
1. **Test baseline report:** `docs/test-baseline.md` — comprehensive gap analysis, 29 test methods documented, Phase 1 regression guard checklist
2. **Integration test draft:** `tests/.../AudioEngineIntegrationTests.cs` — 10 integration tests for real `AudioEngine` (does not compile yet — needs project reference)
3. **Recommendations:**
   - Add `<ProjectReference>` to test project
   - Remove stub interfaces, keep test doubles (update to implement production interfaces)
   - Run integration tests to establish real baseline before Phase 1 work
   - Add 2-3 critical tests: command queue drain, WASAPI thread termination

**Test coverage verdict:**
- ✅ **Good:** Concurrency contracts (no deadlock), disposal correctness (no leaks), settings persistence, error handling
- ❌ **Missing:** Real audio threading, command queue drain, `Volatile` swap pattern, soundfont cache behavior
- ⚠️ **Risk:** Phase 1 can proceed with manual code inspection for regression guard, but integration tests must be added ASAP for long-term stability

**Key architectural patterns verified by stubs:**
- WeakReference disposal pattern (event handler leak detection)
- Concurrent access safety (20 threads calling NoteOn)
- Graceful degradation (missing files, corrupted JSON)
- Wrong disposal order resilience

**Next actions:** Ward reviews baseline report → decides if Phase 1 proceeds with manual inspection or waits for integration tests.

### 2026-03-11 — Phase 1 Verification (Ward Impe task)

**Objective:** Verify build compiles and all existing tests pass after Faye's Phase 1 (IInstrumentBackend refactor).

**Build: ❌ FAIL**
- `dotnet build` fails with 2 C# errors + 1 XAML cascade error.
- Root cause: stray extra `)` in `AudioEngine.cs` line 177 inside `LoadSoundFont()`.
- Line reads `}));` — should be `});`. Extra parenthesis after object initializer closing brace causes `CS1002` and `CS1513`.
- Fix: change `}));` → `});` on line 177.

**Tests: ⚠️ BLOCKED (same as baseline — not a regression)**
- `dotnet test` returns "Permission denied" — identical to baseline environment limitation.
- Test project still compiles successfully (no reference to production project).

**Regressions: 1 — Build now fails (was passing at baseline).**

**Architecture inspection (manual, pending build fix):**
- `Volatile.Read(ref _synthesizer)` preserved in `SoundFontBackend.Read()` ✅
- `Volatile.Write(ref _synthesizer!, newSynth)` preserved in `SoundFontBackend.LoadAsync()` ✅
- Command queue drain loop in `AudioEngine.ReadSamples()` preserved ✅
- `NoteOffAll` enqueued before instrument swap ✅
- SoundFont cache moved to `SoundFontBackend` (cache behavior preserved) ✅
- Phase 1 structure is sound; only the typo blocks the build.

**Deliverable:** `.squad/decisions/inbox/ed-phase1-verification.md` written with full details and required fix.

### 2026-03-11 — Phase 4 Fixes (Ward Impe task — Jet locked out)

**Objective:** Apply two required fixes from Gren's Phase 4 rejection before re-review.

**Fix 1 — `InstrumentDefinition` immutability restored:**
- Changed `Type`, `SoundFontPath`, `Vst3PluginPath`, `Vst3PresetPath` from `{ get; set; }` to `{ get; init; }` in `Models/InstrumentDefinition.cs`.
- All other properties were already `init` — now fully consistent.
- No call sites broken; all use `with` expressions (compatible with `init`).
- Prevents data race: `InstrumentDefinition` crosses the audio thread boundary via `Volatile.Read`/`Volatile.Write`; mutable setters on a live instance are unsafe.

**Fix 2 — VST3 program number collision resolved:**
- Added `if (inst.Type == InstrumentType.SoundFont)` guard before every `_byProgramNumber` insert in `Services/InstrumentCatalog.cs` (4 rebuild loops: constructor, `UpdateAllSoundFontPaths`, `UpdateInstrumentSoundFont`, `AddOrUpdateVst3Instrument`).
- VST3 instruments (slot indices 0–7) were overwriting GM/SF2 entries in `_byProgramNumber`, making those SF2 instruments unreachable via MIDI program change.
- VST3 instruments are triggered by button mappings only, not MIDI PC messages — correct to exclude them from `_byProgramNumber`. Still reachable via `GetById("vst3-slot-{N}")`.

**Build: ✅ 0 errors, 2 pre-existing warnings** (CS0414: `_frameSize` in Vst3BridgeBackend — Phase 3 placeholder).

**Deliverable:** `.squad/decisions/inbox/ed-phase4-fixes.md`

## Learnings
