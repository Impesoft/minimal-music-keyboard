---
name: "native-helper-versioned-deploy"
description: "Deploy a native helper exe by versioned path plus manifest so Windows file locks cannot leave a stale runtime copy active"
domain: "windows-build-deployment"
confidence: "high"
source: "manual"
---

## Context
Use this when a managed Windows app launches a native helper executable from its output directory and rebuilds can race with a still-running old helper process. The goal is to make deployment reliable even when Windows prevents overwriting the currently running `.exe`.

## Patterns

### Deploy the helper to a version-stamped path
Instead of copying every build to one fixed output path, write each fresh helper to a versioned location such as:

`bridge\{source-last-write-utc}\helper.exe`

That avoids collisions with any already-running copy of the helper.

### Write a fixed manifest beside the app
Emit a small text file with a stable name, such as `helper.path`, containing the relative path to the current versioned helper. The managed app should read that manifest first and launch the referenced binary.

This keeps runtime lookup deterministic without requiring the app to guess which helper copy is newest.

### Keep the legacy fixed-path copy as best-effort only
If other tooling expects `helper.exe` directly beside the app, keep copying there too — but treat failures as warnings, not build-stopping errors. On Windows, a running `.exe` is commonly locked against overwrite.

### Add runtime fallback scanning
If the manifest is missing or points to a deleted file, scan the versioned helper root for matching executables and pick the newest one before falling back to the legacy fixed path.

This turns manifest corruption or partial cleanup into a recoverable situation instead of a hard failure.

## Example
- Source helper: `src\mmk-vst3-bridge\build\Release\mmk-vst3-bridge.exe`
- Versioned output: `bin\x64\Debug\...\bridge\202603130913508458931\mmk-vst3-bridge.exe`
- Manifest: `bin\x64\Debug\...\mmk-vst3-bridge.path` containing `bridge\202603130913508458931\mmk-vst3-bridge.exe`

## Anti-Patterns
- **Single fixed output path only** — a running helper can lock it and leave the app on a stale native binary.
- **Treating fallback copy failure as fatal** — the build should still succeed if the versioned copy and manifest are valid.
- **Runtime hardcoding only `AppContext.BaseDirectory\\helper.exe`** — this ignores versioned deployments and reintroduces stale-copy bugs.
