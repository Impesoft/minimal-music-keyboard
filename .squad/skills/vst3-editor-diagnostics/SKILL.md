---
name: "vst3-editor-diagnostics"
description: "Diagnose VST3 editor-open failures stage by stage and surface actionable messages"
domain: "audio-plugin-hosting"
confidence: "high"
source: "manual"
---

## Context
Use this when a hosted VST3 plugin fails to open its editor or reports a vague "no GUI" message. The goal is to identify the exact failure stage and expose that diagnosis all the way to the app UI.

## Patterns

### Track editor capability during load
During plugin load, record whether editor support was found and why:
- direct `IEditController` query result
- `getControllerClassId()` result
- factory `createInstance()` result
- controller `initialize()` result
- whether the controller shares the component instance

Return that data in the bridge load ACK so the managed host can disable the editor button and show the exact reason before the user retries.

If `supportsEditor` is `false`, never leave the load ACK diagnostic blank. If discovery never produced a reason, fall back to the native load error (or another explicit native-side explanation) before serializing the ACK.

### Verify the deployed bridge binary, not just the build output
When the managed app talks to a native helper executable, confirm the binary copied beside the app is the one you just built. A stale deployed bridge can silently mask protocol changes like new `load_ack` fields even when the source and producer build output are correct.

Pay special attention to MSBuild copy targets and relative paths from the app project directory; one wrong `..\` can leave an old bridge exe in `bin\...` and make the UI appear to ignore native changes.

### Use a minimal host harness for binary-parity checks
When you need to prove the shipped sidecar bridge behaves like the rebuilt native exe, you do not need the full WinUI app. Mirror `Vst3BridgeBackend` just enough to:
- create `mmk-vst3-{pid}` as a named pipe server,
- create `mmk-vst3-audio-{pid}` as the shared MMF and write the 16-byte header,
- launch the candidate `mmk-vst3-bridge.exe <pid>`,
- send `load`, then `openEditor`, and compare the raw ACK payloads across binaries.

This is especially useful for OB-Xd-style investigations: it separates **deployment parity** from **managed UI surfacing**. If the shipped bridge and rebuilt bridge return the same ACK JSON, any remaining mismatch is in deployment selection or managed/UI handling, not in the native helper itself.

### Add a second diagnostic path for missing ACKs
If the bridge can crash or abort before replying, the managed host should redirect and buffer the child process stdout/stderr and include the bridge exit code in any "missing ACK" failure.

That way a failed `load` command degrades to a concrete message like "bridge exited with 0xC0000005" or the last native stderr line instead of a useless `<no response>` placeholder.

### Respect shared component/controller lifetime
Some plugins implement `IComponent` and `IEditController` on the same object. When that happens:
- do **not** call `initialize()` twice
- do **not** call `terminate()` twice
- do **not** connect the object to itself through `IConnectionPoint`

Treat the existing component initialization as the controller initialization.

### Report editor-open stages explicitly
For actual editor creation, emit stage-specific failures for:
- `createView(kEditor)` returning null
- `isPlatformTypeSupported(kPlatformTypeHWND)` rejecting HWND
- Win32 host window creation failing (`CreateWindowExW` + `GetLastError()`)
- `IPlugView::attached(HWND)` failing or timing out

### Pre-show the host HWNDs before `attached()`
If a plug-in reaches `createView`, accepts `kPlatformTypeHWND`, and still hangs inside `IPlugView::attached(HWND)`, one justified last-mile host tweak is to:
- explicitly `ShowWindow(...)` the frame window,
- show/focus the child client HWND,
- and force an initial redraw/update before calling `attached()`.

This is low risk because it does not change ownership, threading, or teardown rules; it only makes the Win32 host surface fully visible and paintable before the plug-in probes it during `attached()`.

### Know when to stop blaming the host
If the host already has:
- plugin load and editor operations running on the **same thread** (the thread that initialised the plugin DLL and COM/OLE),
- a Win32 message loop (`GetMessageW`) running on that thread,
- a real frame HWND plus child client HWND,
- `IPlugView::setFrame(IPlugFrame)` succeeding,
- and the pre-show/focus/redraw step above,

yet the exact target plug-in still times out inside `attached()`, treat the remaining issue as plugin-side or unsupported by the current minimal host surface unless you have concrete evidence of one missing interface/callback.

### Thread affinity: load and editor on the same thread
JUCE-based VST3 plugins (OB-Xd, Dexed, Vital, etc.) bind their `MessageManager` singleton to the thread that first loads/initialises the plugin DLL. If the editor window is created on a **different** thread, `attached()` will call `MessageManager::callFunctionOnMessageThread()`, post a message to the loading thread, and block. If the loading thread is not pumping messages (e.g., stuck in `ReadFile` on a pipe), the result is a deadlock.

**Fix pattern:** The bridge's main thread must run a Win32 message loop. Move blocking I/O (pipe reads) to a background thread that posts commands to the main thread's loop via a message-only window. All VST3 plugin loads AND editor operations happen on the main thread — ensuring thread affinity for both the plugin framework and COM/STA.

### Keep managed/UI diagnostics independent from active-backend switching
If the host delays `activeBackend` assignment until VST3 load succeeds, the UI must not use `GetActiveBackend()` as the only source of truth for editor availability. Preserve the latest VST3 diagnostic in a host-level accessor and use that for:
- inline slot status text
- "editor not available" dialogs
- retry/error flows after load faults

Also restore editor-button enabled state from the current capability check, not with an unconditional `finally { button.IsEnabled = true; }`.

## Example
- Load ACK: `supportsEditor=false`, `editorDiagnostics="Editor controller discovery: direct IEditController query failed (kNoInterface). controller class ID lookup succeeded (...). factory createInstance for controller failed (kNoInterface). editor GUI unavailable after controller discovery."`
- Open-editor failure: `"Editor open failed at isPlatformTypeSupported(HWND): kNotImplemented."`

## Anti-Patterns
- **Single fallback message for every editor failure** — hides the real cause and makes plugin support debugging slow.
- **Assuming controller lifetime is always separate** — breaks single-object plugins with duplicate init/teardown.
- **UI-only generic dialogs** — always propagate bridge diagnostics into the managed layer.
