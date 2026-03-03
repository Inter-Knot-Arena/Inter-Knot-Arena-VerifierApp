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
        CancellationToken ct
    )
    {
        var locked = _nativeBridge.TryLockInput();
        try
        {
            var scan = await _worker.RunRosterScanAsync(
                new RosterScanCommand(
                    SessionId: Guid.NewGuid().ToString("N"),
                    RegionHint: regionHint,
                    FullSync: fullSync
                ),
                ct
            );
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
