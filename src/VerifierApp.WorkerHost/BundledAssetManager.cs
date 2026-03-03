using System.Reflection;

namespace VerifierApp.WorkerHost;

public sealed record BundledAssetPaths(
    string RootPath,
    string WorkerExePath,
    string NativeDllPath
);

public static class BundledAssetManager
{
    private const string WorkerResourceName = "Bundled/VerifierWorker.exe";
    private const string NativeResourceName = "Bundled/ika_native.dll";

    public static BundledAssetPaths EnsureExtracted(Assembly resourceAssembly)
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appVersion = resourceAssembly.GetName().Version?.ToString() ?? "dev";
        var root = Path.Combine(baseDir, "InterKnotArena", "VerifierApp", "bundled", appVersion);
        Directory.CreateDirectory(root);

        var workerPath = ResolveAssetPath(
            resourceAssembly,
            WorkerResourceName,
            Path.Combine(root, "VerifierWorker.exe"),
            Path.Combine(AppContext.BaseDirectory, "VerifierWorker.exe")
        );
        var nativePath = ResolveAssetPath(
            resourceAssembly,
            NativeResourceName,
            Path.Combine(root, "ika_native.dll"),
            Path.Combine(AppContext.BaseDirectory, "ika_native.dll")
        );

        return new BundledAssetPaths(root, workerPath, nativePath);
    }

    private static string ResolveAssetPath(
        Assembly assembly,
        string resourceName,
        string outputPath,
        string sidecarPath
    )
    {
        if (File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
        {
            return outputPath;
        }

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is not null)
        {
            using var file = File.Open(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            stream.CopyTo(file);
            return outputPath;
        }

        if (File.Exists(sidecarPath))
        {
            return sidecarPath;
        }

        throw new InvalidOperationException(
            $"Missing bundled resource '{resourceName}' and sidecar '{Path.GetFileName(sidecarPath)}'. " +
            "Run scripts/build.ps1 to stage and embed bundle artifacts."
        );
    }
}
