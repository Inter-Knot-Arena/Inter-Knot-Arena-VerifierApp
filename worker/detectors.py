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
_BOOTSTRAP_EXPLICIT = False
_BOOTSTRAP_CANDIDATES: list[str] = []
_BOOTSTRAP_EXISTING: list[str] = []
_DLL_DIRECTORY_HANDLES: list[Any] = []


def _register_dll_directories() -> None:
    add_dll_directory = getattr(os, "add_dll_directory", None)
    if add_dll_directory is None:
        return

    candidates: list[Path] = []
    bundle_root = os.getenv("IKA_BUNDLE_ROOT", "").strip()
    if bundle_root:
        candidates.append(Path(bundle_root).resolve() / "cuda")

    executable_dir = Path(sys.executable).resolve().parent
    candidates.append(executable_dir.parent / "cuda")
    candidates.append(executable_dir / "cuda")

    seen: set[str] = set()
    for candidate in candidates:
        key = str(candidate).lower()
        if key in seen or not candidate.is_dir():
            continue
        seen.add(key)
        try:
            handle = add_dll_directory(str(candidate))
        except OSError:
            continue
        _DLL_DIRECTORY_HANDLES.append(handle)


def _candidate_roots() -> list[Path]:
    roots: list[Path] = []
    explicit_roots: list[Path] = []

    ocr_root = os.getenv("IKA_OCR_SCAN_ROOT", "").strip()
    cv_root = os.getenv("IKA_CV_ROOT", "").strip()
    bundle_root = os.getenv("IKA_BUNDLE_ROOT", "").strip()

    if ocr_root:
        explicit_roots.append(Path(ocr_root).resolve())
    if cv_root:
        explicit_roots.append(Path(cv_root).resolve())
    if bundle_root:
        bundle = Path(bundle_root).resolve()
        explicit_roots.extend([bundle / "ocr_scan", bundle / "cv", bundle])

    if explicit_roots:
        roots.extend(explicit_roots)
        return _dedupe_roots(roots)

    here = Path(__file__).resolve()
    roots.append(here.parents[2] / "Inter-Knot Arena OCR_Scan")
    roots.append(here.parents[2] / "Inter-Knot Arena CV")
    roots.append(here.parents[1] / "external" / "OCR_Scan")
    roots.append(here.parents[1] / "external" / "CV")
    roots.append(here.parents[1] / "worker" / "ocr_scan")
    roots.append(here.parents[1] / "worker" / "cv")

    return _dedupe_roots(roots)


def _dedupe_roots(roots: Sequence[Path]) -> list[Path]:
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
    global _BOOTSTRAP_EXPLICIT, _BOOTSTRAP_CANDIDATES, _BOOTSTRAP_EXISTING
    _BOOTSTRAP_EXPLICIT = any(
        bool(os.getenv(name, "").strip())
        for name in ("IKA_OCR_SCAN_ROOT", "IKA_CV_ROOT", "IKA_BUNDLE_ROOT")
    )
    candidate_roots = _candidate_roots()
    _BOOTSTRAP_CANDIDATES = [str(root) for root in candidate_roots]
    _BOOTSTRAP_EXISTING = [str(root) for root in candidate_roots if root.exists()]
    if _BOOTSTRAP_EXPLICIT and not _BOOTSTRAP_EXISTING:
        raise RuntimeError(
            "Worker bootstrap failed: explicit OCR/CV bundle roots were provided but none of them exist."
        )
    for root in reversed(_candidate_roots()):
        if root.exists():
            path = str(root)
            if path not in sys.path:
                sys.path.insert(0, path)


_register_dll_directories()
_bootstrap_paths()

_DEFAULT_PRECHECK_REGION: dict[str, tuple[int, int, int, int]] = {
    "1080p": (360, 250, 1200, 250),
    "1440p": (480, 340, 1600, 320),
}

_DEFAULT_INRUN_REGION: dict[str, tuple[int, int, int, int]] = {
    "1080p": (24, 180, 280, 620),
    "1440p": (32, 240, 360, 820),
}

_ROSTER_AGENT_REGIONS = (
    (0.527, 0.014, 0.160, 0.194),
    (0.566, 0.222, 0.133, 0.208),
    (0.605, 0.479, 0.141, 0.278),
)


