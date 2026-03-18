using System.Diagnostics;
using System.Collections.Generic;

namespace VerifierApp.WorkerHost;

public sealed class WorkerProcessLauncher : IDisposable
{
    private Process? _process;

    public void Start(
        string workerExecutablePath,
        string pipeName = "ika_verifier_worker",
        string? extraArguments = null,
        string? pathPrependDirectory = null,
        string? bundleRoot = null,
        string? ocrRoot = null,
        string? cvRoot = null
    )
    {
        if (_process is { HasExited: false })
        {
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = workerExecutablePath,
            Arguments = string.IsNullOrWhiteSpace(extraArguments)
                ? $"--pipe {pipeName}"
                : $"{extraArguments} --pipe {pipeName}",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(workerExecutablePath) ?? AppContext.BaseDirectory
        };
        if (!string.IsNullOrWhiteSpace(bundleRoot))
        {
            startInfo.Environment["IKA_BUNDLE_ROOT"] = bundleRoot;
        }
        if (!string.IsNullOrWhiteSpace(ocrRoot))
        {
            startInfo.Environment["IKA_OCR_SCAN_ROOT"] = ocrRoot;
        }
        if (!string.IsNullOrWhiteSpace(cvRoot))
        {
            startInfo.Environment["IKA_CV_ROOT"] = cvRoot;
        }
        PrependDirectoryToPath(startInfo.Environment, pathPrependDirectory);

        _process = Process.Start(startInfo)
                   ?? throw new InvalidOperationException("Failed to start worker process");
    }

    public void Dispose()
    {
        if (_process is null)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // ignored
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }

    private static void PrependDirectoryToPath(IDictionary<string, string?> environment, string? directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return;
        }

        var currentPath = environment.TryGetValue("PATH", out var existing) && !string.IsNullOrWhiteSpace(existing)
            ? existing
            : Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var parts = currentPath
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Any(part => string.Equals(part, directoryPath, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        environment["PATH"] = string.IsNullOrWhiteSpace(currentPath)
            ? directoryPath
            : directoryPath + Path.PathSeparator + currentPath;
    }
}
