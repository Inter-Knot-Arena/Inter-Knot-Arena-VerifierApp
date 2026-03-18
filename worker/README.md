# Verifier Worker

Python worker process for OCR scan and match-time CV checks.

## Commands

- `health`
- `ocr.scan`
- `cv.precheck`
- `cv.inrun`

`cv.precheck` / `cv.inrun` payload supports:

- `expectedAgents: string[]`
- `bannedAgents: string[]`
- `detectedAgents: string[]` (optional debug override)
- `captureScreen: boolean` (defaults to `true`)
- `captureRegion: [x, y, width, height]` (optional override)

Transport: JSON-RPC over Windows named pipe.

Runtime dependencies:

- OCR package path from `IKA_OCR_SCAN_ROOT` or bundle extraction directory.
- CV package path from `IKA_CV_ROOT` or bundle extraction directory.
- Bundle root is provided by host via `IKA_BUNDLE_ROOT`.
- Capture backend: `dxcam` (DXGI) first for fullscreen support, `PIL.ImageGrab` fallback.
- OCR runtime is CUDA-only. The worker venv must provide `onnxruntime-gpu` and `torch`; `onnxruntime-directml` is not sufficient for live OCR.

Optional env vars:

- `IKA_CAPTURE_OUTPUT_IDX` - monitor index for DXGI capture (`0` by default).

## Local build

```powershell
py -3.12 -m venv .venv
.\\.venv\\Scripts\\Activate.ps1
pip install -r requirements.txt
pip install pyinstaller
pyinstaller VerifierWorker.spec
```
