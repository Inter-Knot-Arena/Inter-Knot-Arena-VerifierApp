param(
    [string]$Locale = "EN",
    [string]$Resolution = "1080p",
    [string]$RegionHint = "OTHER",
    [switch]$FullSync,
    [string]$CapturePlanPreset = "VISIBLE_SLICE_AGENT_DETAIL_EQUIPMENT_AMP_BETA",
    [string]$ScanScript = "",
    [string]$NormalizeScript = "",
    [string]$OutputPath = "",
    [string]$ProbeScript = "",
    [string]$ProbeOutDir = "",
    [int]$ProbeStepDelayMs = 120,
    [int]$ProbePostDelayMs = 500,
    [int]$WorkerStartupDelayMs = 350
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "src\VerifierApp.LiveScan\VerifierApp.LiveScan.csproj"
if (-not (Test-Path $projectPath)) {
    throw "Live scan project not found: $projectPath"
}
$exePath = Join-Path $repoRoot "src\VerifierApp.LiveScan\bin\Release\net10.0-windows\VerifierApp.LiveScan.exe"

Push-Location $repoRoot
try {
    dotnet build $projectPath -c Release | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to build VerifierApp.LiveScan."
    }
}
finally {
    Pop-Location
}

if (-not (Test-Path $exePath)) {
    throw "Live scan executable not found after build: $exePath"
}

$arguments = @(
    "--region", $RegionHint,
    "--locale", $Locale,
    "--resolution", $Resolution,
    "--worker-startup-delay-ms", $WorkerStartupDelayMs
)

if ($FullSync) {
    $arguments += "--full-sync"
}
if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $arguments += @("--out", $OutputPath)
}
if (-not [string]::IsNullOrWhiteSpace($ProbeScript)) {
    $arguments += @("--probe-script", $ProbeScript)
}
if (-not [string]::IsNullOrWhiteSpace($ProbeOutDir)) {
    $arguments += @("--probe-out-dir", $ProbeOutDir)
}
if ($ProbeStepDelayMs -gt 0) {
    $arguments += @("--probe-step-delay-ms", $ProbeStepDelayMs)
}
if ($ProbePostDelayMs -ge 0) {
    $arguments += @("--probe-post-delay-ms", $ProbePostDelayMs)
}

$previousPreset = [Environment]::GetEnvironmentVariable("IKA_DEFAULT_OCR_CAPTURE_PLAN", "Process")
$previousSoftLock = [Environment]::GetEnvironmentVariable("IKA_ALLOW_SOFT_INPUT_LOCK", "Process")
$previousKeyBackend = [Environment]::GetEnvironmentVariable("IKA_KEY_SCRIPT_BACKEND", "Process")
$previousScanScript = [Environment]::GetEnvironmentVariable("IKA_SCAN_SCRIPT", "Process")
$previousNormalizeScript = [Environment]::GetEnvironmentVariable("IKA_VISIBLE_SLICE_INITIAL_NORMALIZE_SCRIPT", "Process")

try {
    if (-not [string]::IsNullOrWhiteSpace($CapturePlanPreset)) {
        [Environment]::SetEnvironmentVariable("IKA_DEFAULT_OCR_CAPTURE_PLAN", $CapturePlanPreset, "Process")
    }
    if ($null -ne $ScanScript) {
        [Environment]::SetEnvironmentVariable("IKA_SCAN_SCRIPT", $ScanScript, "Process")
    }
    if ($null -ne $NormalizeScript) {
        [Environment]::SetEnvironmentVariable("IKA_VISIBLE_SLICE_INITIAL_NORMALIZE_SCRIPT", $NormalizeScript, "Process")
    }
    [Environment]::SetEnvironmentVariable("IKA_ALLOW_SOFT_INPUT_LOCK", "1", "Process")
    [Environment]::SetEnvironmentVariable("IKA_KEY_SCRIPT_BACKEND", "native", "Process")

    Push-Location $repoRoot
    try {
        & $exePath @arguments
        if ($LASTEXITCODE -ne 0) {
            throw "Live OCR smoke failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }
}
finally {
    [Environment]::SetEnvironmentVariable("IKA_DEFAULT_OCR_CAPTURE_PLAN", $previousPreset, "Process")
    [Environment]::SetEnvironmentVariable("IKA_ALLOW_SOFT_INPUT_LOCK", $previousSoftLock, "Process")
    [Environment]::SetEnvironmentVariable("IKA_KEY_SCRIPT_BACKEND", $previousKeyBackend, "Process")
    [Environment]::SetEnvironmentVariable("IKA_SCAN_SCRIPT", $previousScanScript, "Process")
    [Environment]::SetEnvironmentVariable("IKA_VISIBLE_SLICE_INITIAL_NORMALIZE_SCRIPT", $previousNormalizeScript, "Process")
}
