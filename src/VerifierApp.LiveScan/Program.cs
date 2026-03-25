using System.Diagnostics;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using VerifierApp.Core.Models;
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
            WriteDiagnostic("boot-start");
            EnsureProcessEnvironmentDefault("IKA_ALLOW_SOFT_INPUT_LOCK", "1");
            EnsureProcessEnvironmentDefault("IKA_DEFAULT_OCR_CAPTURE_PLAN", "VISIBLE_SLICE_AGENT_DETAIL_EQUIPMENT_AMP_BETA");
            EnsureProcessEnvironmentDefault("IKA_KEY_SCRIPT_BACKEND", "native");
            WriteDiagnostic("boot-env-ready");
            var bundleSidecarRoot = ResolveBundledSidecarRoot(options.BundleRoot, options.RepoRoot);
            var repoRoot = bundleSidecarRoot is null ? ResolveRepoRoot(options.RepoRoot) : null;
            WriteDiagnostic($"boot-paths bundleSidecarRoot={bundleSidecarRoot ?? "<null>"} repoRoot={repoRoot ?? "<null>"}");
            BundledAssetPaths? bundledAssets = null;
            var nativeDll = string.Empty;
            WorkerLaunch workerLaunch;
            string? ocrRoot;
            string? cvRoot;

            if (!string.IsNullOrWhiteSpace(bundleSidecarRoot))
            {
                WriteDiagnostic("boot-bundle-extract-start");
                bundledAssets = BundledAssetManager.EnsureExtracted(
                    Assembly.GetExecutingAssembly(),
                    bundleSidecarRoot
                );
                WriteDiagnostic($"boot-bundle-extract-complete root={bundledAssets.RootPath}");
                nativeDll = bundledAssets.NativeDllPath;
                workerLaunch = new WorkerLaunch(
                    bundledAssets.WorkerExePath,
                    null,
                    bundledAssets.CudaRoot
                );
                ocrRoot = bundledAssets.OcrScanRoot;
                cvRoot = bundledAssets.CvRoot;
            }
            else
            {
                WriteDiagnostic("boot-repo-resolve-start");
                nativeDll = ResolveNativeDll(repoRoot!);
                workerLaunch = ResolveWorkerLaunch(repoRoot!);
                ocrRoot = ResolveOptionalDirectory(
                    Environment.GetEnvironmentVariable("IKA_OCR_SCAN_ROOT"),
                    Path.Combine(Path.GetDirectoryName(repoRoot!) ?? repoRoot!, "Inter-Knot Arena OCR_Scan"),
                    Path.Combine(repoRoot!, "external", "OCR_Scan")
                );
                cvRoot = ResolveOptionalDirectory(
                    Environment.GetEnvironmentVariable("IKA_CV_ROOT"),
                    Path.Combine(Path.GetDirectoryName(repoRoot!) ?? repoRoot!, "Inter-Knot Arena CV"),
                    Path.Combine(repoRoot!, "external", "CV")
                );
                WriteDiagnostic("boot-repo-resolve-complete");
            }

            PrependDirectoryToPath(Path.GetDirectoryName(nativeDll));
            WriteDiagnostic($"boot-native-bootstrap-start dll={nativeDll}");
            NativeLibraryBootstrap.Initialize(nativeDll);
            WriteDiagnostic("boot-native-bootstrap-complete");
            var nativeBridge = new NativeBridge();
            WriteDiagnostic("boot-native-bridge-ready");

            if (!string.IsNullOrWhiteSpace(options.ProbeScript))
            {
                WriteDiagnostic("boot-probe-start");
                return await RunProbeAsync(options, cts.Token);
            }

            WriteDiagnostic($"mode={(bundledAssets is null ? "repo" : "bundled")}");
            WriteDiagnostic($"repoRoot={repoRoot ?? "<null>"}");
            WriteDiagnostic($"bundleSidecarRoot={bundleSidecarRoot ?? "<null>"}");
            WriteDiagnostic($"bundleExtractRoot={bundledAssets?.RootPath ?? "<null>"}");
            WriteDiagnostic($"ocrRoot={ocrRoot ?? "<null>"}");
            WriteDiagnostic($"cvRoot={cvRoot ?? "<null>"}");
            if (bundledAssets is not null)
            {
                var bundleManifestPath = bundledAssets.ManifestPath;
                WriteDiagnostic($"bundleManifestPath={bundleManifestPath}");
                WriteDiagnostic(
                    $"bundleManifestSummary={JsonSerializer.Serialize(ReadBundleManifestSummary(bundleManifestPath), JsonOptions)}"
                );
            }
            WriteDiagnostic($"pipeName={options.PipeName}");
            if (!string.IsNullOrWhiteSpace(ocrRoot))
            {
                var amplifierPath = Path.Combine(ocrRoot, "amplifier_identity.py");
                if (File.Exists(amplifierPath))
                {
                    WriteDiagnostic(
                        $"ocrAmplifierPath={amplifierPath} mtimeUtc={File.GetLastWriteTimeUtc(amplifierPath):O}"
                    );
                }
            }

            using var launcher = new WorkerProcessLauncher();
            launcher.Start(
                workerExecutablePath: workerLaunch.ExecutablePath,
                pipeName: options.PipeName,
                extraArguments: workerLaunch.ExtraArguments,
                pathPrependDirectory: workerLaunch.PathPrependDirectory,
                bundleRoot: bundledAssets?.RootPath,
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
            var workerHealthDetails = await worker.HealthDetailsAsync(cts.Token);
            WriteDiagnostic(
                $"workerHealthDetails={JsonSerializer.Serialize(workerHealthDetails, JsonOptions)}"
            );

            var orchestrator = new ScanOrchestrator(worker, nativeBridge);
            var scanStopwatch = Stopwatch.StartNew();
            var result = await orchestrator.CaptureRosterScanAsync(
                regionHint: options.Region,
                fullSync: options.FullSync,
                locale: options.Locale,
                resolution: options.Resolution,
                scanProfile: options.ScanProfile,
                ct: cts.Token
            );
            scanStopwatch.Stop();
            result = result with
            {
                ScanMeta = AppendScanMetaToken(
                    result.ScanMeta,
                    $"verifier_wall_ms_{Math.Round(scanStopwatch.Elapsed.TotalMilliseconds, MidpointRounding.AwayFromZero)}"
                )
            };
            WriteDiagnostic(
                $"wallMs={scanStopwatch.Elapsed.TotalMilliseconds:F2}"
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
            WriteDiagnostic("cancelled");
            return 130;
        }
        catch (Exception ex)
        {
            WriteDiagnostic($"error={ex.Message}");
            return 1;
        }
    }

    private static void WriteDiagnostic(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var formatted = $"[live-scan] {message}";
        TryAppendDiagnosticTrace(formatted);

        if (!ReadBooleanFlagFromEnvironment("IKA_LIVE_SCAN_WRITE_CONSOLE_DIAGNOSTICS", false))
        {
            return;
        }

        try
        {
            Console.Error.WriteLine(formatted);
        }
        catch
        {
            // ignored
        }
    }

    private static void TryAppendDiagnosticTrace(string message)
    {
        var rawPath = Environment.GetEnvironmentVariable("IKA_LIVE_SCAN_TRACE_PATH");
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return;
        }

        try
        {
            var fullPath = Path.GetFullPath(rawPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.AppendAllText(fullPath, $"[{DateTime.UtcNow:O}] {message}{Environment.NewLine}");
        }
        catch
        {
            // ignored
        }
    }

    private static string AppendScanMetaToken(string existing, string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return existing;
        }
        if (string.IsNullOrWhiteSpace(existing))
        {
            return token;
        }
        if (existing.Contains(token, StringComparison.OrdinalIgnoreCase))
        {
            return existing;
        }
        return $"{existing}+{token}";
    }

    private static async Task<int> RunProbeAsync(Options options, CancellationToken ct)
    {
        WriteDiagnostic("probe-enter");
        var nativeBridge = new NativeBridge();
        var status = nativeBridge.InspectGameWindowStatus();
        WriteDiagnostic(
            $"probe-window-status canInject={status.CanInjectInput.ToString().ToLowerInvariant()} blocking={status.BlockingIssue ?? "<null>"} elevated={status.CurrentProcessElevated.ToString().ToLowerInvariant()}"
        );
        if (!status.CanInjectInput)
        {
            throw new InvalidOperationException(FormatGameWindowStatusFailure(status));
        }

        WriteDiagnostic("probe-focus-start");
        if (!nativeBridge.TryFocusGameWindow())
        {
            throw new InvalidOperationException("Could not focus game window for probe.");
        }
        WriteDiagnostic("probe-focus-complete");

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
        WriteDiagnostic($"probe-capture-before-start path={beforePath}");
        if (!nativeBridge.CaptureGameWindowPng(beforePath))
        {
            throw new InvalidOperationException("Failed to capture pre-probe game window.");
        }
        WriteDiagnostic("probe-capture-before-complete");

        WriteDiagnostic($"probe-script-start script={options.ProbeScript}");
        var scriptExecuted = nativeBridge.ExecuteScanScript(options.ProbeScript!, options.ProbeStepDelayMs);
        WriteDiagnostic($"probe-script-complete executed={scriptExecuted.ToString().ToLowerInvariant()}");
        if (options.ProbePostDelayMs > 0)
        {
            await Task.Delay(options.ProbePostDelayMs, ct);
        }

        WriteDiagnostic($"probe-capture-after-start path={afterPath}");
        if (!nativeBridge.CaptureGameWindowPng(afterPath))
        {
            throw new InvalidOperationException("Failed to capture post-probe game window.");
        }
        WriteDiagnostic("probe-capture-after-complete");

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

        WriteDiagnostic("probe-complete");
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

    private static Dictionary<string, object?> ReadBundleManifestSummary(string manifestPath)
    {
        var summary = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["exists"] = File.Exists(manifestPath),
            ["path"] = manifestPath
        };
        if (!File.Exists(manifestPath))
        {
            return summary;
        }

        try
        {
            using var stream = File.OpenRead(manifestPath);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.TryGetProperty("generatedAt", out var generatedAt))
            {
                summary["generatedAt"] = generatedAt.GetString();
            }
            if (document.RootElement.TryGetProperty("sources", out var sources) && sources.ValueKind == JsonValueKind.Object)
            {
                var sourceSummary = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in sources.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var source = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    if (property.Value.TryGetProperty("sourceKind", out var sourceKind))
                    {
                        source["sourceKind"] = sourceKind.GetString();
                    }
                    if (property.Value.TryGetProperty("sourceDir", out var sourceDir))
                    {
                        source["sourceDir"] = sourceDir.GetString();
                    }
                    if (property.Value.TryGetProperty("branch", out var branch))
                    {
                        source["branch"] = branch.GetString();
                    }
                    if (property.Value.TryGetProperty("commit", out var commit))
                    {
                        source["commit"] = commit.GetString();
                    }
                    sourceSummary[property.Name] = source;
                }
                summary["sources"] = sourceSummary;
            }
        }
        catch (Exception ex)
        {
            summary["error"] = ex.Message;
        }

        return summary;
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

    private static string? ResolveBundledSidecarRoot(string? explicitPath, string? repoRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var fullPath = Path.GetFullPath(explicitPath);
            if (!Directory.Exists(fullPath))
            {
                throw new InvalidOperationException($"Bundled sidecar root does not exist: {fullPath}");
            }

            return fullPath;
        }

        var envRoot = Environment.GetEnvironmentVariable("IKA_BUNDLE_SIDECAR_ROOT");
        if (string.IsNullOrWhiteSpace(envRoot))
        {
            envRoot = Environment.GetEnvironmentVariable("IKA_BUNDLE_ROOT");
        }
        if (!string.IsNullOrWhiteSpace(envRoot))
        {
            var fullPath = Path.GetFullPath(envRoot);
            if (!Directory.Exists(fullPath))
            {
                throw new InvalidOperationException($"Bundled sidecar root does not exist: {fullPath}");
            }

            return fullPath;
        }

        if (!string.IsNullOrWhiteSpace(repoRoot))
        {
            return null;
        }

        return LooksLikeBundledSidecarRoot(AppContext.BaseDirectory)
            ? AppContext.BaseDirectory
            : null;
    }

    private static bool LooksLikeBundledSidecarRoot(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || !Directory.Exists(candidate))
        {
            return false;
        }

        return File.Exists(Path.Combine(candidate, "bundle.manifest.json")) &&
               File.Exists(Path.Combine(candidate, "VerifierWorker_bundle.zip")) &&
               File.Exists(Path.Combine(candidate, "ocr_scan_bundle.zip")) &&
               File.Exists(Path.Combine(candidate, "cv_bundle.zip")) &&
               File.Exists(Path.Combine(candidate, "ika_native.dll")) &&
               Directory.Exists(Path.Combine(candidate, "cuda"));
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
            Path.Combine(repoRoot, "worker", "dist", "VerifierWorker", "VerifierWorker.exe")
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

    private static bool ReadBooleanFlagFromEnvironment(string envVar, bool fallback)
    {
        var raw = Environment.GetEnvironmentVariable(envVar);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => fallback,
        };
    }

    private sealed record Options(
        string Region,
        string Locale,
        string Resolution,
        RosterScanProfile ScanProfile,
        bool FullSync,
        string PipeName,
        int ConnectTimeoutMs,
        int WorkerStartupDelayMs,
        string? OutputPath,
        string? RepoRoot,
        string? BundleRoot,
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
            var resolution = "auto";
            var scanProfile = RosterScanProfile.Deep;
            var fullSync = false;
            var pipeName = $"ika_verifier_worker_{Guid.NewGuid():N}";
            var connectTimeoutMs = 15_000;
            var workerStartupDelayMs = 1_500;
            string? outputPath = null;
            string? repoRoot = null;
            string? bundleRoot = null;
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
                    case "--scan-profile":
                        scanProfile = ParseScanProfile(RequireValue(args, ref index, arg), arg);
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
                    case "--bundle-root":
                        bundleRoot = RequireValue(args, ref index, arg).Trim();
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
                ScanProfile: scanProfile,
                FullSync: fullSync,
                PipeName: pipeName,
                ConnectTimeoutMs: connectTimeoutMs,
                WorkerStartupDelayMs: workerStartupDelayMs,
                OutputPath: outputPath,
                RepoRoot: repoRoot,
                BundleRoot: bundleRoot,
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

        private static RosterScanProfile ParseScanProfile(string value, string argumentName)
        {
            return value.Trim().ToLowerInvariant() switch
            {
                "fast" => RosterScanProfile.Fast,
                "deep" => RosterScanProfile.Deep,
                _ => throw new InvalidOperationException(
                    $"{argumentName} must be one of: fast, deep."
                )
            };
        }

        private static void PrintUsage()
        {
            Console.WriteLine(
                "Usage: VerifierApp.LiveScan [--region EU] [--locale RU] [--resolution auto|1080p|1440p] [--scan-profile fast|deep(default=deep)] [--full-sync] " +
                "[--out path] [--repo-root path] [--bundle-root path] [--pipe-name name] [--connect-timeout-ms 15000] " +
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
