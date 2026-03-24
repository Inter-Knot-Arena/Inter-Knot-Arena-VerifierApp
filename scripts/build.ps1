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
$workspaceRoot = Split-Path -Parent $root
$artifactRoot = Join-Path $root "artifacts\\publish\\$Runtime"
$liveScanPublishRoot = Join-Path $root "artifacts\\publish\\_live_scan\\$Runtime"
$liveScanProjectPath = Join-Path $root "src\\VerifierApp.LiveScan\\VerifierApp.LiveScan.csproj"
$nativeSourceDir = Join-Path $root "native\\ika_native"
$nativeBuildDir = Join-Path $root "native\\ika_native\\build\\release"
$workerDir = Join-Path $root "worker"
$bundleStageDir = Join-Path $root ("artifacts\\bundle_stage\\" + [Guid]::NewGuid().ToString("N"))
$bundleCudaDir = Join-Path $bundleStageDir "cuda"
$workerPyInstallerRoot = Join-Path $root ("artifacts\\worker_pyinstaller\\" + [Guid]::NewGuid().ToString("N"))
$workerDistRoot = Join-Path $workerPyInstallerRoot "dist"
$workerDistDir = Join-Path $workerDistRoot "VerifierWorker"
$workerWorkDir = Join-Path $workerPyInstallerRoot "build"
$workerBundlePath = Join-Path $bundleStageDir "VerifierWorker_bundle.zip"
$workerPythonExe = Join-Path $workerDir ".venv\\Scripts\\python.exe"
$workerPyInstallerExe = Join-Path $workerDir ".venv\\Scripts\\pyinstaller.exe"
$nativeDllPathCandidates = @(
    (Join-Path $nativeBuildDir "ika_native.dll"),
    (Join-Path $nativeBuildDir "$Configuration\\ika_native.dll"),
    (Join-Path $nativeBuildDir "Release\\ika_native.dll")
)
$ocrBundlePath = Join-Path $bundleStageDir "ocr_scan_bundle.zip"
$cvBundlePath = Join-Path $bundleStageDir "cv_bundle.zip"
$bundleManifestPath = Join-Path $bundleStageDir "bundle.manifest.json"
$inVsDevShell = -not [string]::IsNullOrWhiteSpace($env:VSCMD_VER)
$cmakeGenerator = if ($inVsDevShell) { "Ninja" } else { "Visual Studio 18 2026" }

Add-Type -AssemblyName System.IO.Compression.FileSystem

try {
    dotnet build-server shutdown | Out-Null
}
catch {
    Write-Host "    Warning: dotnet build-server shutdown failed; continuing"
}

