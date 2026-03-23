from __future__ import annotations

import argparse
import io
import json
import os
import shutil
import subprocess
import sys
import time
import uuid
import zipfile
from pathlib import Path
from typing import Any


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Bundled-first smoke gate for VerifierApp worker.")
    parser.add_argument("--mode", choices=("Bundled", "Source"), default="Bundled")
    parser.add_argument("--bundle-dir", default="")
    parser.add_argument("--fixture-manifest", default="")
    parser.add_argument("--ocr-root", default="")
    parser.add_argument("--keep-runtime", action="store_true", default=False)
    return parser.parse_args()


def resolve_existing_path(label: str, candidates: list[str], *, directory: bool) -> Path:
    for candidate in candidates:
        if not candidate:
            continue
        path = Path(candidate).expanduser()
        if path.is_dir() if directory else path.is_file():
            return path.resolve()
    raise RuntimeError(f"{label} not found. Checked: {', '.join(candidates)}")


def resolve_fixture_path(base_dir: Path, candidate: str) -> Path:
    if not candidate:
        raise RuntimeError("Fixture path is required.")
    path = Path(candidate)
    if not path.is_absolute():
        path = base_dir / path
    if not path.exists():
        raise RuntimeError(f"Fixture path does not exist: {path}")
    return path.resolve()


def get_required(mapping: dict[str, Any], key: str) -> Any:
    if key not in mapping:
        raise RuntimeError(f"Missing required key '{key}' in smoke configuration.")
    return mapping[key]


def open_pipe(pipe_name: str, timeout_seconds: float) -> io.TextIOWrapper:
    pipe_path = rf"\\.\pipe\{pipe_name}"
    deadline = time.monotonic() + timeout_seconds
    last_error: OSError | None = None
    while time.monotonic() < deadline:
        try:
            raw = open(pipe_path, "r+b", buffering=0)
            return io.TextIOWrapper(raw, encoding="utf-8", newline="\n", write_through=True)
        except OSError as exc:
            last_error = exc
            time.sleep(0.2)
    message = f"Timed out waiting for worker pipe '{pipe_name}'."
    if last_error is not None:
        message += f" Last error: {last_error}"
    raise RuntimeError(message)


def invoke_worker_request(pipe: io.TextIOWrapper, method: str, payload: dict[str, Any]) -> dict[str, Any] | bool:
    request_id = uuid.uuid4().hex
    pipe.write(json.dumps({"id": request_id, "method": method, "payload": payload}, ensure_ascii=True) + "\n")
    pipe.flush()
    raw = pipe.readline()
    if not raw:
        raise RuntimeError(f"Worker returned an empty response for {method}.")
    response = json.loads(raw)
    if response.get("id") != request_id:
        raise RuntimeError(f"Worker correlation mismatch for {method}.")
    error = response.get("error")
    if error:
        code = error.get("code", "WORKER_ERROR")
        message = error.get("message", "Unknown worker error")
        raise RuntimeError(f"Worker request failed for {method}: [{code}] {message}")
    return response.get("result")


def all_discs_empty(inspection: dict[str, Any]) -> bool:
    occupancy = inspection.get("discSlotOccupancy")
    if not isinstance(occupancy, dict):
        raise RuntimeError("Equipment inspection payload is missing discSlotOccupancy.")
    return all(occupancy.get(str(slot)) is False for slot in range(1, 7))


def extract_bundle(bundle_dir: Path, runtime_root: Path) -> dict[str, Any]:
    worker_bundle = resolve_existing_path(
        "Bundled worker bundle",
        [str(bundle_dir / "VerifierWorker_bundle.zip")],
        directory=False,
    )
    manifest_path = resolve_existing_path(
        "Bundle manifest",
        [str(bundle_dir / "bundle.manifest.json")],
        directory=False,
    )
    ocr_bundle = resolve_existing_path(
        "OCR bundle archive",
        [str(bundle_dir / "ocr_scan_bundle.zip")],
        directory=False,
    )
    cv_bundle = resolve_existing_path(
        "CV bundle archive",
        [str(bundle_dir / "cv_bundle.zip")],
        directory=False,
    )

    runtime_root.mkdir(parents=True, exist_ok=True)
    worker_root = runtime_root / "worker"
    cuda_root = runtime_root / "cuda"
    ocr_root = runtime_root / "ocr_scan"
    cv_root = runtime_root / "cv"
    bundled_cuda_root = resolve_existing_path(
        "Bundled CUDA sidecar directory",
        [str(bundle_dir / "cuda")],
        directory=True,
    )
    if cuda_root.exists():
        shutil.rmtree(cuda_root, ignore_errors=True)
    shutil.copytree(bundled_cuda_root, cuda_root)
    with zipfile.ZipFile(worker_bundle) as archive:
        archive.extractall(worker_root)
    with zipfile.ZipFile(ocr_bundle) as archive:
        archive.extractall(ocr_root)
    with zipfile.ZipFile(cv_bundle) as archive:
        archive.extractall(cv_root)
    worker_exe = resolve_existing_path(
        "Extracted worker executable",
        [str(worker_root / "VerifierWorker.exe")],
        directory=False,
    )

    return {
        "worker_bundle": worker_bundle,
        "worker_exe": worker_exe,
        "manifest_path": manifest_path,
        "manifest": json.loads(manifest_path.read_text(encoding="utf-8-sig")),
        "bundle_root": runtime_root,
        "worker_root": worker_root,
        "cuda_root": cuda_root,
        "ocr_root": ocr_root,
        "cv_root": cv_root,
    }


