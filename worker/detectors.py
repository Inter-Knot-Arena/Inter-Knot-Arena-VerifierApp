from __future__ import annotations

import importlib
import os
import sys
import tempfile
import threading
from pathlib import Path
from typing import Any, Dict, Sequence, Tuple

import cv2
import numpy as np
from PIL import ImageGrab

try:
    import dxcam  # type: ignore
except Exception:  # pragma: no cover - optional dependency
    dxcam = None

_DXCAM_LOCK = threading.Lock()
_DXCAM_CAMERA = None


def _candidate_roots() -> list[Path]:
    roots: list[Path] = []

    env_paths = [
        os.getenv("IKA_OCR_SCAN_ROOT", ""),
        os.getenv("IKA_CV_ROOT", ""),
        os.getenv("IKA_BUNDLE_ROOT", ""),
    ]
    for value in env_paths:
        if value:
            roots.append(Path(value).resolve())

    here = Path(__file__).resolve()
    roots.append(here.parents[2] / "Inter-Knot Arena OCR_Scan")
    roots.append(here.parents[2] / "Inter-Knot Arena CV")
    roots.append(here.parents[1] / "external" / "OCR_Scan")
    roots.append(here.parents[1] / "external" / "CV")
    roots.append(here.parents[1] / "worker" / "ocr_scan")
    roots.append(here.parents[1] / "worker" / "cv")

    bundle_root = os.getenv("IKA_BUNDLE_ROOT")
    if bundle_root:
        bundle = Path(bundle_root).resolve()
        roots.extend([bundle / "ocr_scan", bundle / "cv"])

    unique: list[Path] = []
    seen: set[str] = set()
    for root in roots:
        key = str(root).lower()
        if key in seen:
            continue
        seen.add(key)
        unique.append(root)
    return unique


def _bootstrap_paths() -> None:
    for root in reversed(_candidate_roots()):
        if root.exists():
            path = str(root)
            if path not in sys.path:
                sys.path.insert(0, path)


_bootstrap_paths()

_DEFAULT_PRECHECK_REGION: dict[str, tuple[int, int, int, int]] = {
    "1080p": (360, 250, 1200, 250),
    "1440p": (480, 340, 1600, 320),
}

_DEFAULT_INRUN_REGION: dict[str, tuple[int, int, int, int]] = {
    "1080p": (24, 180, 280, 620),
    "1440p": (32, 240, 360, 820),
}

_ROSTER_UID_REGION: dict[str, tuple[int, int, int, int]] = {
    "1080p": (1160, 930, 640, 130),
    "1440p": (1540, 1220, 860, 180),
}

_ROSTER_AGENT_REGIONS: dict[str, list[tuple[int, int, int, int]]] = {
    "1080p": [
        (1180, 150, 520, 220),
        (1180, 390, 520, 220),
        (1180, 630, 520, 220),
    ],
    "1440p": [
        (1560, 200, 700, 300),
        (1560, 530, 700, 300),
        (1560, 860, 700, 300),
    ],
}


def _load_ocr_runtime():
    module = importlib.import_module("scanner")
    scan_fn = getattr(module, "scan_roster")
    failure_cls = getattr(module, "ScanFailure")
    return scan_fn, failure_cls


def _load_cv_runtime():
    module = importlib.import_module("runtime.matcher")
    evaluate_fn = getattr(module, "evaluate_detection")
    return evaluate_fn


def _safe_list(values: Any) -> list[str]:
    if not isinstance(values, Sequence) or isinstance(values, (str, bytes)):
        return []
    return [str(item).strip() for item in values if str(item).strip()]


def _coerce_payload_resolution(payload: Dict[str, Any]) -> str:
    resolution = str(payload.get("resolution", "1080p")).lower().strip()
    return resolution if resolution in {"1080p", "1440p"} else "1080p"


def _coerce_payload_locale(payload: Dict[str, Any]) -> str:
    locale = str(payload.get("locale", "EN")).upper().strip()
    return locale if locale in {"RU", "EN"} else "EN"


def _capture_screen_bgr(region: tuple[int, int, int, int] | None = None) -> np.ndarray | None:
    frame = _capture_screen_dxgi(region)
    if frame is not None:
        return frame
    return _capture_screen_pil(region)


def _capture_screen_dxgi(region: tuple[int, int, int, int] | None = None) -> np.ndarray | None:
    if dxcam is None:
        return None

    camera = _get_dxcam_camera()
    if camera is None:
        return None

    capture_region = None
    if region:
        x, y, w, h = region
        capture_region = (x, y, x + w, y + h)

    try:
        frame = camera.grab(region=capture_region)
        if frame is None:
            return None
        if frame.ndim == 3 and frame.shape[2] == 4:
            return cv2.cvtColor(frame, cv2.COLOR_BGRA2BGR)
        return frame
    except Exception:
        return None


