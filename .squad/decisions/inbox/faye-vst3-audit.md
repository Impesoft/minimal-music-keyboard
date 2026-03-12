# VST3 Pipeline Audit — "Select plugin, nothing happens"

**Author:** Faye (Audio Dev)  
**Requested by:** Ward Impe  
**Date:** 2026-07-18  
**Status:** Findings complete — awaiting team action

---

## Executive Summary

The VST3 pipeline has **one definitive root cause** plus four secondary bugs that compound it. No audio will ever emerge until the root cause is fixed. Secondary bugs will surface once the bridge actually launches.

---

## 1. Root Cause of Silence

### 🔴 `ProcessStartInfo` is missing the `Arguments` field — bridge exits immediately

**File:** `src/MinimalMusicKeyboard/Services/Vst3BridgeBackend.cs`  
**Line:** ~389–396 (inside `LoadAsync`)

```csharp
var psi = new ProcessStartInfo
{
    FileName        = bridgeExePath,
    UseShellExecute = false,
    CreateNoWindow  = true,
    // ← Arguments NOT SET — should be $"{hostPid}"
};
_bridgeProcess = Process.Start(psi) ...
```

**File:** `src/mmk-vst3-bridge/src/main.cpp`  
**Lines:** 6–11

```cpp
int main(int argc, char* argv[])
{
    if (argc < 2)   // argc == 1 because no args were passed
    {
        std::cerr << "Usage: mmk-vst3-bridge.exe <hostPid>\n";
        return 1;   // ← Bridge exits immediately
    }
    const std::uint32_t hostPid = std::stoul(argv[1]);
```

**Sequence of failure:**
1. User selects VST3 plugin file in settings → `InstrumentCatalog.AddOrUpdateInstrument` persists it.
2. User triggers the slot → `MidiInstrumentSwitcher.SelectInstrumentFromUi` → `AudioEngine.HandleVst3Instrument` → `_vst3Backend.LoadAsync(instrument)`.
3. `LoadAsync` finds `mmk-vst3-bridge.exe` ✓, creates pipe server + MMF ✓, launches bridge **without `Arguments`**.
4. Bridge receives `argc == 1`, prints usage to stderr, returns 1.
5. C# `WaitForConnectionAsync` hangs for 5 seconds, then times out.
6. `OperationCanceledException` is caught (internal timeout branch), `TransitionToFaulted("Timed out waiting for bridge to respond.")` is called.
7. `_isReady = false` — permanently.
8. Every subsequent `NoteOn` returns at `if (!_isReady) return;`.
9. `Read()` returns silence for every WASAPI callback.

**Fix (single line):**
```csharp
var psi = new ProcessStartInfo
{
    FileName        = bridgeExePath,
    Arguments       = $"{hostPid}",   // ← ADD THIS
    UseShellExecute = false,
    CreateNoWindow  = true,
};
```

---

## 2. Stub Inventory — Every Unimplemented / Broken Piece

### C# side

| # | File | Line | Issue |
|---|------|------|-------|
| 1 | `Vst3BridgeBackend.cs` | ~390 | **[ROOT CAUSE]** `ProcessStartInfo.Arguments` not set; bridge exits with code 1 |
| 2 | `Vst3BridgeBackend.cs` | ~148 | `Read()` always reads from `MmfHeaderSize` (offset 16) without checking `writePos`; no ring-buffer awareness — reads same or stale frame on every WASAPI callback |
| 3 | `Vst3BridgeBackend.cs` | ~93 | `SampleRate = 48_000` — mismatches native bridge's `kSampleRate = 44_100` |
| 4 | `InstrumentCatalog.cs` | 140 | `BuildDefaultCatalog()` returns `[]` — no default instruments on fresh install (pre-existing known issue, unrelated to VST3 flow but means catalog may be empty) |

### C++ side

