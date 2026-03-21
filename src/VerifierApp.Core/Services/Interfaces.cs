using VerifierApp.Core.Models;

namespace VerifierApp.Core.Services;

public sealed record GameWindowStatus(
    bool GameProcessFound,
    bool GameWindowFound,
    bool CurrentProcessElevated,
    bool GameProcessElevated,
    int? GameProcessId,
    string? MainWindowTitle,
    string? BlockingIssue
)
{
    public bool CanInjectInput =>
        GameProcessFound &&
        GameWindowFound &&
        string.IsNullOrWhiteSpace(BlockingIssue);
}

public interface IVerifierApiClient
{
    Task<VerifierDeviceStartResponse> StartDeviceAuthAsync(VerifierDeviceStartRequest request, CancellationToken ct);
    Task<VerifierTokens> ExchangeDeviceCodeAsync(VerifierDeviceExchangeRequest request, CancellationToken ct);
    Task<VerifierTokens> RefreshVerifierTokenAsync(string refreshToken, string? currentUserId, CancellationToken ct);
    Task RevokeTokenAsync(string token, CancellationToken ct);
    Task<VerifierAuthUser> GetCurrentUserAsync(CancellationToken ct);
    Task<RosterImportResult> ImportRosterAsync(RosterScanResult result, CancellationToken ct);
    Task<MatchVerifierSession> CreateMatchSessionAsync(string matchId, CancellationToken ct);
    Task SubmitEvidenceAsync(EvidenceSubmission submission, CancellationToken ct);
}

public interface IWorkerClient
{
    Task<RosterScanResult> RunRosterScanAsync(RosterScanCommand command, CancellationToken ct);
    Task<EquipmentOverviewInspectionResult> InspectEquipmentOverviewAsync(string imagePath, CancellationToken ct);
    Task<DetectionResult> RunPrecheckAsync(
        string matchId,
        string? frameHashHint,
        IReadOnlyList<string> expectedAgents,
        IReadOnlyList<string> bannedAgents,
        string locale,
        string resolution,
        CancellationToken ct
    );

    Task<DetectionResult> RunInrunAsync(
        string matchId,
        string? frameHashHint,
        IReadOnlyList<string> expectedAgents,
        IReadOnlyList<string> bannedAgents,
        string locale,
        string resolution,
        CancellationToken ct
    );

    Task<bool> HealthAsync(CancellationToken ct);
}

public interface INativeBridge
{
    string CurrentInputLockMode { get; }
    GameWindowStatus InspectGameWindowStatus();
    bool TryFocusGameWindow();
    bool TryLockInput(bool preferSoft = false);
    void UnlockInput();
    bool ExecuteScanScript(string script, int stepDelayMs);
    string CaptureFrameHash();
    bool CaptureGameWindowPng(string outputPath);
    bool CaptureDesktopPng(string outputPath);
}

public interface ITokenStore
{
    Task SaveAsync(VerifierTokens tokens, CancellationToken ct);
    Task<VerifierTokens?> ReadAsync(CancellationToken ct);
    Task ClearAsync(CancellationToken ct);
}
