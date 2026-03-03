# Verifier Worker

Python worker process for OCR scan and match-time CV checks.

## Commands

- `health`
- `ocr.scan`
- `cv.precheck`
- `cv.inrun`

Transport: JSON-RPC over Windows named pipe.

Runtime dependencies:

- OCR package path from `IKA_OCR_SCAN_ROOT` or bundle extraction directory.
- CV package path from `IKA_CV_ROOT` or bundle extraction directory.
- Bundle root is provided by host via `IKA_BUNDLE_ROOT`.

## Local build

```powershell
py -3.12 -m venv .venv
.\\.venv\\Scripts\\Activate.ps1
pip install -r requirements.txt
pip install pyinstaller
pyinstaller VerifierWorker.spec
```