function New-BundleArchive {
    param(
        [string]$SourceDir,
        [string]$DestinationZip
    )

    if (-not (Test-Path $SourceDir)) {
        throw "Bundle source directory not found: $SourceDir"
    }

    $cleanupPatterns = @(
        ".git",
        ".github",
        "__pycache__",
        ".venv"
    )

    if (Test-Path $DestinationZip) {
        Remove-Item $DestinationZip -Force
    }

    $sourceRoot = (Resolve-Path $SourceDir).Path
    $sourceRootWithSlash = if ($sourceRoot.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
        $sourceRoot
    }
    else {
        $sourceRoot + [System.IO.Path]::DirectorySeparatorChar
    }

    $archive = [System.IO.Compression.ZipFile]::Open(
        $DestinationZip,
        [System.IO.Compression.ZipArchiveMode]::Create
    )
    try {
        Get-ChildItem -Path $sourceRoot -Recurse -Force -File -ErrorAction SilentlyContinue | ForEach-Object {
            $file = $_
            $segments = $file.FullName.Substring($sourceRootWithSlash.Length).Split([System.IO.Path]::DirectorySeparatorChar)
            $shouldSkip = $false
            foreach ($segment in $segments) {
                if ($cleanupPatterns -contains $segment) {
                    $shouldSkip = $true
                    break
                }
            }
            if ($shouldSkip -or $file.Extension -eq ".pyc") {
                return
            }

            $entryName = [string]::Join("/", $segments)
            $entry = $archive.CreateEntry($entryName, [System.IO.Compression.CompressionLevel]::Optimal)
            $entryStream = $entry.Open()
            try {
                $fileStream = [System.IO.File]::OpenRead($file.FullName)
                try {
                    $fileStream.CopyTo($entryStream)
                }
                finally {
                    $fileStream.Dispose()
                }
            }
            finally {
                $entryStream.Dispose()
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

function New-ArchiveFromDirectory {
    param(
        [string]$SourceDir,
        [string]$DestinationZip
    )

    if (-not (Test-Path $SourceDir)) {
        throw "Archive source directory not found: $SourceDir"
    }

    if (Test-Path $DestinationZip) {
        Remove-Item $DestinationZip -Force
    }

    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $SourceDir,
        $DestinationZip,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false
    )
}

function Resolve-BundleSourceDir {
    param(
        [string]$Name,
        [string[]]$Candidates
    )

    foreach ($candidate in $Candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) {
            continue
        }

        if (Test-Path $candidate) {
            $resolved = (Resolve-Path $candidate).Path
            Write-Host "    Using $Name source: $resolved"
            return $resolved
        }
    }

    throw "$Name source directory not found. Checked: $($Candidates -join ', ')"
}

function Test-OcrBundleSourceContract {
    param(
        [string]$PythonExe,
        [string]$SourceDir
    )

    $validationScript = @'
import importlib
import os
import sys

root = os.environ["IKA_VALIDATE_OCR_ROOT"]
sys.path.insert(0, root)
module = importlib.import_module("scanner")
required = ("scan_roster", "ScanFailure", "inspect_equipment_capture")
missing = [name for name in required if not hasattr(module, name)]
if missing:
    raise SystemExit("Missing OCR runtime exports: " + ", ".join(missing))
print("ocr_contract_ok")
'@

    $previousRoot = $env:IKA_VALIDATE_OCR_ROOT
    $env:IKA_VALIDATE_OCR_ROOT = $SourceDir
    try {
        @($validationScript) | & $PythonExe -
        if ($LASTEXITCODE -ne 0) {
            throw "OCR bundle contract validation failed for source: $SourceDir"
        }
    }
    finally {
        if ($null -eq $previousRoot) {
            Remove-Item Env:\IKA_VALIDATE_OCR_ROOT -ErrorAction SilentlyContinue
        }
        else {
            $env:IKA_VALIDATE_OCR_ROOT = $previousRoot
        }
    }
}

function Get-BundleSourceMetadata {
    param(
        [string]$Name,
        [string]$SourceDir,
        [string]$WorkspaceRoot,
        [string]$ExternalRoot
    )

    $resolvedWorkspaceRoot = if (Test-Path $WorkspaceRoot) {
        (Resolve-Path $WorkspaceRoot).Path
    }
    else {
        ""
    }
    $resolvedExternalRoot = if (Test-Path $ExternalRoot) {
        (Resolve-Path $ExternalRoot).Path
    }
    else {
        ""
    }

    $sourceKind = if ([string]::IsNullOrWhiteSpace($SourceDir)) {
        "unknown"
    }
    elseif (-not [string]::IsNullOrWhiteSpace($resolvedWorkspaceRoot) -and $SourceDir -eq $resolvedWorkspaceRoot) {
        "workspace"
    }
    elseif (-not [string]::IsNullOrWhiteSpace($resolvedExternalRoot) -and $SourceDir -eq $resolvedExternalRoot) {
        "external"
    }
    else {
        "override"
    }

    $branch = $null
    $commit = $null
    try {
        $branch = (& git -C $SourceDir rev-parse --abbrev-ref HEAD 2>$null).Trim()
        $commit = (& git -C $SourceDir rev-parse HEAD 2>$null).Trim()
    }
    catch {
        $branch = $null
        $commit = $null
    }

    return @{
        name = $Name
        sourceDir = $SourceDir
        sourceKind = $sourceKind
        branch = if ([string]::IsNullOrWhiteSpace($branch)) { $null } else { $branch }
        commit = if ([string]::IsNullOrWhiteSpace($commit)) { $null } else { $commit }
    }
}

function Get-FileSha256 {
    param(
        [string]$Path,
        [int]$Attempts = 20,
        [int]$DelayMs = 500
    )

    $lastError = $null
    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        try {
            $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
            try {
                $sha256 = [System.Security.Cryptography.SHA256]::Create()
                try {
                    $hashBytes = $sha256.ComputeHash($stream)
                }
                finally {
                    $sha256.Dispose()
                }
                return ([System.BitConverter]::ToString($hashBytes) -replace "-", "").ToLowerInvariant()
            }
            finally {
                $stream.Dispose()
            }
        }
        catch {
            $lastError = $_
            if ($attempt -lt $Attempts) {
                Start-Sleep -Milliseconds $DelayMs
            }
        }
    }

    if ($null -ne $lastError) {
        throw $lastError
    }

    throw "Could not compute SHA256 for file: $Path"
}

function Remove-PathWithRetry {
    param(
        [string]$Path,
        [int]$Attempts = 40,
        [int]$DelayMs = 500
    )

    if (-not (Test-Path $Path)) {
        return
    }

    $lastError = $null
    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        try {
            Remove-Item $Path -Recurse -Force -ErrorAction Stop
            return
        }
        catch {
            $lastError = $_
            if (-not (Test-Path $Path)) {
                return
            }
            if ($attempt -lt $Attempts) {
                Start-Sleep -Milliseconds $DelayMs
            }
        }
    }

    if ($null -ne $lastError) {
        throw $lastError
    }
}

