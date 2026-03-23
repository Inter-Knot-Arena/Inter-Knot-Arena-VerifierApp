# Inter-Knot-Arena-VerifierApp

Desktop verifier for Inter-Knot Arena with:

- OAuth loopback login.
- In-app sign-in via email/password or Google (browser).
- OCR roster import (`POST /verifier/roster/import`) with visible-slice mode and guarded multi-page full sync.
- Match precheck/in-run evidence submission.
- Hybrid stack: WPF + Python worker + C++ native module.
- Bundled OCR/CV runtime packages with SHA256 integrity checks.

## Repository layout

- `src/VerifierApp.UI` - WPF host (`VerifierApp.exe`)
- `src/VerifierApp.Core` - domain contracts + orchestrators
- `src/VerifierApp.ApiClient` - typed HTTP client for Inter-Knot Arena API
- `src/VerifierApp.Auth` - PKCE + loopback callback + DPAPI token store
- `src/VerifierApp.WorkerHost` - named pipe worker bridge + native P/Invoke
- `worker/` - Python worker (line-delimited JSON envelopes over named pipe)
- `native/ika_native` - C++ DLL bridge (`ika_native.dll`)
- `scripts/build.ps1` - build + publish pipeline

## Runtime architecture

1. UI calls API for device auth start.
2. Browser opens verifier bridge URL and completes OAuth.
3. Loopback callback receives `requestId + code`.
4. UI exchanges code for bearer verifier tokens and stores them via DPAPI.
5. `VerifierApp.exe` extracts bundled worker/native assets to `%LOCALAPPDATA%`.
6. UI extracts `VerifierWorker_bundle.zip`, starts `VerifierWorker.exe` from the extracted onedir runtime, and talks over named pipe.
7. Worker executes `ocr.scan`, `cv.precheck`, `cv.inrun`.
8. Native module provides OS-level input lock and real desktop frame hash capture.
9. Bundled OCR/CV zip packages are integrity-checked and extracted before worker startup.
10. Before OCR scan, a native pre-scan script can be executed under active input lock. The built-in live default is `ESC,ESC`, and visible-slice navigation then uses a dedicated state-recovery path to normalize into the home/menu screen before opening `Agents`.
11. Worker/CV capture path uses DXGI (`dxcam`) first for fullscreen compatibility, with safe fallback.

## Current OCR capability

- The bundled OCR worker reads UID plus the visible roster slice and can ingest richer follow-up captures (`agent_detail`, `equipment`, `amplifier_detail`, `disk_detail`) when supplied.
- The desktop app includes a guarded multi-page full-sync path. It only performs destructive overwrite when terminal-slice coverage is confirmed via `capabilities.fullRosterCoverage=true`.
- The richer visible-slice preset now captures `agent_detail`, `equipment`, `amplifier_detail`, and `disk_detail` for the three visible slots and feeds them into OCR as page-aware `screenCaptures`.

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
- Packaged live-scan tool: `artifacts/publish/win-x64/VerifierApp.LiveScan.exe`
- Bundle manifest: `artifacts/publish/win-x64/bundle.manifest.json`
- Native sidecar: `artifacts/publish/win-x64/ika_native.dll`
- OCR bundle sidecar: `artifacts/publish/win-x64/ocr_scan_bundle.zip`
- CV bundle sidecar: `artifacts/publish/win-x64/cv_bundle.zip`
- Worker bundle sidecar: `artifacts/publish/win-x64/VerifierWorker_bundle.zip`
- CUDA sidecars: `artifacts/publish/win-x64/cuda/*.dll`
- `VerifierApp.exe` still embeds OCR/CV/native assets for desktop runtime. The publish root also stages the same bundle sidecars so `VerifierApp.LiveScan.exe` and `scripts/smoke_worker.ps1 -BundleDirectory artifacts/publish/win-x64` can validate the packaged runtime directly.
- Release publish does not include `.pdb` files.

## Worker smoke

Bundled-first smoke for the staged worker/OCR/CV runtime:

```powershell
.\scripts\smoke_worker.ps1
```

Bundled smoke against the published artifact directory:

```powershell
.\scripts\smoke_worker.ps1 -BundleDirectory ".\artifacts\publish\win-x64"
```

Optional source-mode smoke against an explicit OCR checkout:

```powershell
.\scripts\smoke_worker.ps1 -Mode Source -OcrRoot "D:\Inter-Knot Arena\Inter-Knot Arena OCR_Scan"
```

The smoke gate now uses committed fixtures and asserts:

