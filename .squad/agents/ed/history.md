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

## Historical Summary (2026-03-01 through 2026-03-12)

**Test framework established:** xUnit 2.7 + Moq 4.20 + FluentAssertions 6.12. Test project targets `net8.0-windows10.0.22621.0`. Interface-first scaffolding with production namespaces defined first.

**Disposal verification patterns:** WeakReference + GC.Collect(2, Forced) required for leak detection. `[MethodImpl(NoInlining)]` essential to prevent JIT from keeping hidden roots. Event handler leaks primary risk — verification pattern: subscribe to events, dispose all objects, check no subscribers remain.

**InstrumentCatalog testing:** Real JSON files in temp directories (not mocks). Catches serialization bugs. CatalogLoader test helper captures common patterns.

**VST3 bridge integration tests:** Created minimal harness using named pipe + MMF (same contract as production Vst3BridgeBackend). Validates native bridge independently of WinUI app. Reusable pattern for sidecar process verification.

**Zero automated test coverage:** `dotnet test MinimalMusicKeyboard.sln` discovers 0 tests. No regression suite covering VST3/UI behavior — critical gap flagged for team.

<!-- append new learnings below -->

**Deployment parity verified:** `src\MinimalMusicKeyboard\MinimalMusicKeyboard.csproj` now copies `src\mmk-vst3-bridge\build\Release\mmk-vst3-bridge.exe` into the Debug app output, and the shipped Debug bridge at `src\MinimalMusicKeyboard\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64\mmk-vst3-bridge.exe` matched the rebuilt native bridge byte-for-byte (same size, timestamp, and SHA-256) after `dotnet build -c Debug -p:Platform=x64`.

**Reusable validation pattern:** For sidecar bridge verification, a minimal host harness is enough — create the same named pipe (`mmk-vst3-{pid}`) and MMF (`mmk-vst3-audio-{pid}`) that `Vst3BridgeBackend` uses, launch the candidate bridge exe with the host PID, send `load` then `openEditor`, and compare the raw ACK JSON. Against `C:\Program Files\Common Files\VST3\OB-Xd 3.vst3`, both the shipped Debug bridge and rebuilt native bridge returned the same successful `load_ack` plus the same detailed `IPlugView::attached(HWND)` timeout on `openEditor`.

**Remaining validation gap:** This direct bridge probe proves the stale-deployment issue is fixed, but it does not prove the WinUI app surfaces the native diagnostic end-to-end. Current automated validation is also weak: `dotnet test MinimalMusicKeyboard.sln -c Debug -p:Platform=x64` completed with **0 discovered tests**, so there is still no executed regression suite covering shipped VST3/UI behavior.

<!-- append new learnings below -->

### 2026-03-01 — Test Strategy + Scaffolding (Ward Impe task)

**Test pyramid decision:** 75% unit / 20% integration / 5% manual. Hardware I/O (MIDI, WASAPI) is untestable in CI, so unit layer must cover all correctness and stability guarantees. Integration tests cover AppLifecycle wiring and settings persistence.

**Tooling chosen:** xUnit 2.7 + Moq 4.20 + FluentAssertions 6.12. No Fakes or NSubstitute — Moq is sufficient. Test project targets `net8.0-windows10.0.22621.0` matching production.

**Interface-first scaffolding:** Production code is written in parallel; test project defines minimal interface stubs (`IMidiDeviceService`, `IAudioEngine`, `IInstrumentCatalog`, `IMidiInput`) under production namespaces (`MinimalMusicKeyboard.Midi` etc.). When production project is added, stubs are removed and replaced with `<ProjectReference>`.

**WeakReference disposal pattern:** `[MethodImpl(NoInlining)]` is REQUIRED on the factory helper. Without it the JIT keeps a hidden root and `IsAlive` returns a false positive. `GC.Collect(2, Forced, blocking: true)` called twice (with `WaitForPendingFinalizers` between) guarantees generational promotion.

**Event handler leak is the primary risk:** `MidiMessageRouter`, `SettingsWindow`, `InstrumentSwitcher` all subscribe to `MidiDeviceService` events. If `Dispose()` doesn't null the invocation list, subscriber objects root through the event. The `HasNoteReceivedSubscribers` test helper exposes this without reflection.

**InstrumentCatalog file tests:** Used temp directories with real JSON files rather than mocking `IFileSystem`. More realistic, catches JSON serialization bugs, and `CatalogLoader` test helper is small enough to justify.

### 2026-03-13 — Final Bridge Parity Verification + Test Coverage Gap Flag

**Validation method:** Created minimal host harness using same named pipe (`mmk-vst3-{pid}`) and MMF (`mmk-vst3-audio-{pid}`) that `Vst3BridgeBackend` uses. Launched candidate bridge exe with host PID, sent `load` then `openEditor`, parsed raw ACK JSON.

**Parity result:** Shipped Debug bridge at `bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64\mmk-vst3-bridge.exe` byte-matches rebuilt Release bridge:
- Same file size, timestamp, SHA-256
- Identical `load_ack` JSON against installed `C:\Program Files\Common Files\VST3\OB-Xd 3.vst3`
- Identical `openEditor` behavior (now successful attach without deadlock; plugin-side UI incomplete)

**Critical test gap flagged:** Current test suite runs 0 tests — `dotnet test MinimalMusicKeyboard.sln -c Debug -p:Platform=x64` discovers nothing. No automated regression coverage for VST3/UI behavior path. End-to-end WinUI app validation still manual.

**Recommendation:** Establish automated bridge integration tests in test project before shipping.

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

### 2026-03-13 — VST Crackle Failure-Mode Triage

**Most likely crackle mode:** timing-domain mismatch between the bridge producer thread and the host/WASAPI consumer, not stale-buffer replay. The bridge renders on its own `std::thread` paced by `sleep_until` (`src\mmk-vst3-bridge\src\audio_renderer.cpp:832-848`), while the app consumes on the WASAPI callback thread (`src\MinimalMusicKeyboard\Services\AudioEngine.cs:239-289`). Those clocks are not synchronized; the MMF ring only absorbs short-term drift.

**Why stale-buffer replay is unlikely in current code:** the managed reader now uses a version-2 MMF ring with `writeCounter`, `ringCapacity`, and buffered partial-block consumption (`src\MinimalMusicKeyboard\Services\Vst3BridgeBackend.cs:91-109`, `139-215`). If no new block is published, the reader zero-fills the remainder instead of replaying an old block. The native writer also publishes whole blocks into ring slots before atomically advancing `writeCounter` (`src\mmk-vst3-bridge\src\mmf_writer.cpp:46-65`).

**What to watch when reproducing:**
- `publishedBlockCount <= _readBlockCounter` or zero-fill remainder in `Vst3BridgeBackend.Read()` -> consumer underrun / late producer
- `unreadBlockCount > _ringCapacity` in `Vst3BridgeBackend.Read()` -> producer outran consumer / drift overflow
- periodic pops at ~ring-depth intervals without CPU pegging -> scheduling jitter / unsynchronized pacing
- persistent pops from first load across every plugin and device would make block-size negotiation the next suspect, but current code keeps `frameSize = 960` on both sides (`Vst3BridgeBackend.cs:291-308`, `audio_renderer.cpp:523-528`, `832-848`)

**Verification status:** `dotnet build MinimalMusicKeyboard.sln -c Debug -p:Platform=x64` succeeds; `dotnet test MinimalMusicKeyboard.sln -c Debug -p:Platform=x64 --no-build` still discovers 0 tests, so this path remains unguarded by automation.
