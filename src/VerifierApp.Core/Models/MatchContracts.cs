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
    IReadOnlyList<string>? UnexpectedAgents,
    string Result,
    IReadOnlyDictionary<string, double> Confidence,
    IReadOnlyDictionary<string, double>? ConfidenceByField,
    string FrameHash,
    string ModelVersion,
    IReadOnlyList<string>? LowConfReasons,
    double? TimingMs,
    string? Resolution,
    string? Locale
);

public sealed record EvidenceSubmission(
    string MatchId,
    string UserId,
    string Type,
    DetectionResult Detection,
    string VerifierSessionToken,
    string VerifierNonce,
    string VerifierSignature
);
