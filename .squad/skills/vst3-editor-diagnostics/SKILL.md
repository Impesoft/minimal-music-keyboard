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