function Wait-DotnetPublishCompletion {
    param(
        [string]$ProjectMarker,
        [string]$ExpectedFile,
        [int]$TimeoutSeconds = 900
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $publishProcesses = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
            $_.Name -eq "dotnet.exe" -and
            $_.CommandLine -like "* publish *" -and
            $_.CommandLine -like "*$ProjectMarker*"
        })
        $artifactReady = (Test-Path $ExpectedFile) -and ((Get-Item $ExpectedFile).Length -gt 0)
        if ($publishProcesses.Count -eq 0 -and $artifactReady) {
            return
        }

        Start-Sleep -Seconds 2
    }

    throw "Timed out waiting for dotnet publish completion for $ProjectMarker"
}

function Resolve-CudaRuntimeSourceDirs {
    $resolved = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::OrdinalIgnoreCase)

    $explicitDir = $env:IKA_CUDA_DLL_DIR
    if (-not [string]::IsNullOrWhiteSpace($explicitDir)) {
        $explicitCandidates = $explicitDir -split [regex]::Escape([string][System.IO.Path]::PathSeparator)
        foreach ($candidate in $explicitCandidates) {
            if (Test-Path $candidate) {
                [void]$resolved.Add((Resolve-Path $candidate).Path)
            }
        }
    }

    $probeScript = @'
import pathlib
import sys

try:
    import torch
except Exception:
    raise SystemExit(1)

lib_dir = pathlib.Path(torch.__file__).resolve().parent / "lib"
markers = list(lib_dir.glob("cublasLt64_*.dll")) + list(lib_dir.glob("cufft64_*.dll"))
if lib_dir.is_dir() and markers:
    print(lib_dir)
    raise SystemExit(0)
raise SystemExit(1)
'@

    foreach ($candidate in @(
        @((Join-Path $env:LOCALAPPDATA "Programs\\Python\\Python312\\python.exe"), ""),
        @("py", "-3.12"),
        @("python", "")
    )) {
        $executable = $candidate[0]
        $prefixArgument = $candidate[1]
        $arguments = @()
        if (-not [string]::IsNullOrWhiteSpace($prefixArgument)) {
            $arguments += $prefixArgument
        }
        try {
            $raw = @($probeScript) | & $executable @arguments -
            if ($LASTEXITCODE -eq 0) {
                $probeResults = @($raw) | ForEach-Object { $_.ToString().Trim() } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
                foreach ($result in $probeResults) {
                    if (Test-Path $result) {
                        [void]$resolved.Add((Resolve-Path $result).Path)
                    }
                }
            }
        }
        catch {
            continue
        }
    }

    if ($resolved.Count -eq 0) {
        throw "Could not locate CUDA runtime DLL directories. Set IKA_CUDA_DLL_DIR to one or more torch\\lib folders with CUDA DLLs."
    }

    return @($resolved)
}

function Copy-CudaRuntimeDlls {
    param(
        [string[]]$SourceDirs,
        [string]$DestinationDir
    )

    $patterns = @(
        "cublas*.dll",
        "cudart*.dll",
        "cudnn*.dll",
        "cufft*.dll",
        "curand*.dll",
        "cusolver*.dll",
        "cusparse*.dll",
        "nvJitLink*.dll",
        "nvrtc*.dll",
        "nvToolsExt*.dll",
        "zlibwapi.dll"
    )

    New-Item -ItemType Directory -Force -Path $DestinationDir | Out-Null
    Get-ChildItem -Path $DestinationDir -Filter *.dll -File -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

    $copied = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($sourceDir in $SourceDirs) {
        foreach ($pattern in $patterns) {
            foreach ($file in Get-ChildItem -Path $sourceDir -Filter $pattern -File -ErrorAction SilentlyContinue) {
                if ($copied.Add($file.Name)) {
                    Copy-Item $file.FullName -Destination (Join-Path $DestinationDir $file.Name) -Force
                }
            }
        }
    }

    $required = @(
        "cublasLt64_12.dll",
        "cudart64_12.dll",
        "cudnn64_9.dll",
        "cufft64_11.dll",
        "curand64_10.dll",
        "cusolver64_11.dll",
        "cusparse64_12.dll",
        "nvJitLink_120_0.dll",
        "nvrtc64_120_0.dll",
        "zlibwapi.dll"
    )
    foreach ($name in $required) {
        if (-not (Test-Path (Join-Path $DestinationDir $name))) {
            throw "CUDA runtime bundle is missing required dependency: $name"
        }
    }

    return (Get-ChildItem -Path $DestinationDir -Filter *.dll -File | Sort-Object Name)
}

