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

        var workerPath = Path.Combine(root, "VerifierWorker.exe");
        var nativePath = Path.Combine(root, "ika_native.dll");

        ExtractResourceIfNeeded(resourceAssembly, WorkerResourceName, workerPath);
        ExtractResourceIfNeeded(resourceAssembly, NativeResourceName, nativePath);

        return new BundledAssetPaths(root, workerPath, nativePath);
    }

    private static void ExtractResourceIfNeeded(
        Assembly assembly,
        string resourceName,
        string outputPath
    )
    {
        if (File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
        {
            return;
        }

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            throw new InvalidOperationException(
                $"Missing bundled resource '{resourceName}'. Run scripts/build.ps1 to stage bundle artifacts."
            );
        }

        using var file = File.Open(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        stream.CopyTo(file);
    }
}
