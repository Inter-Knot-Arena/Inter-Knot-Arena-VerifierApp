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
