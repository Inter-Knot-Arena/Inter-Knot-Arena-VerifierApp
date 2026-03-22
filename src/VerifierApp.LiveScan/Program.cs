using System.Diagnostics;
using System.Collections.Generic;
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
            EnsureProcessEnvironmentDefault("IKA_KEY_SCRIPT_BACKEND", "native");
            var nativeDll = ResolveNativeDll(repoRoot);
            PrependDirectoryToPath(Path.GetDirectoryName(nativeDll));

            if (!string.IsNullOrWhiteSpace(options.ProbeScript))
            {
                return await RunProbeAsync(options, cts.Token);
            }

            var workerLaunch = ResolveWorkerLaunch(repoRoot);
            var ocrRoot = ResolveOptionalDirectory(
                Environment.GetEnvironmentVariable("IKA_OCR_SCAN_ROOT"),
                Path.Combine(Path.GetDirectoryName(repoRoot) ?? repoRoot, "Inter-Knot Arena OCR_Scan"),
                Path.Combine(repoRoot, "external", "OCR_Scan")
            );
            var cvRoot = ResolveOptionalDirectory(
                Environment.GetEnvironmentVariable("IKA_CV_ROOT"),
                Path.Combine(Path.GetDirectoryName(repoRoot) ?? repoRoot, "Inter-Knot Arena CV"),
                Path.Combine(repoRoot, "external", "CV")
            );
            Console.Error.WriteLine($"[live-scan] repoRoot={repoRoot}");
            Console.Error.WriteLine($"[live-scan] ocrRoot={ocrRoot ?? "<null>"}");
            Console.Error.WriteLine($"[live-scan] cvRoot={cvRoot ?? "<null>"}");
            Console.Error.WriteLine($"[live-scan] pipeName={options.PipeName}");
            if (!string.IsNullOrWhiteSpace(ocrRoot))
            {
                var amplifierPath = Path.Combine(ocrRoot, "amplifier_identity.py");
                if (File.Exists(amplifierPath))
                {
                    Console.Error.WriteLine(
                        $"[live-scan] ocrAmplifierPath={amplifierPath} mtimeUtc={File.GetLastWriteTimeUtc(amplifierPath):O}"
                    );
                }
            }

            using var launcher = new WorkerProcessLauncher();
            launcher.Start(
                workerExecutablePath: workerLaunch.ExecutablePath,
                pipeName: options.PipeName,
                extraArguments: workerLaunch.ExtraArguments,
                pathPrependDirectory: workerLaunch.PathPrependDirectory,
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
        var status = nativeBridge.InspectGameWindowStatus();
        if (!status.CanInjectInput)
        {
            throw new InvalidOperationException(FormatGameWindowStatusFailure(status));
        }

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
            gameWindowStatus = status,
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

    private static string FormatGameWindowStatusFailure(GameWindowStatus status)
    {
        return status.BlockingIssue switch
        {
            "game_process_not_found" => "Could not start live probe: ZenlessZoneZero process was not found.",
            "game_window_missing" => "Could not start live probe: game window is not available.",
            "game_requires_elevated_verifier" =>
                "Could not start live probe: ZenlessZoneZero is running as administrator. Start VerifierApp.LiveScan as administrator too.",
            _ => "Could not start live probe: game window is not ready for live input."
        };
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
        var workerMain = ResolveOptionalFile(
            Environment.GetEnvironmentVariable("IKA_WORKER_MAIN"),
            Path.Combine(repoRoot, "worker", "main.py")
        );
        if (!string.IsNullOrWhiteSpace(workerMain))
        {
            var workerPython = ResolvePreferredWorkerPython(repoRoot, explicitPython);
            if (workerPython is not null)
            {
                return new WorkerLaunch(workerPython.ExecutablePath, $"\"{workerMain}\"", workerPython.PathPrependDirectory);
            }

            throw new InvalidOperationException("Could not find a CUDA-capable worker Python with onnxruntime-gpu and torch.");
        }

        var workerExe = ResolveRequiredFile(
            Environment.GetEnvironmentVariable("IKA_WORKER_EXE_PATH"),
            Path.Combine(repoRoot, "worker", "dist", "VerifierWorker.exe"),
            Path.Combine(repoRoot, "src", "VerifierApp.UI", "Bundled", "VerifierWorker.exe")
        );
        return new WorkerLaunch(workerExe, null, null);
    }

    private static WorkerPythonLaunch? ResolvePreferredWorkerPython(string repoRoot, string? explicitPython)
    {
        var probeModelPath = ResolveOptionalFile(
            Path.Combine(repoRoot, "external", "OCR_Scan", "models", "uid_digit.onnx"),
            Path.Combine(repoRoot, "external", "CV", "models", "cv_agent_icon.onnx")
        );
        var pythonCandidates = new[]
        {
            explicitPython,
            Path.Combine(repoRoot, "worker", ".venv", "Scripts", "python.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "Python",
                "Python312",
                "python.exe"
            ),
        };

        foreach (var candidate in pythonCandidates)
        {
            var resolved = ResolveOptionalFile(candidate);
            if (string.IsNullOrWhiteSpace(resolved))
            {
                continue;
            }

            var torchLibDirectory = ResolvePythonTorchLibDirectory(resolved);
            if (SupportsCudaWorkerPython(resolved, torchLibDirectory, probeModelPath))
            {
                return new WorkerPythonLaunch(resolved, torchLibDirectory);
            }
        }

        return null;
    }

    private static bool SupportsCudaWorkerPython(string pythonExecutablePath, string? torchLibDirectory, string? probeModelPath)
    {
        if (string.IsNullOrWhiteSpace(torchLibDirectory))
        {
            return false;
        }

        var scriptPath = Path.Combine(Path.GetTempPath(), $"verifier_cuda_probe_{Guid.NewGuid():N}.py");
        try
        {
            var modelPathLiteral = string.IsNullOrWhiteSpace(probeModelPath)
                ? "None"
                : ToPythonStringLiteral(probeModelPath);
            var torchLibLiteral = ToPythonStringLiteral(torchLibDirectory);
            var script = string.Join(Environment.NewLine, new[]
            {
                "import onnxruntime as ort",
                $"model_path = {modelPathLiteral}",
                "try:",
                "    from onnxruntime import datasets as ort_datasets",
                "    example = ort_datasets.get_example('sigmoid.onnx')",
                "    if example:",
                "        model_path = example",
                "except Exception:",
                "    pass",
                "",
                "if model_path is None:",
                "    raise RuntimeError('No probe model available.')",
                "",
                "if hasattr(ort, 'preload_dlls'):",
                $"    ort.preload_dlls(directory={torchLibLiteral})",
                "",
                "session = ort.InferenceSession(model_path, providers=['CUDAExecutionProvider'])",
                "providers = session.get_providers()",
                "if 'CUDAExecutionProvider' not in providers:",
                "    raise RuntimeError(f'CUDAExecutionProvider not active: {providers!r}')",
                "print('READY')"
            });
            File.WriteAllText(scriptPath, script);

            var startInfo = new ProcessStartInfo
            {
                FileName = pythonExecutablePath,
                Arguments = $"\"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            PrependDirectoryToPath(startInfo.Environment, torchLibDirectory);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            if (!process.WaitForExit(15000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // ignored
                }
                return false;
            }

            var stdout = process.StandardOutput.ReadToEnd().Trim();
            return process.ExitCode == 0 &&
                   string.Equals(stdout, "READY", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(scriptPath))
                {
                    File.Delete(scriptPath);
                }
            }
            catch
            {
                // ignored
            }
        }
    }

    private static string? ResolvePythonTorchLibDirectory(string pythonExecutablePath)
    {
        var pythonDirectory = Path.GetDirectoryName(pythonExecutablePath);
        if (string.IsNullOrWhiteSpace(pythonDirectory))
        {
            return null;
        }

        var candidateRoots = new[]
        {
            pythonDirectory,
            Directory.GetParent(pythonDirectory)?.FullName
        };

        foreach (var candidateRoot in candidateRoots.Where(static root => !string.IsNullOrWhiteSpace(root)))
        {
            var torchLibDirectory = Path.GetFullPath(Path.Combine(candidateRoot!, "Lib", "site-packages", "torch", "lib"));
            if (Directory.Exists(torchLibDirectory))
            {
                return torchLibDirectory;
            }
        }

        return null;
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
        Environment.SetEnvironmentVariable("PATH", UpdatePathValue(currentPath, directoryPath));
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
        environment["PATH"] = UpdatePathValue(currentPath, directoryPath);
    }

    private static string UpdatePathValue(string currentPath, string directoryPath)
    {
        var parts = currentPath
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Any(part => string.Equals(part, directoryPath, StringComparison.OrdinalIgnoreCase)))
        {
            return currentPath;
        }

        return string.IsNullOrWhiteSpace(currentPath)
            ? directoryPath
            : directoryPath + Path.PathSeparator + currentPath;
    }

    private static string ToPythonStringLiteral(string value)
    {
        return "'" + value.Replace("\\", "\\\\").Replace("'", "\\'") + "'";
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
            var pipeName = $"ika_verifier_worker_{Guid.NewGuid():N}";
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
        string? ExtraArguments,
        string? PathPrependDirectory
    );

    private sealed record WorkerPythonLaunch(
        string ExecutablePath,
        string? PathPrependDirectory
    );
}
