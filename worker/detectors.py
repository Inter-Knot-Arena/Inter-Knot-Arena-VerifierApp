from __future__ import annotations

import hashlib
import importlib
import os
import sys
from pathlib import Path
from typing import Any, Dict, Iterable, List, Sequence


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
    for root in _candidate_roots():
        if root.exists():
            path = str(root)
            if path not in sys.path:
                sys.path.insert(0, path)


_bootstrap_paths()

_AGENT_POOL = [
    "agent_anby",
    "agent_nicole",
    "agent_ellen",
    "agent_koleda",
    "agent_lycaon",
    "agent_vivian",
]


def _load_ocr_runtime():
    module = importlib.import_module("scanner")
    scan_fn = getattr(module, "scan_roster")
    failure_cls = getattr(module, "ScanFailure")
    return scan_fn, failure_cls


def _load_cv_runtime():
    module = importlib.import_module("runtime.matcher")
    evaluate_fn = getattr(module, "evaluate_detection")
    return evaluate_fn


def _seeded_agents(seed: str, limit: int = 3) -> list[str]:
    digest = hashlib.sha256(seed.encode("utf-8")).hexdigest()
    values = sorted(_AGENT_POOL, key=lambda item: hashlib.md5(f"{digest}:{item}".encode("utf-8")).hexdigest())
    return values[:max(1, min(limit, len(values)))]


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


def _default_roster_agents(session_id: str, full_sync: bool) -> list[dict[str, Any]]:
    selected = _seeded_agents(session_id, limit=4 if full_sync else 3)
    agents: list[dict[str, Any]] = []
    for index, agent_id in enumerate(selected):
        base_level = 50 + index * 2
        agents.append(
            {
                "agentId": agent_id,
                "level": base_level,
                "mindscape": index % 3,
                "weapon": {"weaponId": f"amp_{agent_id}", "level": base_level},
                "discs": [
                    {"slot": 1, "setId": "set_baseline", "level": 15},
                    {"slot": 2, "setId": "set_baseline", "level": 15},
                ],
            }
        )
    return agents


def _build_roster_context(payload: Dict[str, Any]) -> tuple[Dict[str, Any], Dict[str, Any], str, str]:
    session_id = str(payload.get("sessionId", "session"))
    full_sync = bool(payload.get("fullSync", True))
    region_hint = str(payload.get("regionHint", "OTHER"))
    locale = _coerce_payload_locale(payload)
    resolution = _coerce_payload_resolution(payload)

    anchors_raw = payload.get("anchors")
    anchors = anchors_raw if isinstance(anchors_raw, dict) else {"profile": True, "agents": True, "equipment": True}

    uid_candidates = payload.get("uidCandidates")
    if not isinstance(uid_candidates, list):
        uid_candidates = [hashlib.md5(session_id.encode("utf-8")).hexdigest()[:9]]

    agents_raw = payload.get("agents")
    if not isinstance(agents_raw, list):
        agents_raw = _default_roster_agents(session_id, full_sync=full_sync)

    session_context = {
        "sessionId": session_id,
        "regionHint": region_hint,
        "region": payload.get("region"),
        "inputLockActive": bool(payload.get("inputLockActive", True)),
        "anchors": anchors,
        "uidCandidates": uid_candidates,
        "agents": agents_raw,
    }

    calibration_raw = payload.get("calibration")
    calibration = calibration_raw if isinstance(calibration_raw, dict) else {"requiredAnchors": ["profile", "agents", "equipment"]}
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
        return {
            "uid": "",
            "region": str(session_context.get("regionHint", "OTHER")).upper(),
            "fullSync": full_sync,
            "modelVersion": "ocr-hybrid-v1.1",
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


def _resolve_detection_sets(payload: Dict[str, Any], mode: str) -> tuple[List[str], List[str], List[str]]:
    expected = _safe_list(payload.get("expectedAgents"))
    detected = _safe_list(payload.get("detectedAgents"))
    history = _safe_list(payload.get("historyAgents"))
    match_id = str(payload.get("matchId", "match"))

    if not expected:
        expected = _seeded_agents(f"expected:{match_id}", 3)
    if not detected:
        if mode == "PRECHECK":
            detected = expected
        else:
            detected = _seeded_agents(f"detected:{match_id}", 3)
    return expected, detected, history


def run_precheck(payload: Dict[str, Any]) -> Dict[str, Any]:
    evaluate_detection = _load_cv_runtime()
    expected, detected, history = _resolve_detection_sets(payload, mode="PRECHECK")
    return evaluate_detection(
        expected_agents=expected,
        detected_agents=detected,
        mode="PRECHECK",
        locale=_coerce_payload_locale(payload),
        resolution=_coerce_payload_resolution(payload),
        history_agents=history,
        frame_hash_hint=str(payload.get("frameHashHint", "")).strip() or None,
    )


def run_inrun(payload: Dict[str, Any]) -> Dict[str, Any]:
    evaluate_detection = _load_cv_runtime()
    expected, detected, history = _resolve_detection_sets(payload, mode="INRUN")
    return evaluate_detection(
        expected_agents=expected,
        detected_agents=detected,
        mode="INRUN",
        locale=_coerce_payload_locale(payload),
        resolution=_coerce_payload_resolution(payload),
        history_agents=history,
        frame_hash_hint=str(payload.get("frameHashHint", "")).strip() or None,
    )
