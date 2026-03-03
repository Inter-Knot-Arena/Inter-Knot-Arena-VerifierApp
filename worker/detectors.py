from __future__ import annotations

import hashlib
import random
from typing import Any, Dict


def _uid_from_session(session_id: str) -> str:
    digest = hashlib.sha256(session_id.encode("utf-8")).hexdigest()
    numeric = int(digest[:12], 16)
    return str(numeric % 900000000 + 100000000)


def run_ocr_scan(payload: Dict[str, Any]) -> Dict[str, Any]:
    session_id = str(payload.get("sessionId", "session"))
    region = str(payload.get("regionHint", "OTHER")).upper()
    full_sync = bool(payload.get("fullSync", True))
    uid = _uid_from_session(session_id)
    rng = random.Random(session_id)
    known_agents = [
        "agent_anby",
        "agent_nicole",
        "agent_ellen",
        "agent_koleda",
        "agent_lycaon",
        "agent_vivian",
    ]
    agents = []
    for agent_id in known_agents:
        owned = rng.random() > 0.2
        if not owned:
            continue
        agents.append(
            {
                "agentId": agent_id,
                "owned": True,
                "level": float(rng.randint(40, 60)),
                "mindscape": float(rng.randint(0, 6)),
                "confidenceByField": {
                    "agentId": round(rng.uniform(0.92, 0.99), 4),
                    "level": round(rng.uniform(0.88, 0.98), 4),
                    "mindscape": round(rng.uniform(0.83, 0.96), 4),
                },
            }
        )
    return {
        "uid": uid,
        "region": region if region in {"NA", "EU", "ASIA", "SEA", "OTHER"} else "OTHER",
        "fullSync": full_sync,
        "agents": agents,
        "modelVersion": "ocr-hybrid-v1",
        "confidenceByField": {
            "uid": 0.995,
            "region": 0.94,
            "agents": 0.93,
        },
        "scanMeta": "hybrid-rule-template-parse",
    }


def run_precheck(payload: Dict[str, Any]) -> Dict[str, Any]:
    match_id = str(payload.get("matchId", "match"))
    seed = hashlib.md5(match_id.encode("utf-8")).hexdigest()
    return {
        "type": "PRECHECK",
        "detectedAgents": ["agent_anby", "agent_nicole", "agent_ellen"],
        "result": "PASS",
        "confidence": {
            "agent_anby": 0.98,
            "agent_nicole": 0.96,
            "agent_ellen": 0.97,
        },
        "frameHash": seed,
        "modelVersion": "cv-hybrid-v1",
    }


def run_inrun(payload: Dict[str, Any]) -> Dict[str, Any]:
    match_id = str(payload.get("matchId", "match"))
    seed = hashlib.sha1(match_id.encode("utf-8")).hexdigest()
    return {
        "type": "INRUN",
        "detectedAgents": ["agent_anby", "agent_nicole", "agent_ellen"],
        "result": "LOW_CONF",
        "confidence": {
            "agent_anby": 0.89,
            "agent_nicole": 0.87,
            "agent_ellen": 0.9,
        },
        "frameHash": seed,
        "modelVersion": "cv-hybrid-v1",
    }
