using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace VerifierApp.WorkerHost;

public sealed record BundledAssetPaths(
    string RootPath,
    string CudaRoot,
    string WorkerExePath,
    string NativeDllPath,
    string OcrScanRoot,
    string CvRoot,
    string ManifestPath
);

public static class BundledAssetManager
{
    private const string WorkerBundleResourceName = "Bundled/VerifierWorker_bundle.zip";
    private const string NativeResourceName = "Bundled/ika_native.dll";
    private const string OcrBundleResourceName = "Bundled/ocr_scan_bundle.zip";
    private const string CvBundleResourceName = "Bundled/cv_bundle.zip";
    private const string ManifestResourceName = "Bundled/bundle.manifest.json";

    public static BundledAssetPaths EnsureExtracted(Assembly resourceAssembly, string? sidecarRootOverride = null)
    {
        var appVersion = resourceAssembly.GetName().Version?.ToString() ?? "dev";
        var sidecarRoot = ResolveSidecarRoot(sidecarRootOverride);
        var root = ResolveExtractionRoot(sidecarRootOverride, sidecarRoot, appVersion);
        Directory.CreateDirectory(root);

        var manifestPath = ResolveAssetPath(
            resourceAssembly,
            ManifestResourceName,
            Path.Combine(root, "bundle.manifest.json"),
            Path.Combine(sidecarRoot, "bundle.manifest.json"),
            expectedHash: null
        );
        var manifest = LoadBundleManifest(manifestPath);
        var hashes = manifest.Sha256;

        var workerBundlePath = ResolveAssetPath(
            resourceAssembly,
            WorkerBundleResourceName,
            Path.Combine(root, "VerifierWorker_bundle.zip"),
            Path.Combine(sidecarRoot, "VerifierWorker_bundle.zip"),
            hashes.GetValueOrDefault("VerifierWorker_bundle.zip")
        );
        var nativePath = ResolveAssetPath(
            resourceAssembly,
            NativeResourceName,
            Path.Combine(root, "ika_native.dll"),
            Path.Combine(sidecarRoot, "ika_native.dll"),
            hashes.GetValueOrDefault("ika_native.dll")
        );
        var ocrBundlePath = ResolveAssetPath(
            resourceAssembly,
            OcrBundleResourceName,
            Path.Combine(root, "ocr_scan_bundle.zip"),
            Path.Combine(sidecarRoot, "ocr_scan_bundle.zip"),
            hashes.GetValueOrDefault("ocr_scan_bundle.zip")
        );
        var cvBundlePath = ResolveAssetPath(
            resourceAssembly,
            CvBundleResourceName,
            Path.Combine(root, "cv_bundle.zip"),
            Path.Combine(sidecarRoot, "cv_bundle.zip"),
            hashes.GetValueOrDefault("cv_bundle.zip")
        );
        foreach (var fileName in manifest.CudaRuntimeFiles)
        {
            var outputPath = Path.Combine(root, "cuda", fileName);
            var resolvedPath = ResolveAssetPath(
                resourceAssembly,
                $"Bundled/cuda/{fileName}",
                outputPath,
                Path.Combine(sidecarRoot, "cuda", fileName),
                hashes.GetValueOrDefault(fileName)
            );
            MaterializeAsset(outputPath, resolvedPath);
        }

        var workerRoot = Path.Combine(root, "worker");
        EnsureArchiveExtracted(workerBundlePath, workerRoot, hashes.GetValueOrDefault("VerifierWorker_bundle.zip"));
        var workerPath = Path.Combine(workerRoot, "VerifierWorker.exe");
        if (!File.Exists(workerPath))
        {
            throw new InvalidOperationException(
                $"Extracted worker bundle is missing VerifierWorker.exe at '{workerPath}'."
            );
        }
        var ocrRoot = Path.Combine(root, "ocr_scan");
        var cvRoot = Path.Combine(root, "cv");
        var cudaRoot = Path.Combine(root, "cuda");
        if (!string.IsNullOrWhiteSpace(sidecarRootOverride))
        {
            MirrorCudaRuntimeIntoWorker(cudaRoot, workerRoot, manifest.CudaRuntimeFiles);
        }
        EnsureArchiveExtracted(ocrBundlePath, ocrRoot, hashes.GetValueOrDefault("ocr_scan_bundle.zip"));
        EnsureArchiveExtracted(cvBundlePath, cvRoot, hashes.GetValueOrDefault("cv_bundle.zip"));

        return new BundledAssetPaths(root, cudaRoot, workerPath, nativePath, ocrRoot, cvRoot, manifestPath);
    }

    private static string ResolveExtractionRoot(string? sidecarRootOverride, string sidecarRoot, string appVersion)
    {
        if (!string.IsNullOrWhiteSpace(sidecarRootOverride))
        {
            return Path.Combine(sidecarRoot, "_bundled_runtime", appVersion);
        }

        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(baseDir, "InterKnotArena", "VerifierApp", "bundled", appVersion);
    }

