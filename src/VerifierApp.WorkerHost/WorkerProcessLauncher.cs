using System.Diagnostics;

namespace VerifierApp.WorkerHost;

public sealed class WorkerProcessLauncher : IDisposable
{
    private Process? _process;

    public void Start(
        string workerExecutablePath,
        string pipeName = "ika_verifier_worker",
        string? extraArguments = null,
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
}
