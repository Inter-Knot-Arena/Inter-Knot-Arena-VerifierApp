namespace VerifierApp.Core.Models;

public sealed record MatchVerifierSession(
    string MatchId,
    string VerifierSessionToken,
    long ExpiresAt,
    int PrecheckFrequencySec,
    int InrunFrequencySec,
    bool RequireInrunCheck
);

public sealed record DetectionResult(
    string Type,
    IReadOnlyList<string> DetectedAgents,
    string Result,
    IReadOnlyDictionary<string, double> Confidence,
    string FrameHash,
    string ModelVersion
);

public sealed record EvidenceSubmission(
    string MatchId,
    string Type,
    DetectionResult Detection,
    string VerifierSessionToken,
    string VerifierNonce,
    string VerifierSignature
);
