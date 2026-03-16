using VerifierApp.Core.Models;

namespace VerifierApp.Core.Services;

public sealed class ScanOrchestrator
{
    private readonly IVerifierApiClient _apiClient;
    private readonly IWorkerClient _worker;
    private readonly INativeBridge _nativeBridge;

    public ScanOrchestrator(
        IVerifierApiClient apiClient,
        IWorkerClient worker,
        INativeBridge nativeBridge
    )
    {
        _apiClient = apiClient;
        _worker = worker;
        _nativeBridge = nativeBridge;
    }

    public async Task<RosterImportResult> ExecuteRosterScanAsync(
        string regionHint,
        bool fullSync,
        string locale,
        string resolution,
        CancellationToken ct
    )
    {
        if (fullSync)
        {
            throw new InvalidOperationException(
                "Full sync is blocked: the worker currently scans only the visible roster slice, not the full account."
            );
        }

        var locked = _nativeBridge.TryLockInput();
        if (!locked)
        {
            throw new InvalidOperationException("Scan aborted: OS input lock failed.");
        }

        try
        {
            var sessionId = Guid.NewGuid().ToString("N");
            var scanScript = Environment.GetEnvironmentVariable("IKA_SCAN_SCRIPT");
            if (string.IsNullOrWhiteSpace(scanScript))
            {
                scanScript = "ESC,TAB,TAB,ENTER";
            }
            var scanStepDelayMs = ReadPositiveIntFromEnvironment("IKA_SCAN_SCRIPT_STEP_DELAY_MS", 120);

            if (!_nativeBridge.ExecuteScanScript(scanScript, scanStepDelayMs))
            {
                throw new InvalidOperationException("Scan aborted: native scan automation script failed.");
            }

            var runtimeCaptures = await CaptureExtraScreensAsync(sessionId, ct);
            var scan = await _worker.RunRosterScanAsync(
                new RosterScanCommand(
                    SessionId: sessionId,
                    RegionHint: regionHint,
                    FullSync: fullSync,
                    Locale: locale,
                    Resolution: resolution,
                    InputLockActive: true,
                    ScreenCaptures: runtimeCaptures.Count > 0 ? runtimeCaptures : null
                ),
                ct
            );

            if (!string.IsNullOrWhiteSpace(scan.ErrorCode))
            {
                throw new InvalidOperationException(
                    $"Scan aborted [{scan.ErrorCode}]: {scan.ErrorMessage ?? "worker scan failure"}."
                );
            }

            if (string.IsNullOrWhiteSpace(scan.Uid))
            {
                throw new InvalidOperationException("Scan aborted: UID was not extracted.");
            }

            var importResult = await _apiClient.ImportRosterAsync(scan, ct);
            if (scan.LowConfReasons is { Count: > 0 })
            {
                var warning = $"Imported with low confidence ({string.Join(", ", scan.LowConfReasons)}).";
                var message = string.IsNullOrWhiteSpace(importResult.Message)
                    ? warning
                    : $"{importResult.Message} {warning}";
                var status = string.Equals(importResult.Status, "OK", StringComparison.OrdinalIgnoreCase)
                    ? "DEGRADED"
                    : importResult.Status;
                return importResult with
                {
                    Status = status,
                    Message = message
                };
            }

            return importResult;
        }
        finally
        {
            if (locked)
            {
                _nativeBridge.UnlockInput();
            }
        }
    }

    private static int ReadPositiveIntFromEnvironment(string envVar, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(envVar);
        return !string.IsNullOrWhiteSpace(raw) &&
               int.TryParse(raw, out var parsed) &&
               parsed > 0
            ? parsed
            : fallback;
    }

    private async Task<IReadOnlyList<ScreenCaptureInput>> CaptureExtraScreensAsync(
        string sessionId,
        CancellationToken ct
    )
    {
        var plan = RuntimeScreenCapturePlan.LoadFromEnvironment();
        if (plan.Count == 0)
        {
            return [];
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "ika_verifier", "screen_captures", sessionId);
        Directory.CreateDirectory(tempRoot);

        var captures = new List<ScreenCaptureInput>();
        for (var index = 0; index < plan.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var step = plan[index];
            if (!_nativeBridge.ExecuteScanScript(step.Script ?? string.Empty, step.StepDelayMs))
            {
                throw new InvalidOperationException(
                    $"Scan aborted: extra capture automation failed for step {index + 1} ({step.Role})."
                );
            }

            if (step.PostDelayMs > 0)
            {
                await Task.Delay(step.PostDelayMs, ct);
            }

            if (!step.Capture)
            {
                continue;
            }

            var fileName = BuildCaptureFileName(index + 1, step);
            var outputPath = Path.Combine(tempRoot, fileName);
            if (!_nativeBridge.CaptureDesktopPng(outputPath))
            {
                throw new InvalidOperationException(
                    $"Scan aborted: desktop capture failed for step {index + 1} ({step.Role})."
                );
            }

            captures.Add(
                new ScreenCaptureInput(
                    Role: step.Role,
                    Path: outputPath,
                    SlotIndex: step.SlotIndex,
                    AgentSlotIndex: step.AgentSlotIndex,
                    ScreenAlias: step.ScreenAlias
                )
            );
        }

        return captures;
    }

    private static string BuildCaptureFileName(int ordinal, RuntimeScreenCaptureStep step)
    {
        var alias = string.IsNullOrWhiteSpace(step.ScreenAlias)
            ? step.Role
            : step.ScreenAlias;
        var safeAlias = string.Concat(alias.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')).Trim('_');
        if (string.IsNullOrWhiteSpace(safeAlias))
        {
            safeAlias = step.Role;
        }

        var parts = new List<string>
        {
            ordinal.ToString("D2"),
            step.Role,
            $"agent_slot_{step.AgentSlotIndex}"
        };
        if (step.SlotIndex is int slotIndex)
        {
            parts.Add($"slot_{slotIndex}");
        }
        parts.Add(safeAlias);
        return $"{string.Join("_", parts)}.png";
    }
}