def build_runtime_root(repo_root: Path) -> Path:
    return (
        repo_root
        / "artifacts"
        / "smoke_worker"
        / f"{time.strftime('%Y%m%d_%H%M%S')}_{uuid.uuid4().hex}"
    )


def start_worker(
    executable: Path,
    arguments: list[str],
    *,
    cwd: Path,
    env_overrides: dict[str, str],
    stdout_log: Path,
    stderr_log: Path,
) -> tuple[subprocess.Popen[str], io.TextIOBase, io.TextIOBase]:
    env = os.environ.copy()
    env.update(env_overrides)
    creationflags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
    stdout_handle = stdout_log.open("w", encoding="utf-8")
    stderr_handle = stderr_log.open("w", encoding="utf-8")
    process = subprocess.Popen(
        [str(executable), *arguments],
        cwd=str(cwd),
        env=env,
        stdout=stdout_handle,
        stderr=stderr_handle,
        text=True,
        creationflags=creationflags,
    )
    return process, stdout_handle, stderr_handle


def assert_condition(condition: bool, message: str) -> None:
    if not condition:
        raise RuntimeError(message)


def kill_process_tree(process: subprocess.Popen[str]) -> None:
    if process.poll() is not None:
        return
    try:
        subprocess.run(
            ["taskkill", "/PID", str(process.pid), "/T", "/F"],
            check=False,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
    except Exception:
        process.kill()


def main() -> int:
    args = parse_args()
    repo_root = Path(__file__).resolve().parents[1]
    workspace_root = repo_root.parent
    fixture_manifest = resolve_existing_path(
        "Smoke fixture manifest",
        [
            args.fixture_manifest,
            str(repo_root / "tests" / "fixtures" / "bundled_smoke" / "manifest.json"),
        ],
        directory=False,
    )
    fixture_root = fixture_manifest.parent
    fixture_payload = json.loads(fixture_manifest.read_text(encoding="utf-8"))
    strict_scan = get_required(fixture_payload, "strictScan")
    equipment = get_required(fixture_payload, "equipment")

    runtime_root = build_runtime_root(repo_root)
    runtime_root.mkdir(parents=True, exist_ok=True)
    stdout_log = runtime_root / "worker.stdout.log"
    stderr_log = runtime_root / "worker.stderr.log"
    pipe_name = f"ika_smoke_{uuid.uuid4().hex}"
    keep_runtime = args.keep_runtime
    success = False

    bundle_info: dict[str, Any] | None = None
    if args.mode == "Bundled":
        bundle_dir = resolve_existing_path(
            "Bundled asset directory",
            [
                args.bundle_dir,
                str(repo_root / "src" / "VerifierApp.UI" / "Bundled"),
            ],
            directory=True,
        )
        bundle_info = extract_bundle(bundle_dir, runtime_root)
        worker_executable = bundle_info["worker_exe"]
        worker_args = ["--pipe", pipe_name]
        worker_cwd = worker_executable.parent
        env_overrides = {"IKA_BUNDLE_ROOT": str(bundle_info["bundle_root"])}
    else:
        worker_executable = resolve_existing_path(
            "Worker python",
            [
                str(repo_root / "worker" / ".venv" / "Scripts" / "python.exe"),
                sys.executable,
            ],
            directory=False,
        )
        worker_main = resolve_existing_path(
            "Worker main script",
            [str(repo_root / "worker" / "main.py")],
            directory=False,
        )
        ocr_root = resolve_existing_path(
            "OCR source root",
            [
                args.ocr_root,
                str(workspace_root / "Inter-Knot Arena OCR_Scan"),
                str(repo_root / "external" / "OCR_Scan"),
            ],
            directory=True,
        )
        worker_args = [str(worker_main), "--pipe", pipe_name]
        worker_cwd = repo_root
        env_overrides = {"IKA_OCR_SCAN_ROOT": str(ocr_root)}

    process: subprocess.Popen[str] | None = None
    stdout_handle: io.TextIOBase | None = None
    stderr_handle: io.TextIOBase | None = None
    pipe: io.TextIOWrapper | None = None

    try:
        process, stdout_handle, stderr_handle = start_worker(
            worker_executable,
            worker_args,
            cwd=worker_cwd,
            env_overrides=env_overrides,
            stdout_log=stdout_log,
            stderr_log=stderr_log,
        )

        pipe = open_pipe(pipe_name, timeout_seconds=20.0)
        health = invoke_worker_request(pipe, "health", {})
        assert_condition(health is True, "Worker health returned false.")

        health_details = invoke_worker_request(pipe, "health.details", {})
        assert_condition(isinstance(health_details, dict), "health.details returned an invalid payload.")
        assert_condition(bool(health_details.get("ok")), "Worker health.details returned ok=false.")
        ocr_runtime = health_details.get("ocrRuntime") or {}
        scanner_module_path = str(ocr_runtime.get("scannerModulePath") or "").strip()
        assert_condition(scanner_module_path, "Worker health.details did not report scannerModulePath.")
        assert_condition(
            bool(ocr_runtime.get("equipmentInspectorAvailable")),
            "Worker health.details reported no equipment inspector.",
        )
        model_probes = ocr_runtime.get("modelProbes") or {}
        for model_name in ("uid", "agent", "disk"):
            probe = model_probes.get(model_name)
            assert_condition(
                isinstance(probe, dict) and probe.get("available") is True,
                f"Bundled smoke expected OCR model probe '{model_name}' to be available.",
            )
            assert_condition(
                probe.get("cudaActive") is True,
                f"Bundled smoke expected OCR model probe '{model_name}' to use CUDA. Probe: {probe}",
            )

        if args.mode == "Bundled":
            expected_ocr_root = str(bundle_info["ocr_root"])
            assert_condition(
                scanner_module_path.lower().startswith(expected_ocr_root.lower()),
                f"Bundled smoke resolved scanner module outside extracted OCR bundle: {scanner_module_path}",
            )
        else:
            expected_ocr_root = env_overrides["IKA_OCR_SCAN_ROOT"]
            assert_condition(
                scanner_module_path.lower().startswith(expected_ocr_root.lower()),
                f"Source smoke resolved scanner module outside requested OCR root: {scanner_module_path}",
            )

        weapon_only = get_required(equipment, "weaponOnlyEquipped")
        empty_core = get_required(equipment, "emptyCoreAvailable")
        weapon_only_path = resolve_fixture_path(fixture_root, get_required(weapon_only, "path"))
        empty_core_path = resolve_fixture_path(fixture_root, get_required(empty_core, "path"))

        weapon_only_inspection = invoke_worker_request(
            pipe,
            "ocr.inspectEquipmentOverview",
            {"path": str(weapon_only_path)},
        )
        assert_condition(
            isinstance(weapon_only_inspection, dict) and weapon_only_inspection.get("weaponPresent") is True,
            "Bundled smoke expected weaponOnlyEquipped to report weaponPresent=true.",
        )
        assert_condition(
            all_discs_empty(weapon_only_inspection),
            "Bundled smoke expected weaponOnlyEquipped to keep all disc slots empty.",
        )
        if weapon_only.get("expectNoLowConfReasons"):
            assert_condition(
                len(weapon_only_inspection.get("lowConfReasons") or []) == 0,
                "Bundled smoke expected no low-confidence reasons for weaponOnlyEquipped.",
            )

        empty_core_inspection = invoke_worker_request(
            pipe,
            "ocr.inspectEquipmentOverview",
            {"path": str(empty_core_path)},
        )
        assert_condition(
            isinstance(empty_core_inspection, dict) and empty_core_inspection.get("weaponPresent") is False,
            "Bundled smoke expected emptyCoreAvailable to report weaponPresent=false.",
        )
        assert_condition(
            all_discs_empty(empty_core_inspection),
            "Bundled smoke expected emptyCoreAvailable to keep all disc slots empty.",
        )
        if empty_core.get("expectNoLowConfReasons"):
            assert_condition(
                len(empty_core_inspection.get("lowConfReasons") or []) == 0,
                "Bundled smoke expected no low-confidence reasons for emptyCoreAvailable.",
            )

        strict_payload_template = get_required(strict_scan, "payload")
        strict_screen_captures: list[dict[str, Any]] = []
        for capture in strict_payload_template.get("screenCaptures") or []:
            strict_screen_captures.append(
                {
                    "role": capture["role"],
                    "path": str(resolve_fixture_path(fixture_root, capture["path"])),
                    "screenAlias": capture.get("screenAlias", ""),
                    "pageIndex": int(capture.get("pageIndex", 0)),
                }
            )

        strict_payload = {
            "sessionId": strict_payload_template["sessionId"],
            "regionHint": strict_payload_template["regionHint"],
            "inputLockActive": bool(strict_payload_template.get("inputLockActive", True)),
            "fullSync": bool(strict_payload_template.get("fullSync", False)),
            "locale": strict_scan["locale"],
            "resolution": strict_scan["resolution"],
            "anchors": strict_payload_template["anchors"],
            "uidImagePath": str(resolve_fixture_path(fixture_root, strict_payload_template["uidImagePath"])),
            "screenCaptures": strict_screen_captures,
        }
        strict_result = invoke_worker_request(pipe, "ocr.scan", strict_payload)
        assert_condition(isinstance(strict_result, dict), "Worker ocr.scan returned an invalid payload.")
        assert_condition(
            strict_result.get("uid") == strict_scan["expectedUid"],
            "Bundled smoke strict scan returned unexpected UID.",
        )
        agents = strict_result.get("agents") or []
        assert_condition(
            isinstance(agents, list) and len(agents) >= int(strict_scan["minimumAgentCount"]),
            "Bundled smoke strict scan returned too few agents.",
        )
        for agent in agents:
            assert_condition(
                bool((agent or {}).get("agentId")),
                "Bundled smoke strict scan returned an agent without agentId.",
            )

        capabilities = strict_result.get("capabilities") or {}
        for key, expected in (strict_scan.get("requiredCapabilities") or {}).items():
            assert_condition(
                capabilities.get(key) == expected,
                f"Bundled smoke strict scan capability mismatch for {key}.",
            )

        field_sources = strict_result.get("fieldSources") or {}
        for key, expected in (strict_scan.get("requiredFieldSources") or {}).items():
            assert_condition(
                field_sources.get(key) == expected,
                f"Bundled smoke strict scan field source mismatch for {key}.",
            )

        low_conf_reasons = strict_result.get("lowConfReasons") or []
        for reason in strict_scan.get("requiredLowConfReasons") or []:
            assert_condition(
                reason in low_conf_reasons,
                f"Bundled smoke strict scan is missing expected low-confidence reason '{reason}'.",
            )

        summary: dict[str, Any] = {
            "mode": args.mode,
            "fixtureManifest": str(fixture_manifest),
            "worker": {
                "executable": str(worker_executable),
                "pipeName": pipe_name,
                "scannerModulePath": scanner_module_path,
                "stdoutLog": str(stdout_log),
                "stderrLog": str(stderr_log),
            },
            "health": health_details,
            "strictScan": {
                "uid": strict_result.get("uid"),
                "agentCount": len(agents),
                "lowConfReasons": low_conf_reasons,
                "fieldSources": field_sources,
                "capabilities": capabilities,
            },
            "equipment": {
                "weaponOnlyEquipped": weapon_only_inspection,
                "emptyCoreAvailable": empty_core_inspection,
            },
        }
        if bundle_info is not None:
            ocr_source = (bundle_info["manifest"].get("sources") or {}).get("ocr") or {}
            summary["bundle"] = {
                "root": str(bundle_info["bundle_root"]),
                "manifestPath": str(bundle_info["manifest_path"]),
                "ocrSource": ocr_source.get("sourceDir"),
                "ocrSourceKind": ocr_source.get("sourceKind"),
                "ocrCommit": ocr_source.get("commit"),
            }
        else:
            summary["bundle"] = {"ocrRoot": env_overrides["IKA_OCR_SCAN_ROOT"]}

        print(json.dumps(summary, ensure_ascii=True, indent=2))
        success = True
        return 0
    finally:
        if pipe is not None:
            try:
                pipe.close()
            except Exception:
                pass
        if stdout_handle is not None:
            stdout_handle.close()
        if stderr_handle is not None:
            stderr_handle.close()
        if process is not None:
            kill_process_tree(process)
        if args.mode == "Bundled" and success and not keep_runtime:
            shutil.rmtree(runtime_root, ignore_errors=True)


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:
        print(str(exc), file=sys.stderr)
        raise SystemExit(1)
