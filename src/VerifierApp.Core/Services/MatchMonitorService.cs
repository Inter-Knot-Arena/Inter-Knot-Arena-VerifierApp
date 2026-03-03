using VerifierApp.Core.Models;

namespace VerifierApp.Core.Services;

public sealed class MatchMonitorService
{
    private readonly IVerifierApiClient _apiClient;
    private readonly IWorkerClient _worker;

    public MatchMonitorService(
        IVerifierApiClient apiClient,
        IWorkerClient worker
    )
    {
        _apiClient = apiClient;
        _worker = worker;
    }

    public async Task RunMatchAsync(
        string matchId,
        string userId,
        CancellationToken ct
    )
    {
        var session = await _apiClient.CreateMatchSessionAsync(matchId, ct);

        var precheck = await _worker.RunPrecheckAsync(matchId, ct);
        await SubmitAsync(matchId, userId, "PRECHECK", precheck, session.VerifierSessionToken, ct);

        if (!session.RequireInrunCheck)
        {
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(session.InrunFrequencySec), ct);
            var inrun = await _worker.RunInrunAsync(matchId, ct);
            await SubmitAsync(matchId, userId, "INRUN", inrun, session.VerifierSessionToken, ct);
        }
    }

    private async Task SubmitAsync(
        string matchId,
        string userId,
        string type,
        DetectionResult detection,
        string verifierSessionToken,
        CancellationToken ct
    )
    {
        var nonce = VerifierSignatureService.CreateNonce();
        var submission = new EvidenceSubmission(
            MatchId: matchId,
            UserId: userId,
            Type: type,
            Detection: detection,
            VerifierSessionToken: verifierSessionToken,
            VerifierNonce: nonce,
            VerifierSignature: string.Empty
        );
        var signed = submission with
        {
            VerifierSignature = VerifierSignatureService.BuildEvidenceSignature(submission)
        };
        await _apiClient.SubmitEvidenceAsync(signed, ct);
    }
}
