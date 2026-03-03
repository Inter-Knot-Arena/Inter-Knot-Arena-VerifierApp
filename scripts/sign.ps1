param(
    [Parameter(Mandatory = $true)][string]$FilePath,
    [string]$CertPath = $env:IKA_CODESIGN_CERT_PATH,
    [string]$CertPassword = $env:IKA_CODESIGN_CERT_PASSWORD
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($CertPath) -or -not (Test-Path $CertPath)) {
    throw "Code-signing certificate not found. Set IKA_CODESIGN_CERT_PATH."
}

if ([string]::IsNullOrWhiteSpace($CertPassword)) {
    throw "IKA_CODESIGN_CERT_PASSWORD is required."
}

$signtool = Get-Command signtool.exe -ErrorAction Stop
& $signtool.Source sign /f $CertPath /p $CertPassword /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $FilePath

Write-Host "Signed: $FilePath"
