namespace VerifierApp.Core.Models;

public sealed record ScreenCaptureInput(
    string Role,
    string Path,
    string? AgentId = null,
    int? SlotIndex = null,
    string? ScreenAlias = null
);

public sealed record RosterScanCommand(
    string SessionId,
    string RegionHint,
    bool FullSync,
    string Locale = "EN",
    string Resolution = "1080p",
    bool InputLockActive = true,
    bool CaptureScreen = true,
    IReadOnlyList<ScreenCaptureInput>? ScreenCaptures = null
);

public sealed record AgentScanResult(
    string AgentId,
    double? Level,
    double? LevelCap,
    double? Mindscape,
    double? MindscapeCap,
    IReadOnlyDictionary<string, double>? Stats,
    WeaponScanResult? Weapon,
    bool? WeaponPresent,
    IReadOnlyDictionary<string, bool>? DiscSlotOccupancy,
    IReadOnlyList<DiscScanResult>? Discs,
    IReadOnlyDictionary<string, double>? ConfidenceByField,
    IReadOnlyDictionary<string, string>? FieldSources
);

public sealed record WeaponScanResult(
    string? WeaponId,
    string? DisplayName,
    double? Level,
    double? LevelCap,
    string? BaseStatKey,
    double? BaseStatValue,
    string? AdvancedStatKey,
    double? AdvancedStatValue
);

public sealed record DiscSubstatScanResult(
    string? Key,
    double? Value
);

public sealed record DiscScanResult(
    int? Slot,
    string? SetId,
    string? DisplayName,
    double? Level,
    double? LevelCap,
    string? MainStatKey,
    double? MainStatValue,
    IReadOnlyList<DiscSubstatScanResult>? Substats
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
    IReadOnlyDictionary<string, string>? FieldSources,
    IReadOnlyDictionary<string, bool>? Capabilities,
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
