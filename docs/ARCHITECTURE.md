# VerifierApp Architecture (v1)

## Components

1. `VerifierApp.UI` (WPF)
- login, scan start, monitor start/stop, status feed.

2. `VerifierApp.ApiClient`
- desktop auth (`/auth/verifier/device/*`)
- roster import (`/verifier/roster/import`)
- match verifier session + evidence endpoints.

3. `VerifierApp.Auth`
- PKCE code verifier/challenge
- loopback callback listener
- DPAPI token persistence.

4. `VerifierApp.WorkerHost`
- named pipe line-delimited JSON client
- worker process lifecycle
- native bridge P/Invoke.

5. `worker/VerifierWorker` runtime bundle (Python onedir)
- methods: `health`, `ocr.scan`, `cv.precheck`, `cv.inrun`.

6. `native/ika_native.dll` (C++)
- `ika_native_lock_input`
- `ika_native_unlock_input`
- `ika_native_capture_frame_hash`

## Data flow

1. Start device auth from app.
2. Open browser authorize URL.
3. Receive loopback callback.
4. Exchange callback code to access/refresh tokens.
5. Extract bundled worker/native assets from `VerifierApp.exe` plus sidecar worker/CUDA payloads into `%LOCALAPPDATA%`.
6. Run roster scan -> import payload.
7. For matches, request verifier session and submit precheck/in-run evidence with HMAC signature.

## Security model

- No game memory reading.
- No DLL injection into game process.
- Bearer tokens stored encrypted via DPAPI.
- Nonce + signature required for verifier match evidence.

## Performance goals

- Idle CPU < 2%
- Monitoring CPU < 8% on i5 7th gen
- RAM baseline < 300 MB
- OCR burst RAM < 700 MB
