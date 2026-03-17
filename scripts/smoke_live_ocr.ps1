param(
    [string]$Locale = "EN",
    [string]$Resolution = "1080p",
    [string]$RegionHint = "OTHER",
    [switch]$FullSync,
    [string]$CapturePlanPreset = "VISIBLE_SLICE_AGENT_DETAIL_EQUIPMENT_AMP_BETA",
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

$arguments = @(
    "run",
    "--project", $projectPath,
    "-c", "Release",
    "--",
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

try {
    if (-not [string]::IsNullOrWhiteSpace($CapturePlanPreset)) {
        [Environment]::SetEnvironmentVariable("IKA_DEFAULT_OCR_CAPTURE_PLAN", $CapturePlanPreset, "Process")
    }
    [Environment]::SetEnvironmentVariable("IKA_ALLOW_SOFT_INPUT_LOCK", "1", "Process")
    [Environment]::SetEnvironmentVariable("IKA_KEY_SCRIPT_BACKEND", "managed", "Process")

    Push-Location $repoRoot
    try {
        dotnet @arguments
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
}