    private static string ResolveSidecarRoot(string? sidecarRootOverride)
    {
        if (string.IsNullOrWhiteSpace(sidecarRootOverride))
        {
            return AppContext.BaseDirectory;
        }

        var fullPath = Path.GetFullPath(sidecarRootOverride);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Bundled sidecar root does not exist: '{fullPath}'.");
        }

        return fullPath;
    }

    private static BundleManifestData LoadBundleManifest(string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            return new BundleManifestData(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                new List<string>()
            );
        }

        using var stream = File.OpenRead(manifestPath);
        using var doc = JsonDocument.Parse(stream);
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.TryGetProperty("sha256", out var shaNode) && shaNode.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in shaNode.EnumerateObject())
            {
                var value = property.Value.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    hashes[property.Name] = value.Trim().ToLowerInvariant();
                }
            }
        }

        var cudaRuntimeFiles = new List<string>();
        if (doc.RootElement.TryGetProperty("cudaRuntimeFiles", out var cudaNode) && cudaNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in cudaNode.EnumerateArray())
            {
                var value = item.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    cudaRuntimeFiles.Add(value.Trim());
                }
            }
        }

        return new BundleManifestData(hashes, cudaRuntimeFiles);
    }

    private static string ResolveAssetPath(
        Assembly assembly,
        string resourceName,
        string outputPath,
        string sidecarPath,
        string? expectedHash
    )
    {
        if (IsValidAsset(outputPath, expectedHash))
        {
            return outputPath;
        }
        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is not null)
        {
            using var file = File.Open(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            stream.CopyTo(file);
            file.Flush(flushToDisk: true);
            if (!IsValidAsset(outputPath, expectedHash))
            {
                throw new InvalidOperationException($"Extracted resource '{resourceName}' failed integrity check.");
            }
            return outputPath;
        }

        if (IsValidAsset(sidecarPath, expectedHash))
        {
            return sidecarPath;
        }

        throw new InvalidOperationException(
            $"Missing bundled resource '{resourceName}' and valid sidecar '{Path.GetFileName(sidecarPath)}'. " +
            "Run scripts/build.ps1 to stage bundle artifacts."
        );
    }

    private static void EnsureArchiveExtracted(string archivePath, string extractRoot, string? expectedHash)
    {
        var markerPath = Path.Combine(extractRoot, ".bundle_hash");
        if (Directory.Exists(extractRoot) && File.Exists(markerPath) && !string.IsNullOrWhiteSpace(expectedHash))
        {
            var current = File.ReadAllText(markerPath).Trim();
            if (string.Equals(current, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        if (Directory.Exists(extractRoot))
        {
            Directory.Delete(extractRoot, recursive: true);
        }
        Directory.CreateDirectory(extractRoot);
        ZipFile.ExtractToDirectory(archivePath, extractRoot, overwriteFiles: true);
        if (!string.IsNullOrWhiteSpace(expectedHash))
        {
            File.WriteAllText(markerPath, expectedHash);
        }
    }

    private static bool IsValidAsset(string path, string? expectedHash)
    {
        if (!File.Exists(path))
        {
            return false;
        }
        if (string.IsNullOrWhiteSpace(expectedHash))
        {
            return new FileInfo(path).Length > 0;
        }

        using var stream = File.OpenRead(path);
        var digest = SHA256.HashData(stream);
        var actual = Convert.ToHexStringLower(digest);
        return string.Equals(actual, expectedHash.Trim().ToLowerInvariant(), StringComparison.OrdinalIgnoreCase);
    }

    private static void MaterializeAsset(string outputPath, string resolvedPath)
    {
        if (string.Equals(outputPath, resolvedPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        File.Copy(resolvedPath, outputPath, overwrite: true);
    }

    private static void MirrorCudaRuntimeIntoWorker(string cudaRoot, string workerRoot, IEnumerable<string> fileNames)
    {
        if (!Directory.Exists(cudaRoot) || !Directory.Exists(workerRoot))
        {
            return;
        }

        foreach (var fileName in fileNames)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                continue;
            }

            var sourcePath = Path.Combine(cudaRoot, fileName);
            if (!File.Exists(sourcePath))
            {
                continue;
            }

            var destinationPath = Path.Combine(workerRoot, fileName);
            if (File.Exists(destinationPath))
            {
                var sourceInfo = new FileInfo(sourcePath);
                var destinationInfo = new FileInfo(destinationPath);
                if (sourceInfo.Length == destinationInfo.Length)
                {
                    continue;
                }

                File.Delete(destinationPath);
            }

            File.Copy(sourcePath, destinationPath, overwrite: true);
        }
    }

    private sealed record BundleManifestData(
        Dictionary<string, string> Sha256,
        List<string> CudaRuntimeFiles
    );
}
