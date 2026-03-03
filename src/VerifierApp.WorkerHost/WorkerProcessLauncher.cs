using System.Diagnostics;

namespace VerifierApp.WorkerHost;

public sealed class WorkerProcessLauncher : IDisposable
{
    private Process? _process;

    public void Start(string workerExecutablePath, string pipeName = "ika_verifier_worker")
    {
        if (_process is { HasExited: false })
        {
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = workerExecutablePath,
            Arguments = $"--pipe {pipeName}",
            UseShellExecute = false,
            CreateNoWindow = true
        };
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
