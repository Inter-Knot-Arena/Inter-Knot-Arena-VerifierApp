# Verifier Worker

Python worker process for OCR scan and match-time CV checks.

## Commands

- `health`
- `ocr.scan`
- `cv.precheck`
- `cv.inrun`

Transport: JSON-RPC over Windows named pipe.

## Local build

```powershell
py -3.12 -m venv .venv
.\\.venv\\Scripts\\Activate.ps1
pip install -r requirements.txt
pip install pyinstaller
pyinstaller VerifierWorker.spec
```
