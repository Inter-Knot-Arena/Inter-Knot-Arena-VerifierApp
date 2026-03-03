param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$ApiOrigin = "http://localhost:4000"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $root "artifacts\\publish\\$Runtime"
$nativeBuildDir = Join-Path $root "native\\ika_native\\build\\release"
$workerDir = Join-Path $root "worker"

Write-Host "==> Building native module (ika_native.dll)"
cmake --preset windows-msvc-release -S $root
cmake --build --preset windows-msvc-release

Write-Host "==> Building python worker (VerifierWorker.exe)"
Push-Location $workerDir
py -3.12 -m venv .venv
.\\.venv\\Scripts\\python -m pip install --upgrade pip
.\\.venv\\Scripts\\python -m pip install -r requirements.txt
.\\.venv\\Scripts\\python -m pip install pyinstaller
.\\.venv\\Scripts\\pyinstaller VerifierWorker.spec --noconfirm --clean
Pop-Location

Write-Host "==> Publishing WPF host (VerifierApp.exe)"
dotnet publish (Join-Path $root "src\\VerifierApp.UI\\VerifierApp.UI.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:ApiOrigin=$ApiOrigin `
    -o $artifactRoot

Write-Host "==> Collecting worker + native binaries"
Copy-Item (Join-Path $workerDir "dist\\VerifierWorker.exe") -Destination (Join-Path $artifactRoot "VerifierWorker.exe") -Force
Copy-Item (Join-Path $nativeBuildDir "ika_native.dll") -Destination (Join-Path $artifactRoot "ika_native.dll") -Force

Write-Host "Build output: $artifactRoot"