$ocrRepoDir = Resolve-BundleSourceDir -Name "OCR bundle" -Candidates @(
    $env:IKA_OCR_REPO_DIR,
    (Join-Path $workspaceRoot "Inter-Knot Arena OCR_Scan"),
    (Join-Path $root "external\\OCR_Scan")
)

$cvRepoDir = Resolve-BundleSourceDir -Name "CV bundle" -Candidates @(
    $env:IKA_CV_REPO_DIR,
    (Join-Path $workspaceRoot "Inter-Knot Arena CV"),
    (Join-Path $root "external\\CV")
)

Write-Host "==> Building native module (ika_native.dll)"
Write-Host "    CMake generator: $cmakeGenerator"
if (Test-Path $nativeBuildDir) {
    $cachePath = Join-Path $nativeBuildDir "CMakeCache.txt"
    if (Test-Path $cachePath) {
        $cacheLine = Select-String -Path $cachePath -Pattern "^CMAKE_GENERATOR:INTERNAL=(.+)$" -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($cacheLine -and $cacheLine.Matches.Count -gt 0) {
            $cachedGenerator = $cacheLine.Matches[0].Groups[1].Value
            if (-not [string]::IsNullOrWhiteSpace($cachedGenerator) -and $cachedGenerator -ne $cmakeGenerator) {
                Write-Host "    Generator changed ($cachedGenerator -> $cmakeGenerator), cleaning native build directory"
                Remove-Item $nativeBuildDir -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }
}
Invoke-ExternalStep -Name "CMake configure" -Action {
    if ($cmakeGenerator -eq "Ninja") {
        cmake -S $nativeSourceDir -B $nativeBuildDir -G Ninja "-DCMAKE_BUILD_TYPE=$Configuration" -DCMAKE_CXX_SCAN_FOR_MODULES=OFF
    } else {
        cmake -S $nativeSourceDir -B $nativeBuildDir -G $cmakeGenerator -A x64 "-DCMAKE_BUILD_TYPE=$Configuration" -DCMAKE_CXX_SCAN_FOR_MODULES=OFF
    }
}
Invoke-ExternalStep -Name "CMake build" -Action {
    cmake --build $nativeBuildDir --config $Configuration
}
$nativeDllPath = $nativeDllPathCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $nativeDllPath) {
    throw "Native DLL not found after build. Checked: $($nativeDllPathCandidates -join ', ')"
}

Write-Host "==> Building python worker (VerifierWorker onedir bundle)"
Push-Location $workerDir
Invoke-ExternalStep -Name "Python venv" -Action {
    if (-not (Test-Path $workerPythonExe)) {
        py -3.12 -m venv .venv
    }
}
Invoke-ExternalStep -Name "Pip upgrade" -Action {
    & $workerPythonExe -m pip install --upgrade pip
}
Invoke-ExternalStep -Name "Pip requirements" -Action {
    & $workerPythonExe -m pip install -r requirements.txt
}
Invoke-ExternalStep -Name "Pip pyinstaller" -Action {
    & $workerPythonExe -m pip install pyinstaller
}
Invoke-ExternalStep -Name "PyInstaller build" -Action {
    & $workerPyInstallerExe VerifierWorker.spec --noconfirm --clean --distpath $workerDistRoot --workpath $workerWorkDir
}
Pop-Location

Write-Host "==> Staging bundled assets into UI project"
New-Item -ItemType Directory -Force -Path $bundleStageDir | Out-Null
if (Test-Path $workerBundlePath) {
    Remove-PathWithRetry -Path $workerBundlePath
}
if (Test-Path (Join-Path $bundleStageDir "VerifierWorker.exe")) {
    Remove-PathWithRetry -Path (Join-Path $bundleStageDir "VerifierWorker.exe")
}
New-ArchiveFromDirectory -SourceDir $workerDistDir -DestinationZip $workerBundlePath
Copy-Item $nativeDllPath -Destination (Join-Path $bundleStageDir "ika_native.dll") -Force
$cudaRuntimeSourceDirs = Resolve-CudaRuntimeSourceDirs
Write-Host "    Using CUDA runtime sources: $($cudaRuntimeSourceDirs -join ', ')"
$cudaRuntimeFiles = Copy-CudaRuntimeDlls -SourceDirs $cudaRuntimeSourceDirs -DestinationDir $bundleCudaDir
Write-Host "==> Building OCR/CV bundle archives"
Test-OcrBundleSourceContract -PythonExe $workerPythonExe -SourceDir $ocrRepoDir
New-BundleArchive -SourceDir $ocrRepoDir -DestinationZip $ocrBundlePath
New-BundleArchive -SourceDir $cvRepoDir -DestinationZip $cvBundlePath

Write-Host "==> Generating bundle integrity manifest"
$manifest = @{
    generatedAt = (Get-Date).ToString("o")
    cudaRuntimeFiles = @($cudaRuntimeFiles | ForEach-Object { $_.Name })
    sources = @{
        ocr = Get-BundleSourceMetadata `
            -Name "ocr" `
            -SourceDir $ocrRepoDir `
            -WorkspaceRoot (Join-Path $workspaceRoot "Inter-Knot Arena OCR_Scan") `
            -ExternalRoot (Join-Path $root "external\\OCR_Scan")
        cv = Get-BundleSourceMetadata `
            -Name "cv" `
            -SourceDir $cvRepoDir `
            -WorkspaceRoot (Join-Path $workspaceRoot "Inter-Knot Arena CV") `
            -ExternalRoot (Join-Path $root "external\\CV")
    }
    sha256 = @{
        "VerifierWorker_bundle.zip" = Get-FileSha256 -Path $workerBundlePath
        "ika_native.dll" = Get-FileSha256 -Path (Join-Path $bundleStageDir "ika_native.dll")
        "ocr_scan_bundle.zip" = Get-FileSha256 -Path $ocrBundlePath
        "cv_bundle.zip" = Get-FileSha256 -Path $cvBundlePath
    }
}
foreach ($file in $cudaRuntimeFiles) {
    $manifest.sha256[$file.Name] = Get-FileSha256 -Path $file.FullName
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -Path $bundleManifestPath -Encoding UTF8

Write-Host "==> Publishing single-file WPF host (VerifierApp.exe)"
if (Test-Path $artifactRoot) {
    Remove-Item $artifactRoot -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
Invoke-ExternalStep -Name "dotnet publish" -Action {
    dotnet publish (Join-Path $root "src\\VerifierApp.UI\\VerifierApp.UI.csproj") `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        "-p:BundledAssetRoot=$bundleStageDir" `
        "-p:ApiOrigin=$ApiOrigin" `
        -o $artifactRoot
}
Wait-DotnetPublishCompletion -ProjectMarker "VerifierApp.UI.csproj" -ExpectedFile (Join-Path $artifactRoot "VerifierApp.exe")

$publishSidecarMap = @{
    "VerifierWorker_bundle.zip" = $workerBundlePath
    "bundle.manifest.json" = $bundleManifestPath
    "ocr_scan_bundle.zip" = $ocrBundlePath
    "cv_bundle.zip" = $cvBundlePath
    "ika_native.dll" = (Join-Path $bundleStageDir "ika_native.dll")
}
foreach ($entry in $publishSidecarMap.GetEnumerator()) {
    Copy-Item $entry.Value -Destination (Join-Path $artifactRoot $entry.Key) -Force
}
$publishCudaDir = Join-Path $artifactRoot "cuda"
if (Test-Path $publishCudaDir) {
    Remove-PathWithRetry -Path $publishCudaDir
}
Copy-Item $bundleCudaDir -Destination $publishCudaDir -Recurse -Force

Write-Host "==> Publishing packaged live-scan tool (VerifierApp.LiveScan.exe)"
if (Test-Path $liveScanPublishRoot) {
    Remove-Item $liveScanPublishRoot -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Force -Path $liveScanPublishRoot | Out-Null
Invoke-ExternalStep -Name "dotnet publish VerifierApp.LiveScan" -Action {
    dotnet publish $liveScanProjectPath `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -o $liveScanPublishRoot
}
Wait-DotnetPublishCompletion -ProjectMarker "VerifierApp.LiveScan.csproj" -ExpectedFile (Join-Path $liveScanPublishRoot "VerifierApp.LiveScan.exe")
$publishToolsRoot = Join-Path $artifactRoot "tools"
New-Item -ItemType Directory -Force -Path $publishToolsRoot | Out-Null
Copy-Item (Join-Path $liveScanPublishRoot "VerifierApp.LiveScan.exe") -Destination (Join-Path $publishToolsRoot "VerifierApp.LiveScan.exe") -Force

Write-Host "Build output: $artifactRoot"
Write-Host "Primary artifact: $(Join-Path $artifactRoot 'VerifierApp.exe')"