def _load_ocr_runtime():
    module = importlib.import_module("scanner")
    scan_fn = getattr(module, "scan_roster")
    failure_cls = getattr(module, "ScanFailure")
    return scan_fn, failure_cls


def _load_ocr_module():
    return importlib.import_module("scanner")


def _load_ocr_equipment_inspector():
    module = importlib.import_module("scanner")
    return getattr(module, "inspect_equipment_capture")


def _load_cv_runtime():
    module = importlib.import_module("runtime.matcher")
    evaluate_fn = getattr(module, "evaluate_detection")
    return evaluate_fn


def _module_origin(module_name: str) -> str | None:
    module = sys.modules.get(module_name)
    if module is None:
        return None
    module_path = getattr(module, "__file__", None)
    return str(module_path) if module_path else None


def get_worker_health_details() -> Dict[str, Any]:
    module = _load_ocr_module()
    scan_roster = getattr(module, "scan_roster")
    scan_failure_cls = getattr(module, "ScanFailure")
    inspect_equipment = _load_ocr_equipment_inspector()
    if not callable(scan_roster):
        raise RuntimeError("OCR runtime contract invalid: scan_roster is not callable.")
    if not callable(inspect_equipment):
        raise RuntimeError("OCR runtime contract invalid: inspect_equipment_capture is not callable.")

    model_probes: Dict[str, Dict[str, Any]] = {}
    registry = getattr(module, "ModelRegistry", None)
    if registry is not None:
        probe_specs = (
            ("uid", "has_uid_model", "uid_classifier"),
            ("agent", "has_agent_model", "agent_classifier"),
            ("disk", "has_disk_model", "disk_classifier"),
        )
        for model_name, has_name, load_name in probe_specs:
            has_model = getattr(registry, has_name, None)
            load_model = getattr(registry, load_name, None)
            if not callable(has_model) or not callable(load_model):
                continue
            if not has_model():
                model_probes[model_name] = {"available": False}
                continue
            try:
                classifier = load_model()
                session = getattr(classifier, "session", None)
                providers = list(session.get_providers()) if session is not None else []
                model_probes[model_name] = {
                    "available": True,
                    "providers": providers,
                    "cudaActive": "CUDAExecutionProvider" in providers,
                }
            except Exception as exc:
                model_probes[model_name] = {
                    "available": True,
                    "error": str(exc),
                    "cudaActive": False,
                }

    return {
        "ok": True,
        "bootstrap": {
            "explicitRoots": _BOOTSTRAP_EXPLICIT,
            "candidateRoots": list(_BOOTSTRAP_CANDIDATES),
            "resolvedRoots": list(_BOOTSTRAP_EXISTING),
        },
        "ocrRuntime": {
            "scannerModulePath": _module_origin("scanner"),
            "scanFailureType": getattr(scan_failure_cls, "__name__", str(scan_failure_cls)),
            "equipmentInspectorAvailable": True,
            "modelProbes": model_probes,
        },
    }


def worker_healthcheck() -> bool:
    get_worker_health_details()
    return True


def _safe_list(values: Any) -> list[str]:
    if not isinstance(values, Sequence) or isinstance(values, (str, bytes)):
        return []
    return [str(item).strip() for item in values if str(item).strip()]


def _coerce_payload_resolution(payload: Dict[str, Any], *, allow_auto: bool = False) -> str:
    resolution = str(payload.get("resolution", "")).lower().strip()
    if resolution in {"1080p", "1440p"}:
        return resolution
    if allow_auto and resolution in {"", "auto"}:
        return ""
    return "1080p"


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


def _crop_with_fractional_box(frame: np.ndarray, box: tuple[float, float, float, float]) -> np.ndarray | None:
    if frame.size == 0:
        return None
    height, width = frame.shape[:2]
    x = int(round(float(box[0]) * width))
    y = int(round(float(box[1]) * height))
    w = int(round(float(box[2]) * width))
    h = int(round(float(box[3]) * height))
    return _crop_with_box(frame, (x, y, w, h))


