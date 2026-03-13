# Faye — Load-Time Editor Diagnostics Decision

## Decision
Treat missing load-time VST3 editor diagnostics as a deployment + protocol-hardening problem:
1. Fix the app's bridge copy path so the freshly built `src\mmk-vst3-bridge\build\Release\mmk-vst3-bridge.exe` is deployed beside the managed app.
2. Harden `load_ack` serialization so whenever `supportsEditor` is `false`, `editorDiagnostics` is never left blank; use the native load error if editor discovery never produced a reason.

## Why
The source bridge already had stage-specific OB-Xd diagnostics, but the app was still running an older deployed bridge exe that did not emit `supportsEditor` / `editorDiagnostics`. Managed code then legitimately fell back to the generic "Plugin editor is not available." text.

Even with deployment fixed, the native bridge should still guarantee a non-empty diagnostic for the `supportsEditor=false` path so future regressions do not collapse back to generic managed fallback text.

## Impact
- OB-Xd and similar plugins now surface a real load-time reason in inline status whenever the bridge decides the editor is unavailable.
- Managed app builds now deploy the freshly rebuilt native bridge instead of silently keeping a stale helper binary in `bin\...\win-x64`.