def _get_dxcam_camera():
    global _DXCAM_CAMERA
    if _DXCAM_CAMERA is not None:
        return _DXCAM_CAMERA

    with _DXCAM_LOCK:
        if _DXCAM_CAMERA is not None:
            return _DXCAM_CAMERA
        if dxcam is None:
            return None

        monitor_idx_raw = os.getenv("IKA_CAPTURE_OUTPUT_IDX", "0")
        try:
            monitor_idx = max(0, int(monitor_idx_raw))
        except Exception:
            monitor_idx = 0

        try:
            _DXCAM_CAMERA = dxcam.create(output_idx=monitor_idx, output_color="BGR")
        except Exception:
            _DXCAM_CAMERA = None
        return _DXCAM_CAMERA


def _capture_screen_pil(region: tuple[int, int, int, int] | None = None) -> np.ndarray | None:
    try:
        bbox = None
        if region:
            x, y, w, h = region
            bbox = (x, y, x + w, y + h)
        image = ImageGrab.grab(bbox=bbox, all_screens=True)
        array = np.array(image)
        if array.ndim < 3:
            return None
        if array.shape[2] == 4:
            return cv2.cvtColor(array, cv2.COLOR_BGRA2BGR)
        return cv2.cvtColor(array, cv2.COLOR_RGB2BGR)
    except Exception:
        return None


def _crop_with_box(frame: np.ndarray, box: tuple[int, int, int, int]) -> np.ndarray | None:
    x, y, w, h = box
    if w <= 0 or h <= 0:
        return None
    max_h, max_w = frame.shape[:2]
    x0 = max(0, min(x, max_w - 1))
    y0 = max(0, min(y, max_h - 1))
    x1 = max(x0 + 1, min(x + w, max_w))
    y1 = max(y0 + 1, min(y + h, max_h))
    crop = frame[y0:y1, x0:x1]
    if crop.size == 0:
        return None
    return crop


def _prepare_roster_capture_assets(session_id: str, resolution: str) -> Dict[str, Any]:
    frame = _capture_screen_bgr()
    if frame is None:
        return {}

    uid_box = _ROSTER_UID_REGION.get(resolution, _ROSTER_UID_REGION["1080p"])
    icon_boxes = _ROSTER_AGENT_REGIONS.get(resolution, _ROSTER_AGENT_REGIONS["1080p"])
    temp_root = Path(tempfile.gettempdir()) / "ika_verifier" / "roster" / session_id
    temp_root.mkdir(parents=True, exist_ok=True)

    payload: Dict[str, Any] = {}
    anchors = {"profile": False, "agents": False, "equipment": False}

    uid_crop = _crop_with_box(frame, uid_box)
    if uid_crop is not None:
        uid_path = temp_root / "uid.png"
        if cv2.imwrite(str(uid_path), uid_crop):
            payload["uidImagePath"] = str(uid_path)
            anchors["profile"] = True

    icon_paths: list[dict[str, str]] = []
    for index, box in enumerate(icon_boxes):
        crop = _crop_with_box(frame, box)
        if crop is None:
            continue
        icon_path = temp_root / f"agent_icon_{index + 1}.png"
        if cv2.imwrite(str(icon_path), crop):
            icon_paths.append({"path": str(icon_path)})

    if icon_paths:
        payload["agentIconPaths"] = icon_paths
        anchors["agents"] = len(icon_paths) >= 2
        anchors["equipment"] = len(icon_paths) >= 2

    payload["anchors"] = anchors
    return payload


def _build_roster_context(payload: Dict[str, Any]) -> tuple[Dict[str, Any], Dict[str, Any], str, str]:
    session_id = str(payload.get("sessionId", "session"))
    full_sync = bool(payload.get("fullSync", True))
    region_hint = str(payload.get("regionHint", "OTHER"))
    locale = _coerce_payload_locale(payload)
    resolution = _coerce_payload_resolution(payload)

    anchors_raw = payload.get("anchors")
    anchors = dict(anchors_raw) if isinstance(anchors_raw, dict) else {}

    uid_candidates = payload.get("uidCandidates")
    if not isinstance(uid_candidates, list):
        uid_candidates = []

    agents_raw = payload.get("agents")
    if not isinstance(agents_raw, list):
        agents_raw = []

    session_context: Dict[str, Any] = {
        "sessionId": session_id,
        "regionHint": region_hint,
        "region": payload.get("region"),
        "inputLockActive": bool(payload.get("inputLockActive", True)),
        "anchors": anchors,
        "uidCandidates": uid_candidates,
        "agents": agents_raw,
    }

    if bool(payload.get("captureScreen", True)):
        capture_payload = _prepare_roster_capture_assets(session_id=session_id, resolution=resolution)
        if "uidImagePath" in capture_payload:
            session_context["uidImagePath"] = capture_payload["uidImagePath"]
        if "agentIconPaths" in capture_payload:
            session_context["agentIconPaths"] = capture_payload["agentIconPaths"]
        if not session_context["anchors"] and isinstance(capture_payload.get("anchors"), dict):
            session_context["anchors"] = capture_payload["anchors"]

    if not isinstance(session_context["anchors"], dict) or not session_context["anchors"]:
        session_context["anchors"] = {"profile": False, "agents": False, "equipment": False}

    calibration_raw = payload.get("calibration")
    calibration = (
        calibration_raw
        if isinstance(calibration_raw, dict)
        else {"requiredAnchors": ["profile", "agents", "equipment"]}
    )
    return session_context, calibration, locale, resolution


