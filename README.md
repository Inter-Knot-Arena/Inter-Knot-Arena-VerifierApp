# Inter-Knot-Arena-VerifierApp

Desktop verifier for Inter-Knot Arena with:

- OAuth loopback login.
- In-app sign-in via email/password or Google (browser).
- Visible-roster OCR import (`POST /verifier/roster/import`); full-account sync is currently blocked.
- Match precheck/in-run evidence submission.
- Hybrid stack: WPF + Python worker + C++ native module.
- Bundled OCR/CV runtime packages with SHA256 integrity checks.

## Repository layout

- `src/VerifierApp.UI` - WPF host (`VerifierApp.exe`)
- `src/VerifierApp.Core` - domain contracts + orchestrators
- `src/VerifierApp.ApiClient` - typed HTTP client for Inter-Knot Arena API
- `src/VerifierApp.Auth` - PKCE + loopback callback + DPAPI token store
- `src/VerifierApp.WorkerHost` - named pipe worker bridge + native P/Invoke
- `worker/` - Python worker (JSON-RPC over named pipe)
- `native/ika_native` - C++ DLL bridge (`ika_native.dll`)
- `scripts/build.ps1` - build + publish pipeline

## Runtime architecture

1. UI calls API for device auth start.
2. Browser opens verifier bridge URL and completes OAuth.
3. Loopback callback receives `requestId + code`.
4. UI exchanges code for bearer verifier tokens and stores them via DPAPI.
5. `VerifierApp.exe` extracts bundled worker/native assets to `%LOCALAPPDATA%`.
6. UI starts `VerifierWorker.exe` and talks over named pipe.
7. Worker executes `ocr.scan`, `cv.precheck`, `cv.inrun`.
8. Native module provides OS-level input lock and real desktop frame hash capture.
9. Bundled OCR/CV zip packages are integrity-checked and extracted before worker startup.
10. Before OCR scan, native scan script is executed (`ESC,TAB,TAB,ENTER` by default) under active input lock.
11. Worker/CV capture path uses DXGI (`dxcam`) first for fullscreen compatibility, with safe fallback.

## Current OCR limitation

- The bundled OCR worker currently reads UID plus the roster slice visible on screen.
- Full account overwrite remains disabled until the worker can navigate and capture every roster page reliably.

## Build prerequisites

- .NET SDK 10.0.x
- Visual Studio Build Tools (MSVC v143)
- CMake 3.24+
- Python 3.12+

## Build

```powershell
Set-Location "Inter-Knot Arena VerifierApp"
.\scripts\build.ps1 -Configuration Release -Runtime win-x64 -ApiOrigin "http://localhost:4000"
```

Output:

- Single-file app: `artifacts/publish/win-x64/VerifierApp.exe`
- Worker/native/OCR/CV bundles are embedded into `VerifierApp.exe` and extracted on first launch.
- Release publish does not include `.pdb` files.

## Worker smoke

Quick smoke for bundled OCR/CV submodules:

```powershell
.\scripts\smoke_worker.ps1 -Locale EN -Resolution 1080p
```

Optional snapshot-based smoke:

```powershell
.\scripts\smoke_worker.ps1 -Locale RU -Resolution 1440p -CvPrecheckFrame "D:\shots\precheck.png" -CvInrunFrame "D:\shots\inrun.png" -UidImage "D:\shots\uid.png" -AgentIcons @("D:\shots\agent1.png","D:\shots\agent2.png","D:\shots\agent3.png")
```

## Code signing

```powershell
.\scripts\sign.ps1 -FilePath ".\artifacts\publish\win-x64\VerifierApp.exe"
```

Env vars required:

- `IKA_CODESIGN_CERT_PATH`
- `IKA_CODESIGN_CERT_PASSWORD`

## Related repositories

- OCR package: `Inter-Knot-Arena-OCR_Scan`
- CV package: `Inter-Knot-Arena-CV`

## Optional runtime env vars

- `IKA_SCAN_SCRIPT` - comma-separated key sequence for pre-scan navigation (`ESC,TAB,TAB,ENTER` by default).
- `IKA_SCAN_SCRIPT_STEP_DELAY_MS` - delay between key presses for `IKA_SCAN_SCRIPT` (default `120`).
- `IKA_CAPTURE_OUTPUT_IDX` - monitor index for fullscreen DXGI capture (`0` by default).
- `IKA_DEFAULT_OCR_CAPTURE_PLAN` - built-in OCR capture preset. Default is `VISIBLE_SLICE_AGENT_DETAIL_V1`; set to `OFF` to disable built-in follow-up captures.
- `IKA_EXTRA_SCREEN_CAPTURE_PLAN_JSON` - optional JSON array of visible-slice follow-up captures executed under active input lock.
- `IKA_EXTRA_SCREEN_CAPTURE_PLAN_PATH` - optional path to the same JSON capture plan; used when the inline env var is empty.

When no explicit `IKA_EXTRA_SCREEN_CAPTURE_PLAN_*` override is provided, the desktop app now uses the built-in `VISIBLE_SLICE_AGENT_DETAIL_V1` plan. It opens the three visible roster slots one by one, captures their `agent_detail` screens, and returns to the roster slice before the worker call.

Example visible-slice capture plan:

```json
[
  {
    "role": "agent_detail",
    "script": "ENTER",
    "agentSlotIndex": 1,
    "screenAlias": "agent_1_detail",
    "stepDelayMs": 120,
    "postDelayMs": 450,
    "capture": true
  },
  {
    "role": "",
    "script": "ESC,DOWN",
    "agentSlotIndex": 1,
    "capture": false
  },
  {
    "role": "agent_detail",
    "script": "ENTER",
    "agentSlotIndex": 2,
    "screenAlias": "agent_2_detail",
    "capture": true
  }
]
```

Notes:

- `agentSlotIndex` is the 1-based visible roster slot, not a canonical `agentId`.
- `slotIndex` is reserved for disc slot captures (`1..6`) on `disk_detail`.
- `capture=false` executes navigation without saving a screenshot, which is useful for exit/next-agent steps.
