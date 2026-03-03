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
$bundleDir = Join-Path $root "src\\VerifierApp.UI\\Bundled"
$workerExePath = Join-Path $workerDir "dist\\VerifierWorker.exe"
$nativeDllPath = Join-Path $nativeBuildDir "ika_native.dll"

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

Write-Host "==> Staging bundled assets into UI project"
New-Item -ItemType Directory -Force -Path $bundleDir | Out-Null
Copy-Item $workerExePath -Destination (Join-Path $bundleDir "VerifierWorker.exe") -Force
Copy-Item $nativeDllPath -Destination (Join-Path $bundleDir "ika_native.dll") -Force

Write-Host "==> Publishing single-file WPF host (VerifierApp.exe)"
dotnet publish (Join-Path $root "src\\VerifierApp.UI\\VerifierApp.UI.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:ApiOrigin=$ApiOrigin `
    -o $artifactRoot

Write-Host "Build output: $artifactRoot"
Write-Host "Primary artifact: $(Join-Path $artifactRoot 'VerifierApp.exe')"
