from __future__ import annotations

import argparse
import json
import sys
from typing import Any, Dict, Tuple

import win32file
import win32pipe

from detectors import run_inrun, run_ocr_scan, run_precheck


def read_line(pipe: int) -> str | None:
    chunks: list[bytes] = []
    while True:
        try:
            _status, data = win32file.ReadFile(pipe, 1)
        except Exception:
            return None
        if not data:
            return None
        if data == b"\n":
            break
        chunks.append(data)
    return b"".join(chunks).decode("utf-8", errors="replace")


def write_line(pipe: int, payload: Dict[str, Any]) -> None:
    message = json.dumps(payload, ensure_ascii=True) + "\n"
    win32file.WriteFile(pipe, message.encode("utf-8"))


def dispatch(method: str, payload: Dict[str, Any]) -> Any:
    if method == "health":
        return True
    if method == "ocr.scan":
        return run_ocr_scan(payload)
    if method == "cv.precheck":
        return run_precheck(payload)
    if method == "cv.inrun":
        return run_inrun(payload)
    raise ValueError(f"Unknown method: {method}")


def parse_message(raw: str) -> Tuple[str | None, str, Dict[str, Any]]:
    message = json.loads(raw)
    if not isinstance(message, dict):
        raise ValueError("Request must be a JSON object.")

    method = str(message.get("method", ""))
    payload_raw = message.get("payload", {})
    payload = dict(payload_raw) if isinstance(payload_raw, dict) else {}

    request_id_raw = message.get("id")
    request_id = str(request_id_raw) if request_id_raw is not None else None
    return request_id, method, payload


def serve(pipe_name: str) -> int:
    full_name = rf"\\.\pipe\{pipe_name}"
    while True:
        pipe = win32pipe.CreateNamedPipe(
            full_name,
            win32pipe.PIPE_ACCESS_DUPLEX,
            win32pipe.PIPE_TYPE_BYTE | win32pipe.PIPE_READMODE_BYTE | win32pipe.PIPE_WAIT,
            1,
            65536,
            65536,
            0,
            None,
        )
        try:
            win32pipe.ConnectNamedPipe(pipe, None)
            while True:
                raw = read_line(pipe)
                if raw is None:
                    break
                request_id: str | None = None
                try:
                    request_id, method, payload = parse_message(raw)
                    result = dispatch(method, payload)
                    if request_id is None:
                        write_line(pipe, result)
                    else:
                        write_line(
                            pipe,
                            {
                                "id": request_id,
                                "result": result,
                                "error": None,
                            },
                        )
                except Exception as exc:
                    error_payload = {
                        "code": "WORKER_DISPATCH_ERROR",
                        "message": str(exc),
                    }
                    if request_id is None:
                        write_line(pipe, {"error": error_payload})
                    else:
                        write_line(
                            pipe,
                            {
                                "id": request_id,
                                "result": None,
                                "error": error_payload,
                            },
                        )
        finally:
            try:
                win32file.CloseHandle(pipe)
            except Exception:
                pass


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Inter-Knot Arena Verifier Worker")
    parser.add_argument("--pipe", default="ika_verifier_worker")
    return parser.parse_args()


if __name__ == "__main__":
    args = parse_args()
    sys.exit(serve(args.pipe))
