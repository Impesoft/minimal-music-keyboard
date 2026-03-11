# VST3 Hosting Options for .NET/C# on Windows

**Author:** Faye (Audio Dev)  
**Date:** 2026-03-01  
**Context:** Research spike for VST3 architecture design (Spike's deliverable)

---

## Executive Summary

For hosting VST3 plugins in our .NET 8 WinUI3 Windows app, **Option A (Direct COM P/Invoke)** is recommended for the initial implementation, with **Option C (out-of-process bridge)** as a safety fallback if COM lifetime or plugin stability becomes problematic in production.

**Key finding:** No production-ready NuGet package exists for hosting VST3 plugins in C#. All existing packages (NPlug, AudioPlugSharp, VST.NET) focus on plugin *creation*, not hosting.

---

## Option A: Direct COM P/Invoke ✅ **RECOMMENDED**

### Description
VST3 plugins on Windows are COM DLLs. Load via `LoadLibrary` + `GetProcAddress("GetPluginFactory")`, then consume the VST3 COM interface hierarchy through `[ComImport]` or manual vtable P/Invoke.

### Minimum COM Interfaces Required

1. **IPluginFactory3** (entry point)
   - `GetClassInfo(int index, out PClassInfo info)` — enumerate available plugin classes
   - `CreateInstance(ref Guid cid, ref Guid iid, out IntPtr obj)` — instantiate a plugin component

2. **IComponent** (plugin initialization and I/O configuration)
   - `Initialize(IntPtr context)` — plugin setup
   - `SetActive(bool state)` — activate/deactivate processing
   - `SetIoMode(IoMode mode)` — configure audio/MIDI routing
   - `GetBusInfo(BusDirection dir, int index, out BusInfo info)` — query audio buses

3. **IAudioProcessor** (audio rendering)
   - `SetBusArrangements(IntPtr inputs, int numIns, IntPtr outputs, int numOuts)` — configure channel layout
   - `SetProcessing(bool state)` — start/stop processing
   - `Process(ref ProcessData data)` — **hot path** — render one audio block
     - `ProcessData` struct contains: input/output audio buffers, MIDI events, tempo/time signature, parameter changes

4. **IEditController** (parameter automation and UI)
   - `SetComponentState(IntPtr stream)` — restore plugin state
   - `GetParameterInfo(int index, out ParameterInfo info)` — query parameters for automation
   - `SetParamNormalized(uint id, double value)` — apply parameter changes

### Implementation Estimate

- **~400-600 lines** of P/Invoke scaffolding:
  - Interface definitions with GUIDs (from VST3 SDK headers)
  - Struct marshaling for `ProcessData`, `BusInfo`, `ParameterInfo`
  - `LoadLibrary`/`GetProcAddress` wrapper
  - COM lifetime management (`Marshal.AddRef`/`Marshal.Release`)
- **~200-300 lines** of audio thread integration:
  - Queue MIDI events (NoteOn/NoteOff/PitchBend/CC) from keyboard input
  - `IAudioProcessor.Process()` call inside NAudio `IWaveProvider.Read()` callback
  - Interleave VST3 output buffers with existing MeltySynth output for mixing

### Key Risk Areas

1. **COM reference counting** — Incorrect `AddRef`/`Release` calls cause memory leaks or premature disposal. Use `Marshal.Release(IntPtr)` defensively.
2. **ProcessData marshaling** — `Process()` takes a large struct with nested pointers (audio buffers, event lists). Must pin managed arrays with `GCHandle.Alloc(Pinned)` or use `stackalloc` for zero-copy.
3. **Thread affinity** — Audio thread must be the sole caller of `IAudioProcessor.Process()`. MIDI events queued from input thread.
4. **VST3 plugin crashes** — Buggy plugins crash the entire app (no isolation). Mitigate with Option C if needed.

### Pros
- **Zero dependencies** — aligns with "lean" philosophy
- **No latency overhead** — direct in-process calls
- **Full control** — custom error handling, diagnostics

### Cons
- **High complexity** — 600+ LOC of brittle interop code
- **Maintenance burden** — VST3 SDK evolves; interface GUIDs/signatures must stay synchronized
- **No crash isolation** — plugin bugs crash the main app

---

## Option B: VST.NET / NuGet Packages ❌ **NOT VIABLE**

### Assessment
**No production-ready NuGet package exists for VST3 hosting.**

1. **VST.NET (Jacobi.Vst)** — VST2 only. Has a `Jacobi.Vst3.Core` stub in the repo, but it's incomplete and undocumented. Not usable for production.
2. **NPlug** — Plugin *creation* SDK. Uses NativeAOT to compile C# code into VST3 DLLs. Does not host external plugins.
3. **AudioPlugSharp** — Plugin creation with a C++/CLI bridge. Has a "host" sample, but it's Windows-only and designed for AudioPlugSharp-created plugins, not arbitrary VST3 DLLs.

### Recommendation
Skip this option. No viable package exists as of 2024.

---

## Option C: Out-of-Process VST3 Bridge 🛡️ **FALLBACK**

### Description
Build a small native executable (C++ or Rust) that:
1. Loads the VST3 plugin via the official Steinberg SDK
2. Exposes a named pipe or shared memory interface for:
   - MIDI events (NoteOn/NoteOff/PitchBend/CC) sent from main app → bridge
   - Audio blocks (stereo float32 PCM) sent from bridge → main app
3. Runs as a child process; crashes are isolated from the main app

### Latency Impact
- **~5-10ms per round-trip** (named pipe serialization + context switch)
- For 48kHz @ 512-sample blocks: baseline latency is ~10.7ms. Bridge adds another 5-10ms = 15-20ms total.
- **Acceptable for most use cases**, but professional/studio users may notice.

### Pros
- **Crash isolation** — buggy VST3 plugin crashes bridge process, not main app. Restart bridge automatically.
- **Cleaner codebase** — main app stays pure C#. Bridge wraps all COM/C++ complexity.
- **Security boundary** — VST3 plugins run in a separate process; sandboxing possible.

### Cons
- **Extra latency** — 5-10ms IPC overhead
- **Build complexity** — must ship a native binary (bridge.exe) alongside the .NET app
- **IPC maintenance** — serialization protocol for MIDI events, audio buffers, parameter changes

### Implementation Estimate
- **Bridge (C++):** ~800-1000 lines (VST3 SDK integration + named pipe server)
- **C# client:** ~200 lines (named pipe client + audio buffer deserialization)
- **Total:** ~1200 lines

---

## Option D: No VST3 Hosting — Use Virtual ASIO Driver ❌ **NOT APPLICABLE**

Most VST3 instruments do **not** expose a system-level audio driver (ASIO/WASAPI). This is only true for a small subset of plugins (e.g., Steinberg's own tools). Not a viable general solution.

---

## Option E: CLAP Format ⚠️ **OUT OF SCOPE**

CLAP (CLever Audio Plugin) is an open alternative to VST3 with a cleaner C API (easier P/Invoke). However:
1. **User explicitly requested VST3**, not CLAP.
2. CLAP adoption is growing but still niche compared to VST3.
3. Supporting both formats would double the hosting complexity.

**Defer CLAP to a future iteration** if there's demand.

---

## Audio Pipeline Integration (Arturia KeyLab 88 MkII Use Case)

For the target hardware:
1. **MIDI input** (handled by Jet's `MidiDeviceService`):
   - NoteOn/NoteOff → forward to VST3 instrument + existing MeltySynth
   - Pitchbend (MIDI CC #E0) → both engines
   - Sustain pedal (CC #64) → both engines
2. **Audio output mixing**:
   - `IWaveProvider.Read()` callback renders **both** MeltySynth (soundfont) and VST3 instrument
   - Mix outputs: `finalSample = (meltySynthSample * gain1) + (vst3Sample * gain2)`
   - Balance controlled via UI setting: 0% = VST3 only, 50% = equal mix, 100% = MeltySynth only
3. **WASAPI output** (existing `NAudio.Wave.WasapiOut`):
   - No changes needed — VST3 audio is just another input to the mixer

---

## Recommendation Summary

| Option | Complexity | Latency | Crash Safety | Verdict |
|--------|-----------|---------|--------------|---------|
| **A: Direct COM P/Invoke** | High (600 LOC) | None | Poor (in-process) | ✅ **Initial implementation** |
| B: NuGet Package | N/A | N/A | N/A | ❌ Nothing exists |
| **C: Out-of-Process Bridge** | Very High (1200 LOC) | +5-10ms | Excellent (isolated) | 🛡️ **Fallback if Option A crashes in prod** |
| D: Virtual ASIO Driver | N/A | N/A | N/A | ❌ Not applicable |
| E: CLAP Format | Medium-High | None | Poor | ⚠️ Out of scope (user wants VST3) |

### Action Plan
1. **Spike (architecture design)** documents the COM interface hierarchy and threading model.
2. **Faye implements Option A** as a `Vst3Host` class in `src/MinimalMusicKeyboard/Services/`.
3. **Test with a known-stable VST3 instrument** (e.g., Steinberg HALion Sonic SE — free, well-tested).
4. **Monitor for crashes/leaks** in Ward's daily use. If stability issues arise, pivot to Option C.

---

## References
- Steinberg VST3 SDK: https://github.com/steinbergmedia/vst3sdk
- VST3 API Docs: https://steinbergmedia.github.io/vst3_doc/
- Stack Overflow: "How to use VST3 plugin inside NET Application" (limited, but confirms no turnkey solution)
- NPlug (plugin creation, not hosting): https://github.com/xoofx/NPlug
- AudioPlugSharp (plugin creation): https://github.com/mikeoliphant/AudioPlugSharp
