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
$nativeSourceDir = Join-Path $root "native\\ika_native"
$nativeBuildDir = Join-Path $root "native\\ika_native\\build\\release"
$workerDir = Join-Path $root "worker"
$bundleDir = Join-Path $root "src\\VerifierApp.UI\\Bundled"
$bundleCudaDir = Join-Path $bundleDir "cuda"
$workerDistDir = Join-Path $workerDir "dist\\VerifierWorker"
$workerBundlePath = Join-Path $bundleDir "VerifierWorker_bundle.zip"
$workerPythonExe = Join-Path $workerDir ".venv\\Scripts\\python.exe"
$workerPyInstallerExe = Join-Path $workerDir ".venv\\Scripts\\pyinstaller.exe"
$nativeDllPathCandidates = @(
    (Join-Path $nativeBuildDir "ika_native.dll"),
    (Join-Path $nativeBuildDir "$Configuration\\ika_native.dll"),
    (Join-Path $nativeBuildDir "Release\\ika_native.dll")
)
$ocrBundlePath = Join-Path $bundleDir "ocr_scan_bundle.zip"
$cvBundlePath = Join-Path $bundleDir "cv_bundle.zip"
$bundleManifestPath = Join-Path $bundleDir "bundle.manifest.json"
$inVsDevShell = -not [string]::IsNullOrWhiteSpace($env:VSCMD_VER)
$cmakeGenerator = if ($inVsDevShell) { "Ninja" } else { "Visual Studio 18 2026" }

Add-Type -AssemblyName System.IO.Compression.FileSystem

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

function Resolve-CudaRuntimeSourceDir {
    $explicitDir = $env:IKA_CUDA_DLL_DIR
    if (-not [string]::IsNullOrWhiteSpace($explicitDir) -and (Test-Path $explicitDir)) {
        return (Resolve-Path $explicitDir).Path
    }

    $probeScript = @'
import pathlib
import sys

try:
    import torch
except Exception:
    raise SystemExit(1)

lib_dir = pathlib.Path(torch.__file__).resolve().parent / "lib"
markers = list(lib_dir.glob("cublasLt64_*.dll"))
if lib_dir.is_dir() and markers:
    print(lib_dir)
    raise SystemExit(0)
raise SystemExit(1)
'@

    foreach ($candidate in @(@("py", "-3.12"), @("python", ""))) {
        $executable = $candidate[0]
        $prefixArgument = $candidate[1]
        $arguments = @()
        if (-not [string]::IsNullOrWhiteSpace($prefixArgument)) {
            $arguments += $prefixArgument
        }
        try {
            $raw = @($probeScript) | & $executable @arguments -
            if ($LASTEXITCODE -eq 0) {
                $resolved = ($raw -join "").Trim()
                if (-not [string]::IsNullOrWhiteSpace($resolved) -and (Test-Path $resolved)) {
                    return (Resolve-Path $resolved).Path
                }
            }
        }
        catch {
            continue
        }
    }

    throw "Could not locate a CUDA runtime DLL directory. Set IKA_CUDA_DLL_DIR to a torch\\lib folder with CUDA DLLs."
}

function Copy-CudaRuntimeDlls {
    param(
        [string]$SourceDir,
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

    if (Test-Path $DestinationDir) {
        Remove-Item $DestinationDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    New-Item -ItemType Directory -Force -Path $DestinationDir | Out-Null

    $copied = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($pattern in $patterns) {
        foreach ($file in Get-ChildItem -Path $SourceDir -Filter $pattern -File -ErrorAction SilentlyContinue) {
            if ($copied.Add($file.Name)) {
                Copy-Item $file.FullName -Destination (Join-Path $DestinationDir $file.Name) -Force
            }
        }
    }

    $required = @("cublasLt64_12.dll", "cudart64_12.dll", "cudnn64_9.dll")
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
    & $workerPyInstallerExe VerifierWorker.spec --noconfirm --clean
}
Pop-Location

Write-Host "==> Staging bundled assets into UI project"
New-Item -ItemType Directory -Force -Path $bundleDir | Out-Null
if (Test-Path $workerBundlePath) {
    Remove-Item $workerBundlePath -Force
}
if (Test-Path (Join-Path $bundleDir "VerifierWorker.exe")) {
    Remove-Item (Join-Path $bundleDir "VerifierWorker.exe") -Force -ErrorAction SilentlyContinue
}
New-ArchiveFromDirectory -SourceDir $workerDistDir -DestinationZip $workerBundlePath
Copy-Item $nativeDllPath -Destination (Join-Path $bundleDir "ika_native.dll") -Force
$cudaRuntimeSourceDir = Resolve-CudaRuntimeSourceDir
$cudaRuntimeFiles = Copy-CudaRuntimeDlls -SourceDir $cudaRuntimeSourceDir -DestinationDir $bundleCudaDir
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
        "VerifierWorker_bundle.zip" = (Get-FileHash -Algorithm SHA256 $workerBundlePath).Hash.ToLowerInvariant()
        "ika_native.dll" = (Get-FileHash -Algorithm SHA256 (Join-Path $bundleDir "ika_native.dll")).Hash.ToLowerInvariant()
        "ocr_scan_bundle.zip" = (Get-FileHash -Algorithm SHA256 $ocrBundlePath).Hash.ToLowerInvariant()
        "cv_bundle.zip" = (Get-FileHash -Algorithm SHA256 $cvBundlePath).Hash.ToLowerInvariant()
    }
}
foreach ($file in $cudaRuntimeFiles) {
    $manifest.sha256[$file.Name] = (Get-FileHash -Algorithm SHA256 $file.FullName).Hash.ToLowerInvariant()
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

Copy-Item $workerBundlePath -Destination (Join-Path $artifactRoot "VerifierWorker_bundle.zip") -Force
$publishCudaDir = Join-Path $artifactRoot "cuda"
if (Test-Path $publishCudaDir) {
    Remove-Item $publishCudaDir -Recurse -Force -ErrorAction SilentlyContinue
}
Copy-Item $bundleCudaDir -Destination $publishCudaDir -Recurse -Force

Write-Host "Build output: $artifactRoot"
Write-Host "Primary artifact: $(Join-Path $artifactRoot 'VerifierApp.exe')"
