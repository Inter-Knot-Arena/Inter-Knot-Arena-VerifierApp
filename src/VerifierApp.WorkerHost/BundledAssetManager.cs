using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace VerifierApp.WorkerHost;

public sealed record BundledAssetPaths(
    string RootPath,
    string WorkerExePath,
    string NativeDllPath,
    string OcrScanRoot,
    string CvRoot
);

public static class BundledAssetManager
{
    private const string WorkerResourceName = "Bundled/VerifierWorker.exe";
    private const string NativeResourceName = "Bundled/ika_native.dll";
    private const string OcrBundleResourceName = "Bundled/ocr_scan_bundle.zip";
    private const string CvBundleResourceName = "Bundled/cv_bundle.zip";
    private const string ManifestResourceName = "Bundled/bundle.manifest.json";

    public static BundledAssetPaths EnsureExtracted(Assembly resourceAssembly)
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appVersion = resourceAssembly.GetName().Version?.ToString() ?? "dev";
        var root = Path.Combine(baseDir, "InterKnotArena", "VerifierApp", "bundled", appVersion);
        Directory.CreateDirectory(root);

        var manifestPath = ResolveAssetPath(
            resourceAssembly,
            ManifestResourceName,
            Path.Combine(root, "bundle.manifest.json"),
            Path.Combine(AppContext.BaseDirectory, "bundle.manifest.json"),
            expectedHash: null
        );
        var hashes = LoadHashManifest(manifestPath);

        var workerPath = ResolveAssetPath(
            resourceAssembly,
            WorkerResourceName,
            Path.Combine(root, "VerifierWorker.exe"),
            Path.Combine(AppContext.BaseDirectory, "VerifierWorker.exe"),
            hashes.GetValueOrDefault("VerifierWorker.exe")
        );
        var nativePath = ResolveAssetPath(
            resourceAssembly,
            NativeResourceName,
            Path.Combine(root, "ika_native.dll"),
            Path.Combine(AppContext.BaseDirectory, "ika_native.dll"),
            hashes.GetValueOrDefault("ika_native.dll")
        );
        var ocrBundlePath = ResolveAssetPath(
            resourceAssembly,
            OcrBundleResourceName,
            Path.Combine(root, "ocr_scan_bundle.zip"),
            Path.Combine(AppContext.BaseDirectory, "ocr_scan_bundle.zip"),
            hashes.GetValueOrDefault("ocr_scan_bundle.zip")
        );
        var cvBundlePath = ResolveAssetPath(
            resourceAssembly,
            CvBundleResourceName,
            Path.Combine(root, "cv_bundle.zip"),
            Path.Combine(AppContext.BaseDirectory, "cv_bundle.zip"),
            hashes.GetValueOrDefault("cv_bundle.zip")
        );

        var ocrRoot = Path.Combine(root, "ocr_scan");
        var cvRoot = Path.Combine(root, "cv");
        EnsureArchiveExtracted(ocrBundlePath, ocrRoot, hashes.GetValueOrDefault("ocr_scan_bundle.zip"));
        EnsureArchiveExtracted(cvBundlePath, cvRoot, hashes.GetValueOrDefault("cv_bundle.zip"));

        return new BundledAssetPaths(root, workerPath, nativePath, ocrRoot, cvRoot);
    }

    private static Dictionary<string, string> LoadHashManifest(string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        using var stream = File.OpenRead(manifestPath);
        using var doc = JsonDocument.Parse(stream);
        if (!doc.RootElement.TryGetProperty("sha256", out var shaNode) || shaNode.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in shaNode.EnumerateObject())
        {
            var value = property.Value.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                result[property.Name] = value.Trim().ToLowerInvariant();
            }
        }
        return result;
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
}
