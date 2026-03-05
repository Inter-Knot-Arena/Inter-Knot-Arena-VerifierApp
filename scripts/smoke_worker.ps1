param(
    [string]$Python = "python",
    [string]$Locale = "EN",
    [string]$Resolution = "1080p",
    [string]$CvPrecheckFrame = "",
    [string]$CvInrunFrame = "",
    [string]$UidImage = "",
    [string[]]$AgentIcons = @()
)

$ErrorActionPreference = "Stop"

function Invoke-PythonJson {
    param(
        [string]$WorkingDirectory,
        [string[]]$Arguments
    )

    Push-Location $WorkingDirectory
    try {
        $raw = & $Python @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "Command failed ($LASTEXITCODE): $Python $($Arguments -join ' ')"
        }
        return ($raw -join "`n") | ConvertFrom-Json
    }
    finally {
        Pop-Location
    }
}

$root = Split-Path -Parent $PSScriptRoot
$ocrRoot = Join-Path $root "external\OCR_Scan"
$cvRoot = Join-Path $root "external\CV"

if (-not (Test-Path $ocrRoot)) {
    throw "OCR submodule not found: $ocrRoot"
}
if (-not (Test-Path $cvRoot)) {
    throw "CV submodule not found: $cvRoot"
}

Write-Host "==> OCR smoke"
$ocrArgs = @("scripts/run_scan.py")
if ([string]::IsNullOrWhiteSpace($UidImage) -and $AgentIcons.Count -eq 0) {
    $ocrArgs += @("--seed", "smoke", "--region", "EU", "--full-sync")
}
else {
    $ocrArgs += @(
        "--input-lock",
        "--anchor-profile",
        "--anchor-agents",
        "--anchor-equipment",
        "--locale", $Locale,
        "--resolution", $Resolution
    )
    if (-not [string]::IsNullOrWhiteSpace($UidImage)) {
        $ocrArgs += @("--uid-image", $UidImage)
    }
    foreach ($iconPath in $AgentIcons) {
        if (-not [string]::IsNullOrWhiteSpace($iconPath)) {
            $ocrArgs += @("--agent-icon", $iconPath)
        }
    }
}
$ocrResult = Invoke-PythonJson -WorkingDirectory $ocrRoot -Arguments $ocrArgs

Write-Host "==> CV precheck smoke"
$cvPrecheckArgs = @(
    "scripts/run_match_check.py",
    "--mode", "PRECHECK",
    "--expected", "agent_anby,agent_nicole,agent_ellen",
    "--detected", "agent_anby,agent_nicole,agent_ellen",
    "--locale", $Locale,
    "--resolution", $Resolution
)
if (-not [string]::IsNullOrWhiteSpace($CvPrecheckFrame)) {
    $cvPrecheckArgs += @("--frame-path", $CvPrecheckFrame)
}
$cvPrecheckResult = Invoke-PythonJson -WorkingDirectory $cvRoot -Arguments $cvPrecheckArgs

Write-Host "==> CV inrun smoke"
$cvInrunArgs = @(
    "scripts/run_match_check.py",
    "--mode", "INRUN",
    "--expected", "agent_anby,agent_nicole,agent_ellen",
    "--detected", "agent_anby,agent_nicole,agent_ellen",
    "--history", "agent_anby,agent_nicole,agent_ellen",
    "--locale", $Locale,
    "--resolution", $Resolution
)
if (-not [string]::IsNullOrWhiteSpace($CvInrunFrame)) {
    $cvInrunArgs += @("--frame-path", $CvInrunFrame)
}
$cvInrunResult = Invoke-PythonJson -WorkingDirectory $cvRoot -Arguments $cvInrunArgs

$summary = [ordered]@{
    ocr = [ordered]@{
        modelVersion = $ocrResult.modelVersion
        dataVersion = $ocrResult.dataVersion
        uid = $ocrResult.uid
        lowConfReasons = $ocrResult.lowConfReasons
    }
    precheck = [ordered]@{
        result = $cvPrecheckResult.result
        modelVersion = $cvPrecheckResult.modelVersion
        dataVersion = $cvPrecheckResult.dataVersion
        lowConfReasons = $cvPrecheckResult.lowConfReasons
    }
    inrun = [ordered]@{
        result = $cvInrunResult.result
        modelVersion = $cvInrunResult.modelVersion
        dataVersion = $cvInrunResult.dataVersion
        lowConfReasons = $cvInrunResult.lowConfReasons
    }
}

$summary | ConvertTo-Json -Depth 6
