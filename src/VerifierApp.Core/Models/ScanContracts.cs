namespace VerifierApp.Core.Models;

public sealed record RosterScanCommand(
    string SessionId,
    string RegionHint,
    bool FullSync
);

public sealed record AgentScanResult(
    string AgentId,
    bool Owned,
    double? Level,
    double? Mindscape,
    IReadOnlyDictionary<string, double>? ConfidenceByField
);

public sealed record RosterScanResult(
    string Uid,
    string Region,
    bool FullSync,
    IReadOnlyList<AgentScanResult> Agents,
    string ModelVersion,
    IReadOnlyDictionary<string, double>? ConfidenceByField,
    string ScanMeta
);

public sealed record RosterImportResult(
    string Status,
    string Message
);