| # | File | Line | Issue |
|---|------|------|-------|
| 5 | `audio_renderer.h` | 53 | `kSampleRate = 44'100` — bridge initializes VST3 at 44.1 kHz; host expects 48 kHz. Plugin renders at wrong rate. |
| 6 | `audio_renderer.h` | 54 | `kMaxBlockSize = 256` — render loop processes 256 samples; MMF `frameSize = 960`. `FillBuffer` is called with `frameSize=960` but allocates `std::array<float, 256>` for L/R channels; `framesToCopy = min(960, 256) = 256` — only 256 of 960 requested frames are filled. Last 704 frame-slots written to MMF are zero (silence padding). |
| 6b | `audio_renderer.cpp` | 277 | `frameDuration` uses `kMaxBlockSize / kSampleRate` (256/44100 ≈ 5.8ms) as the render tick rate, but the MMF `frameSize` is 960 (20ms). The render thread wakes 3× as often as needed, writing 256-sample partial frames. |
| 7 | `audio_renderer.cpp` | 72 | `component_->initialize(nullptr)` — passes `nullptr` as host context. VST3 spec requires a valid `IHostApplication*`. Many plugins accept null defensively; some do not, returning `kResultFalse` which causes `Load()` to fail with "Failed to initialize VST3 component." |
| 8 | `audio_renderer.cpp` | 253–268 | `QueueSetProgram` constructs a `kLegacyMIDICCOutEvent` (an *output* event from the plugin to the host), not an input MIDI event. Sending this as part of the input `EventList` is undefined behavior per the VST3 spec and will be silently ignored by most plugins. Program change in VST3 must go via `IUnitInfo` / `IEditController` parameter changes, not the event list. |
| 9 | `audio_renderer.cpp` | entire file | `IEditController` is never queried, initialized, or connected. The controller manages plugin parameters and patch state. While audio can technically work without it for simple synths, the majority of VST3 instruments with their own GUI depend on controller init to set default patch state. |
| 10 | `audio_renderer.h` | entire file | No `IEditController`, `IPlugView`, or `IHostApplication` declared or used anywhere in the project. |

### Swallowed exceptions / silent failures

| Location | What's swallowed |
|----------|-----------------|
| `AudioEngine.cs` ~93–94 | `_wasapiOut.Stop()` and `Dispose()` caught with `/* best-effort */` |
| `AudioEngine.cs` ~300 | `backend?.NoteOffAll()` caught with empty `catch { }` in `Dispose()` |
| `InstrumentCatalog.cs` ~129 | JSON parse/deserialize exception swallowed silently; falls back to empty defaults |
| `InstrumentCatalog.cs` ~148 | `SaveCatalog` swallows all I/O exceptions with `// Non-fatal` |
| `Vst3BridgeBackend.cs` ~541 | `_pipeWriterTask?.Wait(...)` exception swallowed silently |
| `Vst3BridgeBackend.cs` ~548 | `_bridgeProcess.Kill()` swallowed |
| `bridge.cpp` ~53–58 | JSON parse errors on malformed commands silently return without logging |

---

## 3. GUI Hosting Gap

**Short answer:** Zero GUI hosting code exists anywhere in the project.

### What VST3 requires for plugin editor display

VST3 instruments expose their editor via `IEditController::createView("editor")` which returns an `IPlugView`. The view must be attached to a native OS window:

```cpp
// Required sequence (not present anywhere):
IEditController* controller = ...;   // queried from IComponent via IComponent::queryInterface or factory
IPlugView* view = controller->createView(Steinberg::Vst::ViewType::kEditor);
if (view && view->isPlatformTypeSupported("HWND") == kResultTrue) {
    view->attached(hwnd, "HWND");    // hwnd = WinUI3 HWND or child HWND
    ViewRect rect{};
    view->getSize(&rect);
    // resize hosting window to rect
}
```

### What's missing

