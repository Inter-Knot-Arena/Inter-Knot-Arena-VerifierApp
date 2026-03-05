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
        var locked = _nativeBridge.TryLockInput();
        if (!locked)
        {
            throw new InvalidOperationException("Scan aborted: OS input lock failed.");
        }

        try
        {
            var scanScript = Environment.GetEnvironmentVariable("IKA_SCAN_SCRIPT");
            if (string.IsNullOrWhiteSpace(scanScript))
            {
                scanScript = "ESC,TAB,TAB,ENTER";
            }
            var scanStepDelayRaw = Environment.GetEnvironmentVariable("IKA_SCAN_SCRIPT_STEP_DELAY_MS");
            var scanStepDelayMs = 120;
            if (!string.IsNullOrWhiteSpace(scanStepDelayRaw) &&
                int.TryParse(scanStepDelayRaw, out var parsedDelay) &&
                parsedDelay > 0)
            {
                scanStepDelayMs = parsedDelay;
            }

            if (!_nativeBridge.ExecuteScanScript(scanScript, scanStepDelayMs))
            {
                throw new InvalidOperationException("Scan aborted: native scan automation script failed.");
            }

            var scan = await _worker.RunRosterScanAsync(
                new RosterScanCommand(
                    SessionId: Guid.NewGuid().ToString("N"),
                    RegionHint: regionHint,
                    FullSync: fullSync,
                    Locale: locale,
                    Resolution: resolution,
                    InputLockActive: true
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

            if (scan.LowConfReasons is { Count: > 0 })
            {
                throw new InvalidOperationException(
                    $"Scan aborted: low confidence ({string.Join(", ", scan.LowConfReasons)})."
                );
            }

            return await _apiClient.ImportRosterAsync(scan, ct);
        }
        finally
        {
            if (locked)
            {
                _nativeBridge.UnlockInput();
            }
        }
    }
}
