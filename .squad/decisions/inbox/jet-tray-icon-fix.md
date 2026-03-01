# Decision: Tray Icon Visibility Fix — ForceCreate + IconSource + AppIcon.png

**Author:** Jet (Windows Dev)
**Date:** 2026-03-01
**Requested by:** Ward Impe
**Status:** Ready for Scribe merge

---

## Problem

App started successfully but the system tray icon was completely invisible — not in the taskbar, not in the notification area overflow, nowhere. Two bugs combined to cause this.

---

## Root Causes

### Bug 1: No `IconSource` set on `TaskbarIcon`

`TrayIconService.Initialize()` constructed the `TaskbarIcon` without setting `IconSource`. Windows silently discards tray icons that have no image. The property was left as a comment placeholder in the original scaffold.

### Bug 2: `ForceCreate()` not called

H.NotifyIcon.WinUI v2.x requires `TaskbarIcon.ForceCreate(bool)` to be called when the icon is created programmatically (i.e. `new TaskbarIcon { ... }` in C# code). When the icon is defined in XAML resources, the library's XAML infrastructure calls `ForceCreate` automatically. For programmatic creation — which is the pattern used in this app — the caller must invoke it manually. Without it, the icon is constructed in memory but never registered with the Windows shell.

The `bool` parameter controls **Efficiency Mode**: `true` means the app runs hidden (no visible icon, no taskbar entry). This is the opposite of what we want. We pass `false`.

### Contributing: No `Assets\` folder

The WinUI3 scaffold was created without default template assets. There was no `Assets\` folder and no icon file to reference.

---

## Fixes

### 1. Created `src\MinimalMusicKeyboard\Assets\AppIcon.png`

A minimal 32×32 PNG icon generated programmatically via `System.Drawing`. Simple music-themed visual (blue background, white note shape).

### 2. Added explicit Content item in `.csproj`

```xml
<Content Include="Assets\AppIcon.png">
  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
</Content>
```

Required so the file copies to the output directory and is accessible via `ms-appx:///Assets/AppIcon.png` in an unpackaged WinUI3 app. The WinUI3 SDK does not auto-glob this folder for unpackaged deployments.

### 3. Set `IconSource` in `TrayIconService.Initialize()`

```csharp
IconSource = new BitmapImage(new Uri("ms-appx:///Assets/AppIcon.png")),
```

`IconSource` is typed as `ImageSource`. The correct concrete type for URI-based images is `BitmapImage` (not `BitmapIcon`, which is a different XAML control). Added `using Microsoft.UI.Xaml.Media.Imaging;`.

### 4. Called `ForceCreate(false)` after full configuration

```csharp
_taskbarIcon.ForceCreate(false);
```

Called after `ContextFlyout` is set so the icon is fully configured before shell registration. `false` = do not use Efficiency Mode = icon is visible.

---

## Files Changed

| File | Change |
|------|--------|
| `src\MinimalMusicKeyboard\Assets\AppIcon.png` | **Created** — 32×32 PNG app icon |
| `src\MinimalMusicKeyboard\MinimalMusicKeyboard.csproj` | Added `<Content>` item for `Assets\AppIcon.png` |
| `src\MinimalMusicKeyboard\Services\TrayIconService.cs` | Set `IconSource`, called `ForceCreate(false)`, added `using` |

---

## Build Verification

- Tool: MSBuild from VS 18 Insiders
- Configuration: Debug, x64
- Result: **0 errors**
- `Assets\AppIcon.png` confirmed present in `bin\x64\Debug\...\win-x64\Assets\` output directory
- NETSDK1057 (preview SDK notice) is informational only

---

## Architecture Note

The programmatic `new TaskbarIcon { ... }` + `ForceCreate(false)` pattern is the correct approach for this app (as specified). No change to the XAML-resource pattern. `ForceCreate` is the documented path for programmatic creation in H.NotifyIcon.WinUI v2.x.
