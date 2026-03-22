param(
    [ValidateSet("Bundled", "Source")]
    [string]$Mode = "Bundled",
    [string]$BundleDirectory = "",
    [string]$FixtureManifest = "",
    [string]$OcrRoot = "",
    [string]$WorkerPython = "",
    [switch]$KeepRuntime
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$helperPath = Join-Path $PSScriptRoot "smoke_worker.py"

function Resolve-PythonExecutable {
    param([string]$ExplicitPython)

    $candidates = @(
        $ExplicitPython,
        (Join-Path $root "worker\\.venv\\Scripts\\python.exe"),
        "python"
    )

    foreach ($candidate in $candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) {
            continue
        }

        if ($candidate -eq "python") {
            return $candidate
        }

        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw "Could not resolve a python executable for smoke_worker.py."
}

$pythonExe = Resolve-PythonExecutable -ExplicitPython $WorkerPython
$arguments = @(
    $helperPath,
    "--mode", $Mode
)
if (-not [string]::IsNullOrWhiteSpace($BundleDirectory)) {
    $arguments += @("--bundle-dir", $BundleDirectory)
}
if (-not [string]::IsNullOrWhiteSpace($FixtureManifest)) {
    $arguments += @("--fixture-manifest", $FixtureManifest)
}
if (-not [string]::IsNullOrWhiteSpace($OcrRoot)) {
    $arguments += @("--ocr-root", $OcrRoot)
}
if ($KeepRuntime) {
    $arguments += "--keep-runtime"
}

& $pythonExe @arguments
if ($LASTEXITCODE -ne 0) {
    throw "smoke_worker.py failed with exit code $LASTEXITCODE."
}
