# VerifierApp Architecture (Draft)

## Modules

1. Auth Client
- Launches OAuth/device flow and stores short-lived verifier token.

2. Scan Orchestrator
- Drives guided scan steps and enforces locked workflow.

3. Frame Capture
- Captures window frames with fixed cadence and low overhead.

4. OCR Adapter
- Calls OCR model from OCR_Scan repo and normalizes output.

5. Match Monitor
- Uses CV model from CV repo for pre-check and in-run checks.

6. API Sync
- Sends payloads to web API endpoints.

## Performance budget (initial)

- Idle CPU: < 2%
- Match monitor CPU: < 8% on i5 7th gen
- RAM baseline: < 300 MB
- OCR burst mode RAM: < 700 MB

## Security notes

- No process injection, no memory reading from game process.
- Sign all verifier payloads with session nonce/challenge.
- Store only required crops/metadata according to server policy.
