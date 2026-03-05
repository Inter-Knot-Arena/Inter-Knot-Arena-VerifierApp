namespace VerifierApp.Core.Models;

public sealed record RosterScanCommand(
    string SessionId,
    string RegionHint,
    bool FullSync,
    string Locale = "EN",
    string Resolution = "1080p",
    bool InputLockActive = true
);

public sealed record AgentScanResult(
    string AgentId,
    double? Level,
    double? Mindscape,
    WeaponScanResult? Weapon,
    IReadOnlyList<DiscScanResult>? Discs,
    IReadOnlyDictionary<string, double>? ConfidenceByField
);

public sealed record WeaponScanResult(
    string? WeaponId,
    double? Level
);

public sealed record DiscScanResult(
    int? Slot,
    string? SetId,
    double? Level
);

public sealed record RosterScanResult(
    string Uid,
    string Region,
    bool FullSync,
    IReadOnlyList<AgentScanResult> Agents,
    string ModelVersion,
    string DataVersion,
    IReadOnlyDictionary<string, double>? ConfidenceByField,
    string ScanMeta,
    IReadOnlyList<string>? LowConfReasons,
    double? TimingMs,
    string? Resolution,
    string? Locale,
    string? ErrorCode,
    string? ErrorMessage
);

public sealed record RosterImportResult(
    string Status,
    string Message
);
