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

## Example
- Load ACK: `supportsEditor=false`, `editorDiagnostics="Editor controller discovery: direct IEditController query failed (kNoInterface). controller class ID lookup succeeded (...). factory createInstance for controller failed (kNoInterface). editor GUI unavailable after controller discovery."`
- Open-editor failure: `"Editor open failed at isPlatformTypeSupported(HWND): kNotImplemented."`

## Anti-Patterns
- **Single fallback message for every editor failure** — hides the real cause and makes plugin support debugging slow.
- **Assuming controller lifetime is always separate** — breaks single-object plugins with duplicate init/teardown.
- **UI-only generic dialogs** — always propagate bridge diagnostics into the managed layer.
