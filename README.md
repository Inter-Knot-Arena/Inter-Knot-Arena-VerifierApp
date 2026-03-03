# Inter-Knot-Arena-VerifierApp

Desktop verifier for Inter-Knot Arena.

## Goals

- Sign in with Inter-Knot Arena account.
- Run full OCR scan of ZZZ account data (UID, agents, discs, amplifiers).
- Upload verifier payload to web API (`POST /verifier/roster/import`).
- Run draft pre-check and in-run checks for match verification.

## Product constraints

- Primary target: Windows 10 64-bit+.
- Minimal extra requirements for player machine.
- Low CPU/GPU footprint while idle and during monitoring.
- User should not manually edit scanned data.

## Planned stack (v1)

- Runtime: .NET 8 (self-contained publish).
- UI: WPF.
- Capture: DXGI Desktop Duplication.
- OCR/CV inferencing: local ONNX Runtime models.
- Transport: HTTPS + signed session token from web API.

## Repositories used by VerifierApp

- OCR model/data: `Inter-Knot-Arena-OCR_Scan`
- In-run CV model/data: `Inter-Knot-Arena-CV`

## Current status

Scaffold initialized. Next implementation step is auth flow + capture pipeline skeleton.
