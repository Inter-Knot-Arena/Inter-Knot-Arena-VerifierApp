using VerifierApp.Core.Models;

namespace VerifierApp.Core.Services;

public sealed class MatchMonitorService
{
    private readonly IVerifierApiClient _apiClient;
    private readonly IWorkerClient _worker;
    private readonly INativeBridge _nativeBridge;

    public MatchMonitorService(
        IVerifierApiClient apiClient,
        IWorkerClient worker,
        INativeBridge nativeBridge
    )
    {
        _apiClient = apiClient;
        _worker = worker;
        _nativeBridge = nativeBridge;
    }

    public async Task RunMatchAsync(
        string matchId,
        string userId,
        string locale,
        string resolution,
        Action<string, DetectionResult>? evidenceObserver,
        CancellationToken ct
    )
    {
        var session = await _apiClient.CreateMatchSessionAsync(matchId, ct);

        var precheckHash = _nativeBridge.CaptureFrameHash();
        var precheck = await _worker.RunPrecheckAsync(matchId, precheckHash, locale, resolution, ct);
        await SubmitAsync(matchId, userId, "PRECHECK", precheck, session.VerifierSessionToken, ct);
        evidenceObserver?.Invoke("PRECHECK", precheck);

        if (!session.RequireInrunCheck)
        {
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(session.InrunFrequencySec), ct);
            var inrunHash = _nativeBridge.CaptureFrameHash();
            var inrun = await _worker.RunInrunAsync(matchId, inrunHash, locale, resolution, ct);
            await SubmitAsync(matchId, userId, "INRUN", inrun, session.VerifierSessionToken, ct);
            evidenceObserver?.Invoke("INRUN", inrun);
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
        var frameHash = detection.FrameHash;
        if (string.IsNullOrWhiteSpace(frameHash))
        {
            frameHash = _nativeBridge.CaptureFrameHash();
        }

        var normalizedDetection = detection with
        {
            FrameHash = frameHash
        };

        var nonce = VerifierSignatureService.CreateNonce();
        var submission = new EvidenceSubmission(
            MatchId: matchId,
            UserId: userId,
            Type: type,
            Detection: normalizedDetection,
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
