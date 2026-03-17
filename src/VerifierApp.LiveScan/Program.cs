using System.Text.Json;
using VerifierApp.Core.Services;
using VerifierApp.WorkerHost;

namespace VerifierApp.LiveScan;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static async Task<int> Main(string[] args)
    {
        var options = Options.Parse(args);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };

        try
        {
            var repoRoot = ResolveRepoRoot(options.RepoRoot);
            EnsureProcessEnvironmentDefault("IKA_ALLOW_SOFT_INPUT_LOCK", "1");
            EnsureProcessEnvironmentDefault("IKA_DEFAULT_OCR_CAPTURE_PLAN", "VISIBLE_SLICE_AGENT_DETAIL_EQUIPMENT_AMP_BETA");
            EnsureProcessEnvironmentDefault("IKA_KEY_SCRIPT_BACKEND", "managed");
            var nativeDll = ResolveNativeDll(repoRoot);
            PrependDirectoryToPath(Path.GetDirectoryName(nativeDll));

            if (!string.IsNullOrWhiteSpace(options.ProbeScript))
            {
                return await RunProbeAsync(options, cts.Token);
            }

            var workerLaunch = ResolveWorkerLaunch(repoRoot);
            var ocrRoot = ResolveOptionalDirectory(
                Environment.GetEnvironmentVariable("IKA_OCR_SCAN_ROOT"),
                Path.Combine(repoRoot, "external", "OCR_Scan"),
                Path.Combine(Path.GetDirectoryName(repoRoot) ?? repoRoot, "Inter-Knot Arena OCR_Scan")
            );
            var cvRoot = ResolveOptionalDirectory(
                Environment.GetEnvironmentVariable("IKA_CV_ROOT"),
                Path.Combine(repoRoot, "external", "CV"),
                Path.Combine(Path.GetDirectoryName(repoRoot) ?? repoRoot, "Inter-Knot Arena CV")
            );

            using var launcher = new WorkerProcessLauncher();
            launcher.Start(
                workerExecutablePath: workerLaunch.ExecutablePath,
                pipeName: options.PipeName,
                extraArguments: workerLaunch.ExtraArguments,
                ocrRoot: ocrRoot,
                cvRoot: cvRoot
            );

            if (options.WorkerStartupDelayMs > 0)
            {
                await Task.Delay(options.WorkerStartupDelayMs, cts.Token);
            }

            await using var worker = new NamedPipeWorkerClient(options.PipeName, options.ConnectTimeoutMs);
            if (!await worker.HealthAsync(cts.Token))
            {
                throw new InvalidOperationException("Worker health probe failed.");
            }

            var orchestrator = new ScanOrchestrator(worker, new NativeBridge());
            var result = await orchestrator.CaptureRosterScanAsync(
                regionHint: options.Region,
                fullSync: options.FullSync,
                locale: options.Locale,
                resolution: options.Resolution,
                ct: cts.Token
            );

            var json = JsonSerializer.Serialize(result, JsonOptions);
            if (!string.IsNullOrWhiteSpace(options.OutputPath))
            {
                var outputPath = Path.GetFullPath(options.OutputPath);
                var directory = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                await File.WriteAllTextAsync(outputPath, json + Environment.NewLine, cts.Token);
            }

            Console.WriteLine(json);
            return string.IsNullOrWhiteSpace(result.ErrorCode) ? 0 : 2;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Live scan cancelled.");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static async Task<int> RunProbeAsync(Options options, CancellationToken ct)
    {
        var nativeBridge = new NativeBridge();
        if (!nativeBridge.TryFocusGameWindow())
        {
            throw new InvalidOperationException("Could not focus game window for probe.");
        }

        if (options.WorkerStartupDelayMs > 0)
        {
            await Task.Delay(options.WorkerStartupDelayMs, ct);
        }

        var probeRoot = Path.GetFullPath(
            string.IsNullOrWhiteSpace(options.ProbeOutputDirectory)
                ? Path.Combine("artifacts", "live_probe", DateTime.UtcNow.ToString("yyyyMMdd_HHmmss"))
                : options.ProbeOutputDirectory
        );
        Directory.CreateDirectory(probeRoot);

        var beforePath = Path.Combine(probeRoot, "before.png");
        var afterPath = Path.Combine(probeRoot, "after.png");
        if (!nativeBridge.CaptureGameWindowPng(beforePath))
        {
            throw new InvalidOperationException("Failed to capture pre-probe game window.");
        }

        var scriptExecuted = nativeBridge.ExecuteScanScript(options.ProbeScript!, options.ProbeStepDelayMs);
        if (options.ProbePostDelayMs > 0)
        {
            await Task.Delay(options.ProbePostDelayMs, ct);
        }

        if (!nativeBridge.CaptureGameWindowPng(afterPath))
        {
            throw new InvalidOperationException("Failed to capture post-probe game window.");
        }

        var payload = new
        {
            mode = "probe",
            script = options.ProbeScript,
            executed = scriptExecuted,
            beforePath,
            afterPath
        };
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        if (!string.IsNullOrWhiteSpace(options.OutputPath))
        {
            var outputPath = Path.GetFullPath(options.OutputPath);
            var directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
            await File.WriteAllTextAsync(outputPath, json + Environment.NewLine, ct);
        }

        Console.WriteLine(json);
        return scriptExecuted ? 0 : 2;
    }

    private static string ResolveRepoRoot(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var candidate = Path.GetFullPath(explicitPath);
            if (File.Exists(Path.Combine(candidate, "VerifierApp.sln")))
            {
                return candidate;
            }
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "VerifierApp.sln")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }

        var workingDirectory = Directory.GetCurrentDirectory();
        if (File.Exists(Path.Combine(workingDirectory, "VerifierApp.sln")))
        {
            return workingDirectory;
        }

        throw new InvalidOperationException("Could not resolve VerifierApp repository root.");
    }

    private static WorkerLaunch ResolveWorkerLaunch(string repoRoot)
    {
        var explicitPython = Environment.GetEnvironmentVariable("IKA_WORKER_PYTHON");
        var workerPython = ResolveOptionalFile(
            explicitPython,
            Path.Combine(repoRoot, "worker", ".venv", "Scripts", "python.exe")
        );
        var workerMain = ResolveOptionalFile(
            Environment.GetEnvironmentVariable("IKA_WORKER_MAIN"),
            Path.Combine(repoRoot, "worker", "main.py")
        );
        if (!string.IsNullOrWhiteSpace(workerPython) && !string.IsNullOrWhiteSpace(workerMain))
        {
            return new WorkerLaunch(workerPython, $"\"{workerMain}\"");
        }

        var workerExe = ResolveRequiredFile(
            Environment.GetEnvironmentVariable("IKA_WORKER_EXE_PATH"),
            Path.Combine(repoRoot, "worker", "dist", "VerifierWorker.exe"),
            Path.Combine(repoRoot, "src", "VerifierApp.UI", "Bundled", "VerifierWorker.exe")
        );
        return new WorkerLaunch(workerExe, null);
    }

    private static string ResolveNativeDll(string repoRoot) =>
        ResolveRequiredFile(
            Environment.GetEnvironmentVariable("IKA_NATIVE_DLL_PATH"),
            Path.Combine(repoRoot, "native", "ika_native", "build", "release", "ika_native.dll"),
            Path.Combine(repoRoot, "native", "ika_native", "build", "release", "Release", "ika_native.dll"),
            Path.Combine(repoRoot, "src", "VerifierApp.UI", "Bundled", "ika_native.dll")
        );

    private static string ResolveRequiredFile(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(candidate);
            if (File.Exists(fullPath))
            {
                return fullPath;
            }
        }

        throw new InvalidOperationException($"Required file not found. Checked: {string.Join(", ", candidates.Where(static value => !string.IsNullOrWhiteSpace(value)))}");
    }

    private static string? ResolveOptionalDirectory(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(candidate);
            if (Directory.Exists(fullPath))
            {
                return fullPath;
            }
        }

        return null;
    }

    private static string? ResolveOptionalFile(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(candidate);
            if (File.Exists(fullPath))
            {
                return fullPath;
            }
        }

        return null;
    }

    private static void PrependDirectoryToPath(string? directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return;
        }

        var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var parts = currentPath
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Any(part => string.Equals(part, directoryPath, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var updated = string.IsNullOrWhiteSpace(currentPath)
            ? directoryPath
            : directoryPath + Path.PathSeparator + currentPath;
        Environment.SetEnvironmentVariable("PATH", updated);
    }

    private static void EnsureProcessEnvironmentDefault(string name, string value)
    {
        var existing = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(existing))
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    private sealed record Options(
        string Region,
        string Locale,
        string Resolution,
        bool FullSync,
        string PipeName,
        int ConnectTimeoutMs,
        int WorkerStartupDelayMs,
        string? OutputPath,
        string? RepoRoot,
        string? ProbeScript,
        string? ProbeOutputDirectory,
        int ProbeStepDelayMs,
        int ProbePostDelayMs
    )
    {
        public static Options Parse(string[] args)
        {
            var region = "EU";
            var locale = "RU";
            var resolution = "1080p";
            var fullSync = false;
            var pipeName = "ika_verifier_worker";
            var connectTimeoutMs = 15_000;
            var workerStartupDelayMs = 1_500;
            string? outputPath = null;
            string? repoRoot = null;
            string? probeScript = null;
            string? probeOutputDirectory = null;
            var probeStepDelayMs = 120;
            var probePostDelayMs = 500;

            for (var index = 0; index < args.Length; index++)
            {
                var arg = args[index];
                switch (arg)
                {
                    case "--region":
                        region = RequireValue(args, ref index, arg).Trim().ToUpperInvariant();
                        break;
                    case "--locale":
                        locale = RequireValue(args, ref index, arg).Trim().ToUpperInvariant();
                        break;
                    case "--resolution":
                        resolution = RequireValue(args, ref index, arg).Trim().ToLowerInvariant();
                        break;
                    case "--full-sync":
                        fullSync = true;
                        break;
                    case "--pipe-name":
                        pipeName = RequireValue(args, ref index, arg).Trim();
                        break;
                    case "--connect-timeout-ms":
                        connectTimeoutMs = ParsePositiveInt(RequireValue(args, ref index, arg), arg);
                        break;
                    case "--worker-startup-delay-ms":
                        workerStartupDelayMs = ParseNonNegativeInt(RequireValue(args, ref index, arg), arg);
                        break;
                    case "--out":
                        outputPath = RequireValue(args, ref index, arg).Trim();
                        break;
                    case "--repo-root":
                        repoRoot = RequireValue(args, ref index, arg).Trim();
                        break;
                    case "--probe-script":
                        probeScript = RequireValue(args, ref index, arg);
                        break;
                    case "--probe-out-dir":
                        probeOutputDirectory = RequireValue(args, ref index, arg).Trim();
                        break;
                    case "--probe-step-delay-ms":
                        probeStepDelayMs = ParsePositiveInt(RequireValue(args, ref index, arg), arg);
                        break;
                    case "--probe-post-delay-ms":
                        probePostDelayMs = ParseNonNegativeInt(RequireValue(args, ref index, arg), arg);
                        break;
                    case "--help":
                    case "-h":
                        PrintUsage();
                        Environment.Exit(0);
                        break;
                    default:
                        throw new InvalidOperationException($"Unknown argument: {arg}");
                }
            }

            return new Options(
                Region: region,
                Locale: locale,
                Resolution: resolution,
                FullSync: fullSync,
                PipeName: pipeName,
                ConnectTimeoutMs: connectTimeoutMs,
                WorkerStartupDelayMs: workerStartupDelayMs,
                OutputPath: outputPath,
                RepoRoot: repoRoot,
                ProbeScript: probeScript,
                ProbeOutputDirectory: probeOutputDirectory,
                ProbeStepDelayMs: probeStepDelayMs,
                ProbePostDelayMs: probePostDelayMs
            );
        }

        private static string RequireValue(string[] args, ref int index, string argumentName)
        {
            if (index + 1 >= args.Length)
            {
                throw new InvalidOperationException($"Missing value for {argumentName}");
            }

            index += 1;
            return args[index];
        }

        private static int ParsePositiveInt(string raw, string argumentName)
        {
            if (!int.TryParse(raw, out var value) || value <= 0)
            {
                throw new InvalidOperationException($"{argumentName} must be a positive integer.");
            }
            return value;
        }

        private static int ParseNonNegativeInt(string raw, string argumentName)
        {
            if (!int.TryParse(raw, out var value) || value < 0)
            {
                throw new InvalidOperationException($"{argumentName} must be a non-negative integer.");
            }
            return value;
        }

        private static void PrintUsage()
        {
            Console.WriteLine(
                "Usage: VerifierApp.LiveScan [--region EU] [--locale RU] [--resolution 1080p] [--full-sync] " +
                "[--out path] [--repo-root path] [--pipe-name name] [--connect-timeout-ms 15000] " +
                "[--worker-startup-delay-ms 1500] [--probe-script \"RIGHT,ENTER\"] " +
                "[--probe-out-dir path] [--probe-step-delay-ms 120] [--probe-post-delay-ms 500]"
            );
        }
    }

    private sealed record WorkerLaunch(
        string ExecutablePath,
        string? ExtraArguments
    );
}
