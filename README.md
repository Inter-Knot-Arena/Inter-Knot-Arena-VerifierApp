# Inter-Knot-Arena-VerifierApp

Desktop verifier for Inter-Knot Arena with:

- OAuth loopback login.
- In-app sign-in via email/password or Google (browser).
- Full OCR roster scan import (`POST /verifier/roster/import`).
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