def _prepare_roster_capture_assets(session_id: str, resolution: str) -> Dict[str, Any]:
    frame = _capture_screen_bgr()
    if frame is None:
        return {}

    runtime_temp_root = str(os.environ.get("IKA_RUNTIME_TEMP_ROOT") or "").strip()
    if runtime_temp_root:
        temp_root = Path(runtime_temp_root) / "ika_verifier" / "roster" / session_id
    else:
        temp_root = Path(tempfile.gettempdir()) / "ika_verifier" / "roster" / session_id
    temp_root.mkdir(parents=True, exist_ok=True)

    payload: Dict[str, Any] = {}
    anchors = {"profile": False, "agents": False, "equipment": False}
    screen_captures: list[dict[str, Any]] = []

    roster_path = temp_root / "roster.png"
    if cv2.imwrite(str(roster_path), frame):
        screen_captures.append(
            {
                "role": "roster",
                "path": str(roster_path),
                "screenAlias": "captured_roster_screen",
            }
        )

    icon_paths: list[dict[str, str]] = []
    for index, box in enumerate(_ROSTER_AGENT_REGIONS):
        crop = _crop_with_fractional_box(frame, box)
        if crop is None:
            continue
        icon_path = temp_root / f"agent_icon_{index + 1}.png"
        if cv2.imwrite(str(icon_path), crop):
            icon_paths.append({"path": str(icon_path)})

    if icon_paths:
        payload["agentIconPaths"] = icon_paths
        anchors["agents"] = len(icon_paths) >= 2
        anchors["equipment"] = len(icon_paths) >= 2
    if screen_captures:
        anchors["agents"] = True
        anchors["equipment"] = True

    if screen_captures:
        payload["screenCaptures"] = screen_captures
    payload["anchors"] = anchors
    return payload


def _build_roster_context(payload: Dict[str, Any]) -> tuple[Dict[str, Any], Dict[str, Any], str, str]:
    session_id = str(payload.get("sessionId", "session"))
    full_sync = bool(payload.get("fullSync", False))
    region_hint = str(payload.get("regionHint", "OTHER"))
    locale = _coerce_payload_locale(payload)
    resolution = _coerce_payload_resolution(payload, allow_auto=True)

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
        "inputLockActive": bool(payload.get("inputLockActive", False)),
        "anchors": anchors,
        "uidCandidates": uid_candidates,
        "agents": agents_raw,
    }
    uid_image_path = str(payload.get("uidImagePath", "")).strip()
    if uid_image_path:
        session_context["uidImagePath"] = uid_image_path
    agent_icon_paths = payload.get("agentIconPaths")
    if isinstance(agent_icon_paths, list) and agent_icon_paths:
        session_context["agentIconPaths"] = list(agent_icon_paths)
    raw_screen_captures = payload.get("screenCaptures")
    if isinstance(raw_screen_captures, list) and raw_screen_captures:
        session_context["screenCaptures"] = list(raw_screen_captures)

    if bool(payload.get("captureScreen", True)):
        capture_payload = _prepare_roster_capture_assets(session_id=session_id, resolution=resolution)
        if "uidImagePath" in capture_payload:
            session_context["uidImagePath"] = capture_payload["uidImagePath"]
        if "agentIconPaths" in capture_payload:
            session_context["agentIconPaths"] = capture_payload["agentIconPaths"]
        captured_screen_captures = capture_payload.get("screenCaptures")
        if isinstance(captured_screen_captures, list) and captured_screen_captures:
            existing = session_context.get("screenCaptures")
            if isinstance(existing, list):
                session_context["screenCaptures"] = [*existing, *captured_screen_captures]
            else:
                session_context["screenCaptures"] = list(captured_screen_captures)
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
    full_sync = bool(payload.get("fullSync", False))
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


def inspect_equipment_overview(payload: Dict[str, Any]) -> Dict[str, Any]:
    path = str(payload.get("path", "")).strip()
    if not path:
        raise ValueError("Equipment overview path is required.")

    inspect_equipment = _load_ocr_equipment_inspector()
    result = inspect_equipment(path)
    if not isinstance(result, dict):
        raise ValueError("Equipment overview inspector returned invalid payload.")
    return result


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