def run_ocr_scan(payload: Dict[str, Any]) -> Dict[str, Any]:
    session_context, calibration, locale, resolution = _build_roster_context(payload)
    full_sync = bool(payload.get("fullSync", True))
    scan_roster, scan_failure_cls = _load_ocr_runtime()
    try:
        result = scan_roster(
            session_context=session_context,
            calibration=calibration,
            locale=locale,
            resolution=resolution,
        )
        result["fullSync"] = full_sync
        return result
    except scan_failure_cls as exc:
        partial_result = getattr(exc, "partial_result", None)
        if str(getattr(exc, "code", "")) == "LOW_CONFIDENCE" and isinstance(partial_result, dict):
            recovered = dict(partial_result)
            recovered["fullSync"] = full_sync
            recovered["lowConfReasons"] = list(exc.low_conf_reasons)
            recovered["resolution"] = recovered.get("resolution") or resolution
            recovered["locale"] = recovered.get("locale") or locale
            return recovered

        return {
            "uid": "",
            "region": str(session_context.get("regionHint", "OTHER")).upper(),
            "fullSync": full_sync,
            "modelVersion": "ocr-hybrid-v1.2",
            "dataVersion": "unknown",
            "scanMeta": "hybrid_deterministic_pipeline",
            "confidenceByField": {"uid": 0.0, "region": 0.0, "agents": 0.0, "equipment": 0.0},
            "agents": [],
            "lowConfReasons": list(exc.low_conf_reasons),
            "timingMs": 0.0,
            "resolution": resolution,
            "locale": locale,
            "errorCode": str(exc.code),
            "errorMessage": exc.message,
        }


def _resolve_detection_sets(payload: Dict[str, Any]) -> tuple[list[str], list[str], list[str], list[str]]:
    expected = _safe_list(payload.get("expectedAgents"))
    detected = _safe_list(payload.get("detectedAgents"))
    history = _safe_list(payload.get("historyAgents"))
    banned = _safe_list(payload.get("bannedAgents"))
    return expected, detected, history, banned


def _parse_capture_region(raw: Any) -> tuple[int, int, int, int] | None:
    if isinstance(raw, dict):
        keys = ("x", "y", "width", "height")
        if all(key in raw for key in keys):
            try:
                return (
                    int(raw["x"]),
                    int(raw["y"]),
                    int(raw["width"]),
                    int(raw["height"]),
                )
            except Exception:
                return None

    if isinstance(raw, Sequence) and not isinstance(raw, (str, bytes)) and len(raw) == 4:
        try:
            return (int(raw[0]), int(raw[1]), int(raw[2]), int(raw[3]))
        except Exception:
            return None
    return None


def _default_cv_region(mode: str, resolution: str) -> tuple[int, int, int, int]:
    if mode == "PRECHECK":
        return _DEFAULT_PRECHECK_REGION.get(resolution, _DEFAULT_PRECHECK_REGION["1080p"])
    return _DEFAULT_INRUN_REGION.get(resolution, _DEFAULT_INRUN_REGION["1080p"])


def run_precheck(payload: Dict[str, Any]) -> Dict[str, Any]:
    evaluate_detection = _load_cv_runtime()
    expected, detected, history, banned = _resolve_detection_sets(payload)
    resolution = _coerce_payload_resolution(payload)
    capture_region = _parse_capture_region(payload.get("captureRegion")) or _default_cv_region(
        mode="PRECHECK",
        resolution=resolution,
    )
    capture_screen = bool(payload.get("captureScreen", True))

    return evaluate_detection(
        expected_agents=expected,
        detected_agents=detected,
        banned_agents=banned,
        mode="PRECHECK",
        locale=_coerce_payload_locale(payload),
        resolution=resolution,
        history_agents=history,
        frame_hash_hint=str(payload.get("frameHashHint", "")).strip() or None,
        capture_region=capture_region if capture_screen else None,
        orientation="horizontal",
        capture_screen=capture_screen,
    )


def run_inrun(payload: Dict[str, Any]) -> Dict[str, Any]:
    evaluate_detection = _load_cv_runtime()
    expected, detected, history, banned = _resolve_detection_sets(payload)
    resolution = _coerce_payload_resolution(payload)
    capture_region = _parse_capture_region(payload.get("captureRegion")) or _default_cv_region(
        mode="INRUN",
        resolution=resolution,
    )
    capture_screen = bool(payload.get("captureScreen", True))

    return evaluate_detection(
        expected_agents=expected,
        detected_agents=detected,
        banned_agents=banned,
        mode="INRUN",
        locale=_coerce_payload_locale(payload),
        resolution=resolution,
        history_agents=history,
        frame_hash_hint=str(payload.get("frameHashHint", "")).strip() or None,
        capture_region=capture_region if capture_screen else None,
        orientation="vertical",
        capture_screen=capture_screen,
    )
