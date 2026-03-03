param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$ApiOrigin = "http://localhost:4000"
)

$ErrorActionPreference = "Stop"

function Invoke-ExternalStep {
    param(
        [string]$Name,
        [scriptblock]$Action
    )

    & $Action
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed with exit code $LASTEXITCODE."
    }
}

$root = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $root "artifacts\\publish\\$Runtime"
$nativeSourceDir = Join-Path $root "native\\ika_native"
$nativeBuildDir = Join-Path $root "native\\ika_native\\build\\release"
$workerDir = Join-Path $root "worker"
$bundleDir = Join-Path $root "src\\VerifierApp.UI\\Bundled"
$workerExePath = Join-Path $workerDir "dist\\VerifierWorker.exe"
$nativeDllPath = Join-Path $nativeBuildDir "ika_native.dll"
$ocrRepoDir = Join-Path $root "external\\OCR_Scan"
$cvRepoDir = Join-Path $root "external\\CV"
$ocrBundlePath = Join-Path $bundleDir "ocr_scan_bundle.zip"
$cvBundlePath = Join-Path $bundleDir "cv_bundle.zip"
$bundleManifestPath = Join-Path $bundleDir "bundle.manifest.json"

Add-Type -AssemblyName System.IO.Compression.FileSystem

function New-BundleArchive {
    param(
        [string]$SourceDir,
        [string]$DestinationZip
    )

    if (-not (Test-Path $SourceDir)) {
        throw "Bundle source directory not found: $SourceDir"
    }

    $tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("ika_bundle_" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
    try {
        Copy-Item (Join-Path $SourceDir "*") -Destination $tempRoot -Recurse -Force

        $cleanupPatterns = @(
            ".git",
            ".github",
            "__pycache__",
            ".venv"
        )
        foreach ($pattern in $cleanupPatterns) {
            Get-ChildItem -Path $tempRoot -Recurse -Force -ErrorAction SilentlyContinue | Where-Object {
                $_.Name -eq $pattern
            } | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
        }
        Get-ChildItem -Path $tempRoot -Recurse -Include *.pyc -File -ErrorAction SilentlyContinue |
            Remove-Item -Force -ErrorAction SilentlyContinue

        if (Test-Path $DestinationZip) {
            Remove-Item $DestinationZip -Force
        }
        [System.IO.Compression.ZipFile]::CreateFromDirectory(
            $tempRoot,
            $DestinationZip,
            [System.IO.Compression.CompressionLevel]::Optimal,
            $false
        )
    } finally {
        if (Test-Path $tempRoot) {
            Remove-Item $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

Write-Host "==> Building native module (ika_native.dll)"
Invoke-ExternalStep -Name "CMake configure" -Action {
    cmake -S $nativeSourceDir -B $nativeBuildDir -G Ninja "-DCMAKE_BUILD_TYPE=$Configuration" -DCMAKE_CXX_SCAN_FOR_MODULES=OFF
}
Invoke-ExternalStep -Name "CMake build" -Action {
    cmake --build $nativeBuildDir --config $Configuration
}

Write-Host "==> Building python worker (VerifierWorker.exe)"
Push-Location $workerDir
Invoke-ExternalStep -Name "Python venv" -Action {
    py -3.12 -m venv .venv
}
Invoke-ExternalStep -Name "Pip upgrade" -Action {
    .\\.venv\\Scripts\\python -m pip install --upgrade pip
}
Invoke-ExternalStep -Name "Pip requirements" -Action {
    .\\.venv\\Scripts\\python -m pip install -r requirements.txt
}
Invoke-ExternalStep -Name "Pip pyinstaller" -Action {
    .\\.venv\\Scripts\\python -m pip install pyinstaller
}
Invoke-ExternalStep -Name "PyInstaller build" -Action {
    .\\.venv\\Scripts\\pyinstaller VerifierWorker.spec --noconfirm --clean
}
Pop-Location

Write-Host "==> Staging bundled assets into UI project"
New-Item -ItemType Directory -Force -Path $bundleDir | Out-Null
Copy-Item $workerExePath -Destination (Join-Path $bundleDir "VerifierWorker.exe") -Force
Copy-Item $nativeDllPath -Destination (Join-Path $bundleDir "ika_native.dll") -Force
Write-Host "==> Building OCR/CV bundle archives"
New-BundleArchive -SourceDir $ocrRepoDir -DestinationZip $ocrBundlePath
New-BundleArchive -SourceDir $cvRepoDir -DestinationZip $cvBundlePath

Write-Host "==> Generating bundle integrity manifest"
$manifest = @{
    generatedAt = (Get-Date).ToString("o")
    sha256 = @{
        "VerifierWorker.exe" = (Get-FileHash -Algorithm SHA256 (Join-Path $bundleDir "VerifierWorker.exe")).Hash.ToLowerInvariant()
        "ika_native.dll" = (Get-FileHash -Algorithm SHA256 (Join-Path $bundleDir "ika_native.dll")).Hash.ToLowerInvariant()
        "ocr_scan_bundle.zip" = (Get-FileHash -Algorithm SHA256 $ocrBundlePath).Hash.ToLowerInvariant()
        "cv_bundle.zip" = (Get-FileHash -Algorithm SHA256 $cvBundlePath).Hash.ToLowerInvariant()
    }
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -Path $bundleManifestPath -Encoding UTF8

Write-Host "==> Publishing single-file WPF host (VerifierApp.exe)"
Invoke-ExternalStep -Name "dotnet publish" -Action {
    dotnet publish (Join-Path $root "src\\VerifierApp.UI\\VerifierApp.UI.csproj") `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        "-p:ApiOrigin=$ApiOrigin" `
        -o $artifactRoot
}

Write-Host "Build output: $artifactRoot"
Write-Host "Primary artifact: $(Join-Path $artifactRoot 'VerifierApp.exe')"