- `health` / `health.details`
- strict `ocr.scan` on a real bundled fixture
- `ocr.inspectEquipmentOverview` for an occupied weapon-only layout
- `ocr.inspectEquipmentOverview` for an empty `CORE AVAILABLE` layout
- OCR bundle provenance from `bundle.manifest.json`

Headless live OCR smoke against a running game window:

```powershell
.\scripts\smoke_live_ocr.ps1 -Locale RU -Resolution 1440p -CapturePlanPreset VISIBLE_SLICE_AGENT_DETAIL_EQUIPMENT_AMP_BETA -OutputPath "D:\tmp\live_scan.json"
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

- `IKA_SCAN_SCRIPT` - optional comma-separated pre-scan navigation script (`ESC,ESC` by default for live OCR). Supports key tokens plus `WAIT:ms`, `CLICK:x:y`, and `DBLCLICK:x:y`. If both coordinates are in `0..1`, they are treated as normalized coordinates inside the focused game window.
- `IKA_SCAN_SCRIPT_STEP_DELAY_MS` - delay between key presses for `IKA_SCAN_SCRIPT` (default `500` for the built-in `ESC,ESC` live normalize script, otherwise `120`).
 - `IKA_SCAN_SCRIPT_POST_DELAY_MS` - extra wait after the pre-scan script completes (default `1600` for the built-in `ESC,ESC` live normalize script, otherwise `550`).
- `IKA_GAME_PROCESS_NAME` - process name used when auto-focusing the game before scan (`ZenlessZoneZero` by default).
- `IKA_GAME_WINDOW_TITLE` - optional window-title hint used to prefer the correct game window.
- `IKA_GAME_FOCUS_DELAY_MS` - extra wait after the game window is focused (default `250`).
- `IKA_GAME_CAPTURE_REFOCUS_DELAY_MS` - small extra wait after each per-step re-focus during live OCR automation (default `90`).
- `IKA_ALLOW_SOFT_INPUT_LOCK` - optional fallback for unattended dev/live runs when Win32 `BlockInput()` is unavailable. The scan is marked degraded with `soft_input_lock_fallback`.
- `IKA_KEY_SCRIPT_BACKEND` - optional key-script backend override. Set to `managed`/`sendkeys` to run key-only navigation scripts through WinForms `SendKeys` instead of native `SendInput`.
- `IKA_CAPTURE_OUTPUT_IDX` - monitor index for fullscreen DXGI capture (`0` by default).
- `IKA_CAPTURE_STEP_MAX_ATTEMPTS` - maximum attempts for each live OCR automation step before aborting (`2` by default).
- `IKA_CAPTURE_STEP_RETRY_DELAY_MS` - delay between failed live OCR automation attempts (`180` by default).
- `IKA_DEFAULT_OCR_CAPTURE_PLAN` - built-in OCR capture preset. Default is `VISIBLE_SLICE_AGENT_DETAIL_V1`; set to `VISIBLE_SLICE_AGENT_DETAIL_EQUIPMENT_AMP_BETA` to enable richer `equipment/amplifier_detail/disk_detail` follow-up captures, or `OFF` to disable built-in follow-up captures.
- `IKA_VISIBLE_SLICE_ENTRY_SCRIPT` - primary live entry step used before visible-slice OCR capture (default is a click on the home-screen `Agents` icon).
- `IKA_VISIBLE_SLICE_ENTRY_STEP_DELAY_MS` - per-token delay for the visible-slice entry script (default `120`).
- `IKA_VISIBLE_SLICE_ENTRY_POST_DELAY_MS` - extra wait after the visible-slice entry script (default `1100`).
- `IKA_VISIBLE_SLICE_ENTRY_RECOVERY_SCRIPT` - recovery navigation used when the direct entry step does not work (default `ESC,ESC`).
- `IKA_VISIBLE_SLICE_ENTRY_RECOVERY_MAX_ATTEMPTS` - maximum number of recovery cycles before visible-slice OCR aborts (default `5`).
- `IKA_VISIBLE_SLICE_ENTRY_RECOVERY_STEP_DELAY_MS` - per-token delay for the recovery script (default `500`).
- `IKA_VISIBLE_SLICE_ENTRY_RECOVERY_POST_DELAY_MS` - extra wait after the recovery script (default `1600`).
- `IKA_EXTRA_SCREEN_CAPTURE_PLAN_JSON` - optional JSON array of follow-up captures executed under active input lock.
- `IKA_EXTRA_SCREEN_CAPTURE_PLAN_PATH` - optional path to the same JSON capture plan; used when the inline env var is empty.
- `IKA_VISIBLE_SLICE_INITIAL_NORMALIZE_SCRIPT` - optional script used before built-in visible-slice capture begins (default empty).
 - `IKA_VISIBLE_SLICE_INITIAL_NORMALIZE_STEP_DELAY_MS` - per-token delay for the visible-slice normalize script (default `120`).
 - `IKA_VISIBLE_SLICE_INITIAL_NORMALIZE_POST_DELAY_MS` - extra wait after the visible-slice normalize script (default `260`).
- `IKA_FULL_SYNC_MAX_PAGES` - maximum roster pages to walk during multi-page full sync (default `64`).
- `IKA_FULL_SYNC_MAX_STALLED_PAGES` - stop after this many pages add no new agents (default `3`).
- `IKA_FULL_SYNC_PAGE_ADVANCE_SCRIPT` - key script used to advance to the next roster page during full sync (default `DOWN`).
- `IKA_FULL_SYNC_PAGE_NORMALIZE_SCRIPT` - optional key script used before each page capture to stabilize the cursor (default `UP,UP`).

When no explicit `IKA_EXTRA_SCREEN_CAPTURE_PLAN_*` override is provided, the desktop app now uses the built-in `VISIBLE_SLICE_AGENT_DETAIL_V1` plan. It opens the three visible roster slots one by one, captures their `agent_detail` screens, and returns to the roster slice before the worker call. When richer follow-up captures are present, the host now prefers those `screenCaptures` over the legacy worker-side fullscreen crop path.

For live OCR bring-up there is also a richer opt-in preset, `VISIBLE_SLICE_AGENT_DETAIL_EQUIPMENT_AMP_BETA`, which extends each visible slot with `equipment`, `amplifier_detail`, and `disk_detail` captures before returning to the roster slice.

Built-in and custom follow-up capture plans now re-focus the game window before every scripted step and can retry a step when the frame hash does not change after navigation. The frame hash is now derived from the captured game window first, with a desktop fallback only when window capture fails. This is meant to reduce missed keypresses, false retries, and stray focus loss during unattended live OCR runs.

When a pre-scan script or follow-up capture plan uses pointer commands (`CLICK:` / `DBLCLICK:`), the verifier now prefers soft input lock automatically. This avoids hard `BlockInput` getting in the way of synthetic mouse automation while still forcing the game window to stay focused.

For headless live OCR validation there is a dedicated console tool. Build it first, then run the compiled `.exe`:

```powershell
dotnet build .\src\VerifierApp.LiveScan\VerifierApp.LiveScan.csproj -c Release
.\src\VerifierApp.LiveScan\bin\Release\net10.0-windows\VerifierApp.LiveScan.exe --locale RU --resolution 1080p --out .\artifacts\live_scan\latest.json
```

To validate the packaged runtime instead of the source checkout, run the published tool against the published sidecar root:

```powershell
.\artifacts\publish\win-x64\VerifierApp.LiveScan.exe --bundle-root .\artifacts\publish\win-x64 --locale RU --resolution 1440p --out .\artifacts\live_scan\packaged_latest.json
```

The tool defaults to `VISIBLE_SLICE_AGENT_DETAIL_EQUIPMENT_AMP_BETA`, `IKA_ALLOW_SOFT_INPUT_LOCK=1`, `IKA_KEY_SCRIPT_BACKEND=native`, and the built-in `ESC,ESC` live normalize script for bring-up. To tune UI navigation without running OCR end-to-end, use probe mode:

```powershell
.\src\VerifierApp.LiveScan\bin\Release\net10.0-windows\VerifierApp.LiveScan.exe --probe-script "CLICK:0.90:0.05" --probe-out-dir .\artifacts\probe\proxy_tab
```

There is also a thin wrapper script for the same flow:

```powershell
.\scripts\smoke_live_ocr.ps1 -Locale RU -Resolution 1080p -OutputPath .\artifacts\live_scan\latest.json
```

If `ZenlessZoneZero` is running as administrator, launch `VerifierApp` / `VerifierApp.LiveScan` as administrator too. Windows UIPI blocks a non-elevated verifier process from injecting live input into an elevated game window, which otherwise looks like dead `ESC` / `C` / `CLICK` automation.

`/verifier/roster/import` also accepts a verifier-linked UID fallback. If OCR cannot extract `uid` from the current screen but the authenticated verifier account is already linked to a UID, the API will use the linked UID instead of rejecting the import immediately.

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
- `expectFrameChange` defaults to `true`; set it to `false` only for custom steps that intentionally should not move the UI.
- `capture=false` executes navigation without saving a screenshot, which is useful for exit/next-agent steps.