1. **`IEditController` is never queried** — not even available as a variable in `AudioRenderer`.
2. **No `IHostApplication` stub** — the VST3 spec requires the host to implement `IHostApplication` and pass it to `initialize()`. Currently `nullptr` is passed.
3. **No WinUI3 ↔ HWND bridge** — WinUI3 windows expose `HWND` via `WinRT.Interop.WindowNative.GetWindowHandle(window)`, which would need to be marshaled over IPC to the bridge process, or the bridge would need to create its own top-level window.
4. **No IPC message for GUI** — there is no `openEditor` / `closeEditor` command in the JSON protocol, no HWND passed to the bridge.
5. **Out-of-process constraint** — because the bridge is a separate process, the plugin's `IPlugView::attached(hwnd)` would attach to a window *in the bridge process*. The bridge must create its own `HWND` or use a cross-process window parenting approach (e.g., `SetParent` with a `WS_CHILD` window), which is complex.

---

## 4. IPC Protocol Completeness

### Commands defined in C# (`SerializeCommand`)

| Command | JSON key | Implemented C# | Implemented C++ |
|---------|----------|---------------|-----------------|
| Load plugin | `load` + `path` + `preset` | ✅ | ✅ (`renderer_.Load(path, preset, error)`) |
| Note On | `noteOn` + `channel` + `pitch` + `velocity` | ✅ | ✅ (`renderer_.QueueNoteOn`) |
| Note Off | `noteOff` + `channel` + `pitch` | ✅ | ✅ (`renderer_.QueueNoteOff`) |
| Note Off All | `noteOffAll` | ✅ | ✅ (`renderer_.QueueNoteOffAll`) |
| Set Program | `setProgram` + `program` | ✅ | ✅ (but broken — see stub #8 above) |
| Shutdown | `shutdown` | ✅ | ✅ |

### Commands NOT defined but needed

| Command | Why needed |
|---------|-----------|
| `setSampleRate` | Allow host to reconfigure bridge after WASAPI device change |
| `openEditor` + HWND | Request plugin GUI window |
| `closeEditor` | Close plugin GUI window |
| `setParameter` + paramId + value | Send VST3 parameter changes (automation, patch recall) |
| `getState` / `setState` | Save/restore plugin state (preset management) |
| `ping` / `ready` | Bridge → host readiness handshake (currently implicit via `load_ack`) |

### Ack protocol note

`ParseLoadAck` accepts `"ack"` value of either `"load"` or `"load_ack"`. The C++ bridge sends `"load_ack"`. This works, but the double-value check is unnecessary — `"load_ack"` only.

---

## 5. Fix Roadmap

### Tier 1 — Minimum to make sound (2 fixes required)

**Fix 1 — Pass `hostPid` to bridge process** ← ROOT CAUSE  
File: `Vst3BridgeBackend.cs`  
Change `ProcessStartInfo` to include `Arguments = $"{hostPid}"`.

**Fix 2 — Align sample rate and block size**  
File: `audio_renderer.h`  
- Change `kSampleRate` from `44'100` to `48'000` to match the host's WASAPI setup and MMF contract.
- Change `kMaxBlockSize` from `256` to `960` to match the MMF `frameSize` written by the C# host. Or, alternatively, have the C++ bridge read `frameSize` from the MMF header and use that as its render block size (already available via `writer_->FrameSize()` — just use it instead of the hardcoded constant for the array sizes too, which requires switching from `std::array` to `std::vector`).

After these two fixes, the bridge will launch, connect, load the plugin, and render audio into the MMF. MIDI note events will flow. Most VST3 instruments will produce sound.

---

### Tier 2 — Correct VST3 hosting (make it spec-compliant)

**Fix 3 — Implement `IHostApplication`**  
Create a minimal `IHostApplication` stub in `audio_renderer.cpp` / a new `host_application.h`. Pass it to `IComponent::initialize(hostApp)` and `IEditController::initialize(hostApp)`.

**Fix 4 — Query and initialize `IEditController`**  
After `IComponent::initialize`, query `IEditController` via:
```cpp
Steinberg::Vst::IEditController* controller = nullptr;
component_->queryInterface(Steinberg::Vst::IEditController::iid, (void**)&controller);
if (!controller) {
    // Try via factory: factory.createInstance<IEditController>(audioEffectClass->cid())
}
if (controller) controller->initialize(hostApp);
```
Store `Steinberg::IPtr<Steinberg::Vst::IEditController> controller_` in `AudioRenderer`.

**Fix 5 — Fix `QueueSetProgram`**  
Remove `kLegacyMIDICCOutEvent` approach. Use `IEditController` parameter changes via `IParameterChanges` in `ProcessData.inputParameterChanges` to send program change, or use `IUnitInfo::getProgramName` + a dedicated program-change parameter if the plugin exposes one.

---

### Tier 3 — Read() ring-buffer awareness

**Fix 6 — Track `writePos` in `Read()`**  
`Vst3BridgeBackend.Read()` should snapshot `writePos` from the MMF header (offset 12) and only copy audio if the bridge has written a new frame since the last read. Without this, the C# side may re-read the same frame multiple times between bridge render ticks (causing repetition at low volumes or with phase artifacts), or read partially-written frames.

Add a `volatile int _lastReadPos` field. In `Read()`:
```csharp
int writePos = _mmfView.ReadInt32(12);
if (writePos == _lastReadPos) {
    // Bridge hasn't written a new frame — return silence or hold last frame
    Array.Clear(buffer, offset, count);
    return count;
}
_lastReadPos = writePos;
// ... then ReadArray as now
```

---

### Tier 4 — GUI hosting

**Fix 7 — Add `openEditor` IPC command**  
Add a new JSON command `{"cmd":"openEditor"}` → bridge creates a top-level `HWND` owned by the bridge process, calls `IEditController::createView("editor")` and `IPlugView::attached(hwnd, "HWND")`.

**Fix 8 — Surface editor window**  
Two options:
- (Simple) Bridge creates its own top-level borderless window and manages it independently. Host just sends `openEditor` / `closeEditor`.
- (Integrated) Bridge creates a child `HWND`, host obtains the bridge process's `HWND` (via IPC response) and reparents it into the WinUI3 window using `SetParent` + `WS_CHILD` style (cross-process window parenting — works on Windows but requires care with DPI scaling).

**Fix 9 — Add `getSize` / `resize` IPC round-trip**  
The plugin's preferred editor size comes from `IPlugView::getSize(&rect)`. The host needs this to size the hosting panel correctly.

---

## Appendix — Full Activation Sequence (as-designed vs. actual)

### Designed sequence
```
User selects VST3 file
  → InstrumentCatalog.AddOrUpdateInstrument (persists)
  → MidiInstrumentSwitcher.SelectInstrumentFromUi
  → AudioEngine.HandleVst3Instrument
  → Volatile.Write(_activeBackend, _vst3Backend)
  → _vst3Backend.LoadAsync(instrument)
      → Create pipe server + MMF
      → Process.Start(bridge, hostPid)      ← FAILS: no Arguments
      → WaitForConnectionAsync (5s timeout)
      → Send load command
      → Await load_ack
      → Start RunPipeWriterTask
      → _isReady = true
MIDI event arrives
  → _commandQueue.Enqueue(NoteOn)
  → AudioEngine.ReadSamples (WASAPI callback)
  → _commandQueue.TryDequeue → backend.NoteOn
  → Vst3BridgeBackend.NoteOn → _commandChannel.Writer.TryWrite
  → RunPipeWriterTask → WriteLineAsync({"cmd":"noteOn",...})
  → Bridge HandleCommand → renderer_.QueueNoteOn
  → AudioRenderer.RenderLoop → FillBuffer → processor_->process
  → MmfWriter.WriteFrame → shared memory
  → Vst3BridgeBackend.Read → ReadArray from MMF → WASAPI output
```

### Actual sequence (broken)
```
Process.Start(bridge)   ← bridge exits code 1 (no hostPid arg)
WaitForConnectionAsync  ← times out after 5s
TransitionToFaulted     ← _isReady = false
NoteOn                  ← if (!_isReady) return;  ← SILENT
Read()                  ← if (!_isReady) { Array.Clear; return count; } ← SILENCE
```

---

*End of audit. Recommend Ward assigns Fix 1 + Fix 2 as an immediate hot-fix sprint.*
