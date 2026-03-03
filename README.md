# Inter-Knot-Arena-VerifierApp

Desktop verifier for Inter-Knot Arena with:

- OAuth loopback login.
- Full OCR roster scan import (`POST /verifier/roster/import`).
- Match precheck/in-run evidence submission.
- Hybrid stack: WPF + Python worker + C++ native module.

## Repository layout

- `src/VerifierApp.UI` - WPF host (`VerifierApp.exe`)
- `src/VerifierApp.Core` - domain contracts + orchestrators
- `src/VerifierApp.ApiClient` - typed HTTP client for Inter-Knot Arena API
- `src/VerifierApp.Auth` - PKCE + loopback callback + DPAPI token store
- `src/VerifierApp.WorkerHost` - named pipe worker bridge + native P/Invoke
- `worker/` - Python worker (JSON-RPC over named pipe)
- `native/ika_native` - C++ DLL bridge (`ika_native.dll`)
- `packaging/VerifierApp.iss` - Inno Setup installer
- `scripts/build.ps1` - build + publish pipeline

## Runtime architecture

1. UI calls API for device auth start.
2. Browser opens verifier bridge URL and completes OAuth.
3. Loopback callback receives `requestId + code`.
4. UI exchanges code for bearer verifier tokens and stores them via DPAPI.
5. UI starts `VerifierWorker.exe` and talks over named pipe.
6. Worker executes `ocr.scan`, `cv.precheck`, `cv.inrun`.
7. Native module provides fast OS-level input lock and frame hash primitives.

## Build prerequisites

- .NET SDK 8.0+
- Visual Studio Build Tools (MSVC v143)
- CMake 3.24+
- Python 3.12+
- Inno Setup 6+

## Build

```powershell
Set-Location "Inter-Knot Arena VerifierApp"
.\scripts\build.ps1 -Configuration Release -Runtime win-x64 -ApiOrigin "http://localhost:4000"
```

Output:

- Host publish: `artifacts/publish/win-x64/VerifierApp.exe`
- Worker: `artifacts/publish/win-x64/VerifierWorker.exe`
- Native DLL: `artifacts/publish/win-x64/ika_native.dll`

## Installer build

Open `packaging/VerifierApp.iss` in Inno Setup and compile.

Installer output:

- `artifacts/installer/Inter-Knot-Arena-VerifierApp-Setup.exe`

## Code signing

```powershell
.\scripts\sign.ps1 -FilePath ".\artifacts\installer\Inter-Knot-Arena-VerifierApp-Setup.exe"
```

Env vars required:

- `IKA_CODESIGN_CERT_PATH`
- `IKA_CODESIGN_CERT_PASSWORD`

## Related repositories

- OCR package: `Inter-Knot-Arena-OCR_Scan`
- CV package: `Inter-Knot-Arena-CV`
