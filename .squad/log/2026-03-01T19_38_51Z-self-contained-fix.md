# Session Log: Self-Contained Runtime Fix
**Timestamp:** 2026-03-01T19:38:51Z  
**Agent:** Jet (Windows Dev)

## Problem Statement
Windows App Runtime deployment required external installer. Ward Impe requested elimination of this dependency to enable zero-prerequisite, xcopy-deployable distribution.

## Solution
Modified `MinimalMusicKeyboard.csproj` to bundle Windows App Runtime into application output:
```xml
<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>
<SelfContained>true</SelfContained>
```

## Verification
- Build tool: MSBuild from Visual Studio 18 Insiders
- Configuration: Debug, x64
- Result: **Clean build, 0 errors**
- NETSDK1057 warning (preview SDK) is informational only

## Tradeoffs
| Aspect | Impact |
|--------|--------|
| Output size | +50–100 MB (Windows App Runtime DLLs bundled) |
| User experience | Improved — zero prerequisites, direct xcopy deployment |
| NuGet reference | Unchanged — Microsoft.WindowsAppSDK retained for build-time tooling |
| Runtime memory | No change — same DLLs loaded from app directory |

## Files Modified
- `src/MinimalMusicKeyboard/MinimalMusicKeyboard.csproj`

## Decision
Merged from `.squad/decisions/inbox/jet-self-contained-runtime.md`
