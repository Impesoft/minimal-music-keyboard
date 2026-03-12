# Faye: VST3 editor diagnostics and shared-controller lifetime

- **Date:** 2026-03-12
- **Status:** Implemented
- **Scope:** `src/mmk-vst3-bridge/src/audio_renderer.*`, `src/mmk-vst3-bridge/src/bridge.cpp`, `src/MinimalMusicKeyboard/Services/Vst3BridgeBackend.cs`, `src/MinimalMusicKeyboard/Views/SettingsWindow.xaml.cs`

## Decision
Propagate VST3 editor availability as structured load-time diagnostics instead of collapsing every failure into `"No IEditController — plugin does not support GUI."`.

## Why
Editor bring-up has multiple independent failure stages: direct controller query, separate controller class lookup, factory instantiation, controller initialization, `createView`, HWND support, Win32 host window creation, and `IPlugView::attached()`.
Without stage-specific messages, troubleshooting plugin-specific GUI issues (like OB-Xd) is guesswork.

## Coupled bug fixed
When a plugin exposes `IComponent` and `IEditController` on the same COM object, the bridge now skips duplicate `initialize()` / `terminate()` calls and avoids self-connecting `IConnectionPoint`s.

## Result
The bridge now returns `supportsEditor` + `editorDiagnostics` in `load_ack`, the managed backend stores that state, and Settings shows the exact reason when the editor is unavailable.
