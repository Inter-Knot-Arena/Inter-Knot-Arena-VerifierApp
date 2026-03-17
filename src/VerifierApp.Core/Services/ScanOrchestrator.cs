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
        if (!_nativeBridge.TryFocusGameWindow())
        {
            throw new InvalidOperationException("Scan aborted: game window could not be focused.");
        }

        var focusDelayMs = ReadNonNegativeIntFromEnvironment("IKA_GAME_FOCUS_DELAY_MS", 250);
        if (focusDelayMs > 0)
        {
            await Task.Delay(focusDelayMs, ct);
        }

        var locked = _nativeBridge.TryLockInput();
        if (!locked)
        {
            throw new InvalidOperationException("Scan aborted: OS input lock failed.");
        }

        try
        {
            var sessionId = Guid.NewGuid().ToString("N");
            var scanScript = Environment.GetEnvironmentVariable("IKA_SCAN_SCRIPT");
            if (string.IsNullOrWhiteSpace(scanScript))
            {
                scanScript = "ESC,TAB,TAB,ENTER";
            }
            var scanStepDelayMs = ReadPositiveIntFromEnvironment("IKA_SCAN_SCRIPT_STEP_DELAY_MS", 120);

            if (!_nativeBridge.ExecuteScanScript(scanScript, scanStepDelayMs))
            {
                throw new InvalidOperationException("Scan aborted: native scan automation script failed.");
            }

            var scan = fullSync
                ? await ExecuteFullRosterScanAsync(sessionId, regionHint, locale, resolution, ct)
                : await ExecuteVisibleSliceScanAsync(sessionId, regionHint, locale, resolution, ct);

            if (!string.IsNullOrWhiteSpace(scan.ErrorCode))
            {
                throw new InvalidOperationException(
                    $"Scan aborted [{scan.ErrorCode}]: {scan.ErrorMessage ?? "worker scan failure"}."
                );
            }

            var importResult = await _apiClient.ImportRosterAsync(scan, ct);
            if (scan.LowConfReasons is { Count: > 0 })
            {
                var warning = $"Imported with low confidence ({string.Join(", ", scan.LowConfReasons)}).";
                var message = string.IsNullOrWhiteSpace(importResult.Message)
                    ? warning
                    : $"{importResult.Message} {warning}";
                var status = string.Equals(importResult.Status, "OK", StringComparison.OrdinalIgnoreCase)
                    ? "DEGRADED"
                    : importResult.Status;
                return importResult with
                {
                    Status = status,
                    Message = message
                };
            }

            return importResult;
        }
        finally
        {
            if (locked)
            {
                _nativeBridge.UnlockInput();
            }
        }
    }

    private async Task<RosterScanResult> ExecuteVisibleSliceScanAsync(
        string sessionId,
        string regionHint,
        string locale,
        string resolution,
        CancellationToken ct
    )
    {
        var runtimeCaptures = await CaptureExtraScreensAsync(sessionId, ct, pageIndex: 1);
        return await RunWorkerScanAsync(
            sessionId,
            regionHint,
            fullSync: false,
            locale,
            resolution,
            runtimeCaptures,
            ct
        );
    }

    private async Task<RosterScanResult> ExecuteFullRosterScanAsync(
        string sessionId,
        string regionHint,
        string locale,
        string resolution,
        CancellationToken ct
    )
    {
        var maxPages = ReadPositiveIntFromEnvironment("IKA_FULL_SYNC_MAX_PAGES", 64);
        var maxStalledPages = ReadPositiveIntFromEnvironment("IKA_FULL_SYNC_MAX_STALLED_PAGES", 3);
        var initialUpSteps = ReadNonNegativeIntFromEnvironment("IKA_FULL_SYNC_INITIAL_UP_STEPS", 24);
        var initialPostDelayMs = ReadNonNegativeIntFromEnvironment("IKA_FULL_SYNC_INITIAL_POST_DELAY_MS", 400);
        var pageAdvanceScript = ReadScriptFromEnvironment("IKA_FULL_SYNC_PAGE_ADVANCE_SCRIPT", "DOWN");
        var pageAdvanceStepDelayMs = ReadPositiveIntFromEnvironment("IKA_FULL_SYNC_PAGE_ADVANCE_STEP_DELAY_MS", 120);
        var pageAdvancePostDelayMs = ReadNonNegativeIntFromEnvironment("IKA_FULL_SYNC_PAGE_ADVANCE_POST_DELAY_MS", 320);
        var pageNormalizeScript = ReadScriptFromEnvironment("IKA_FULL_SYNC_PAGE_NORMALIZE_SCRIPT", "UP,UP");
        var pageNormalizeStepDelayMs = ReadPositiveIntFromEnvironment("IKA_FULL_SYNC_PAGE_NORMALIZE_STEP_DELAY_MS", 120);
        var pageNormalizePostDelayMs = ReadNonNegativeIntFromEnvironment("IKA_FULL_SYNC_PAGE_NORMALIZE_POST_DELAY_MS", 220);

        if (initialUpSteps > 0)
        {
            var resetScript = BuildRepeatedKeyScript("UP", initialUpSteps);
            if (!string.IsNullOrWhiteSpace(resetScript) &&
                !_nativeBridge.ExecuteScanScript(resetScript, pageNormalizeStepDelayMs))
            {
                throw new InvalidOperationException("Scan aborted: full sync could not reset roster cursor.");
            }
            if (initialPostDelayMs > 0)
            {
                await Task.Delay(initialPostDelayMs, ct);
            }
        }

        var mergedAgents = new Dictionary<string, AgentScanResult>(StringComparer.OrdinalIgnoreCase);
        var seenSliceSignatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lowConfReasons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var topConfidence = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var topFieldSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var capabilities = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        string uid = string.Empty;
        string region = string.Empty;
        string modelVersion = string.Empty;
        string dataVersion = string.Empty;
        string scanMeta = string.Empty;
        var totalTimingMs = 0.0;
        var stalledPages = 0;
        var reachedTerminalSlice = false;
        var scannedPages = 0;

        for (var pageIndex = 0; pageIndex < maxPages; pageIndex++)
        {
            ct.ThrowIfCancellationRequested();

            if (pageIndex > 0 && !string.IsNullOrWhiteSpace(pageNormalizeScript))
            {
                if (!_nativeBridge.ExecuteScanScript(pageNormalizeScript, pageNormalizeStepDelayMs))
                {
                    throw new InvalidOperationException(
                        $"Scan aborted: full sync could not normalize roster cursor for page {pageIndex + 1}."
                    );
                }
                if (pageNormalizePostDelayMs > 0)
                {
                    await Task.Delay(pageNormalizePostDelayMs, ct);
                }
            }

            var pageSessionId = $"{sessionId}-page-{pageIndex + 1:D2}";
            var runtimeCaptures = await CaptureExtraScreensAsync(pageSessionId, ct, pageIndex: pageIndex + 1);
            var pageScan = await RunWorkerScanAsync(
                pageSessionId,
                regionHint,
                fullSync: false,
                locale,
                resolution,
                runtimeCaptures,
                ct
            );
            if (!string.IsNullOrWhiteSpace(pageScan.ErrorCode))
            {
                throw new InvalidOperationException(
                    $"Scan aborted [{pageScan.ErrorCode}]: {pageScan.ErrorMessage ?? "worker scan failure"}."
                );
            }

            scannedPages += 1;
            uid = MergeUid(uid, pageScan.Uid, lowConfReasons);
            region = string.IsNullOrWhiteSpace(region) ? pageScan.Region : region;
            modelVersion = string.IsNullOrWhiteSpace(modelVersion) ? pageScan.ModelVersion : modelVersion;
            dataVersion = string.IsNullOrWhiteSpace(dataVersion) ? pageScan.DataVersion : dataVersion;
            scanMeta = MergeScanMeta(scanMeta, pageScan.ScanMeta);
            totalTimingMs += pageScan.TimingMs ?? 0.0;
            MergeTopConfidence(topConfidence, pageScan.ConfidenceByField);
            MergeStringMap((IDictionary<string, string>)topFieldSources, pageScan.FieldSources);
            MergeBoolMap((IDictionary<string, bool>)capabilities, pageScan.Capabilities);
            foreach (var reason in pageScan.LowConfReasons ?? Array.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(reason))
                {
                    lowConfReasons.Add(reason);
                }
            }

            var newAgentsOnPage = 0;
            foreach (var agent in pageScan.Agents ?? Array.Empty<AgentScanResult>())
            {
                if (string.IsNullOrWhiteSpace(agent.AgentId))
                {
                    continue;
                }

                if (mergedAgents.TryGetValue(agent.AgentId, out var existing))
                {
                    mergedAgents[agent.AgentId] = MergeAgent(existing, agent);
                }
                else
                {
                    mergedAgents[agent.AgentId] = agent;
                    newAgentsOnPage += 1;
                }
            }

            var signature = BuildSliceSignature(pageScan.Agents);
            var repeatedSlice = !string.IsNullOrWhiteSpace(signature) && !seenSliceSignatures.Add(signature);
            stalledPages = newAgentsOnPage == 0 ? stalledPages + 1 : 0;

            if (repeatedSlice)
            {
                reachedTerminalSlice = true;
                break;
            }
            if (stalledPages >= maxStalledPages)
            {
                lowConfReasons.Add("full_sync_stalled_before_terminal_slice");
                break;
            }
            if (pageIndex == maxPages - 1)
            {
                lowConfReasons.Add("full_sync_page_limit_reached");
                break;
            }
            if (string.IsNullOrWhiteSpace(pageAdvanceScript))
            {
                lowConfReasons.Add("full_sync_page_advance_script_missing");
                break;
            }
            if (!_nativeBridge.ExecuteScanScript(pageAdvanceScript, pageAdvanceStepDelayMs))
            {
                throw new InvalidOperationException(
                    $"Scan aborted: full sync could not advance after page {pageIndex + 1}."
                );
            }
            if (pageAdvancePostDelayMs > 0)
            {
                await Task.Delay(pageAdvancePostDelayMs, ct);
            }
        }

        if (mergedAgents.Count == 0)
        {
            throw new InvalidOperationException("Scan aborted: full sync produced no agents.");
        }

        if (string.IsNullOrWhiteSpace(uid))
        {
            lowConfReasons.Add("uid_missing_after_full_sync");
        }
        if (scannedPages <= 1)
        {
            lowConfReasons.Add("full_sync_single_page_only");
        }

        capabilities["fullRosterCoverage"] = reachedTerminalSlice;

        var mergedList = mergedAgents.Values
            .OrderBy(agent => agent.AgentId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (mergedList.Length > 0)
        {
            topConfidence["agents"] = Round4(
                mergedList.Average(agent => ReadConfidence(agent.ConfidenceByField, "agentId"))
            );
            topConfidence["equipment"] = Round4(
                mergedList.Average(agent =>
                    (ReadConfidence(agent.ConfidenceByField, "weapon") +
                     ReadConfidence(agent.ConfidenceByField, "discs")) / 2.0
                )
            );
        }

        return new RosterScanResult(
            Uid: uid,
            Region: string.IsNullOrWhiteSpace(region) ? regionHint : region,
            FullSync: true,
            Agents: mergedList,
            ModelVersion: string.IsNullOrWhiteSpace(modelVersion) ? "unknown" : modelVersion,
            DataVersion: string.IsNullOrWhiteSpace(dataVersion) ? "unknown" : dataVersion,
            ConfidenceByField: topConfidence,
            ScanMeta: string.IsNullOrWhiteSpace(scanMeta)
                ? "verifier_multipage_full_sync"
                : $"{scanMeta}+verifier_multipage_full_sync",
            FieldSources: topFieldSources,
            Capabilities: capabilities,
            LowConfReasons: lowConfReasons.Count > 0
                ? lowConfReasons.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray()
                : null,
            TimingMs: Math.Round(totalTimingMs, 2),
            Resolution: resolution,
            Locale: locale,
            ErrorCode: null,
            ErrorMessage: null
        );
    }

    private static int ReadPositiveIntFromEnvironment(string envVar, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(envVar);
        return !string.IsNullOrWhiteSpace(raw) &&
               int.TryParse(raw, out var parsed) &&
               parsed > 0
            ? parsed
            : fallback;
    }

    private static int ReadNonNegativeIntFromEnvironment(string envVar, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(envVar);
        return !string.IsNullOrWhiteSpace(raw) &&
               int.TryParse(raw, out var parsed) &&
               parsed >= 0
            ? parsed
            : fallback;
    }

    private static string ReadScriptFromEnvironment(string envVar, string fallback)
    {
        var raw = Environment.GetEnvironmentVariable(envVar);
        return string.IsNullOrWhiteSpace(raw) ? fallback : raw.Trim();
    }

    private async Task<RosterScanResult> RunWorkerScanAsync(
        string sessionId,
        string regionHint,
        bool fullSync,
        string locale,
        string resolution,
        IReadOnlyList<ScreenCaptureInput> runtimeCaptures,
        CancellationToken ct
    ) =>
        await _worker.RunRosterScanAsync(
            new RosterScanCommand(
                SessionId: sessionId,
                RegionHint: regionHint,
                FullSync: fullSync,
                Locale: locale,
                Resolution: resolution,
                InputLockActive: true,
                CaptureScreen: runtimeCaptures.Count == 0,
                ScreenCaptures: runtimeCaptures.Count > 0 ? runtimeCaptures : null
            ),
            ct
        );

    private async Task<IReadOnlyList<ScreenCaptureInput>> CaptureExtraScreensAsync(
        string sessionId,
        CancellationToken ct,
        int? pageIndex = null
    )
    {
        var plan = RuntimeScreenCapturePlan.LoadActivePlan();
        if (plan.Count == 0)
        {
            return [];
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "ika_verifier", "screen_captures", sessionId);
        Directory.CreateDirectory(tempRoot);

        var captures = new List<ScreenCaptureInput>();
        for (var index = 0; index < plan.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var step = plan[index];
            if (!_nativeBridge.ExecuteScanScript(step.Script ?? string.Empty, step.StepDelayMs))
            {
                throw new InvalidOperationException(
                    $"Scan aborted: extra capture automation failed for step {index + 1} ({step.Role})."
                );
            }

            if (step.PostDelayMs > 0)
            {
                await Task.Delay(step.PostDelayMs, ct);
            }

            if (!step.Capture)
            {
                continue;
            }

            var capturePageIndex = step.PageIndex ?? pageIndex;
            var fileName = BuildCaptureFileName(index + 1, step, capturePageIndex);
            var outputPath = Path.Combine(tempRoot, fileName);
            if (!_nativeBridge.CaptureDesktopPng(outputPath))
            {
                throw new InvalidOperationException(
                    $"Scan aborted: desktop capture failed for step {index + 1} ({step.Role})."
                );
            }

            captures.Add(
                new ScreenCaptureInput(
                    Role: step.Role,
                    Path: outputPath,
                    SlotIndex: step.SlotIndex,
                    AgentSlotIndex: step.AgentSlotIndex,
                    ScreenAlias: step.ScreenAlias,
                    PageIndex: capturePageIndex
                )
            );
        }

        return captures;
    }

    private static string BuildCaptureFileName(int ordinal, RuntimeScreenCaptureStep step, int? pageIndex)
    {
        var alias = string.IsNullOrWhiteSpace(step.ScreenAlias)
            ? step.Role
            : step.ScreenAlias;
        var safeAlias = string.Concat(alias.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')).Trim('_');
        if (string.IsNullOrWhiteSpace(safeAlias))
        {
            safeAlias = step.Role;
        }

        var parts = new List<string>
        {
            ordinal.ToString("D2"),
            step.Role,
            $"agent_slot_{step.AgentSlotIndex}"
        };
        if (pageIndex is int capturePageIndex && capturePageIndex > 0)
        {
            parts.Add($"page_{capturePageIndex:D2}");
        }
        if (step.SlotIndex is int slotIndex)
        {
            parts.Add($"slot_{slotIndex}");
        }
        parts.Add(safeAlias);
        return $"{string.Join("_", parts)}.png";
    }

    private static string BuildRepeatedKeyScript(string key, int count)
    {
        if (string.IsNullOrWhiteSpace(key) || count <= 0)
        {
            return string.Empty;
        }

        return string.Join(",", Enumerable.Repeat(key.Trim(), count));
    }

    private static string MergeUid(string existing, string incoming, ISet<string> lowConfReasons)
    {
        if (string.IsNullOrWhiteSpace(existing))
        {
            return incoming;
        }
        if (string.IsNullOrWhiteSpace(incoming))
        {
            return existing;
        }
        if (!string.Equals(existing, incoming, StringComparison.Ordinal))
        {
            lowConfReasons.Add($"uid_changed_between_pages:{existing}:{incoming}");
        }
        return existing;
    }

    private static string MergeScanMeta(string existing, string incoming)
    {
        if (string.IsNullOrWhiteSpace(existing))
        {
            return incoming;
        }
        if (string.IsNullOrWhiteSpace(incoming) ||
            string.Equals(existing, incoming, StringComparison.OrdinalIgnoreCase))
        {
            return existing;
        }
        return $"{existing}+{incoming}";
    }

    private static void MergeTopConfidence(
        IDictionary<string, double> target,
        IReadOnlyDictionary<string, double>? source
    )
    {
        if (source is null)
        {
            return;
        }

        foreach (var pair in source)
        {
            if (!target.TryGetValue(pair.Key, out var existing) || pair.Value > existing)
            {
                target[pair.Key] = Round4(pair.Value);
            }
        }
    }

    private static void MergeStringMap(
        IDictionary<string, string> target,
        IReadOnlyDictionary<string, string>? source
    )
    {
        if (source is null)
        {
            return;
        }

        foreach (var pair in source)
        {
            if (!string.IsNullOrWhiteSpace(pair.Value))
            {
                target[pair.Key] = pair.Value;
            }
        }
    }

    private static void MergeBoolMap(
        IDictionary<string, bool> target,
        IReadOnlyDictionary<string, bool>? source
    )
    {
        if (source is null)
        {
            return;
        }

        foreach (var pair in source)
        {
            if (!target.TryGetValue(pair.Key, out var existing))
            {
                target[pair.Key] = pair.Value;
                continue;
            }
            target[pair.Key] = existing || pair.Value;
        }
    }

    private static string BuildSliceSignature(IReadOnlyList<AgentScanResult>? agents)
    {
        if (agents is null || agents.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(
            "|",
            agents
                .Select(agent => agent.AgentId)
                .Where(agentId => !string.IsNullOrWhiteSpace(agentId))
                .Select(agentId => agentId.Trim())
        );
    }

    private static AgentScanResult MergeAgent(AgentScanResult existing, AgentScanResult incoming)
    {
        var mergedStats = MergeNumberMap(existing.Stats, incoming.Stats);
        var mergedWeapon = SelectWeapon(existing.Weapon, incoming.Weapon);
        var mergedDiscs = MergeDiscs(existing.Discs, incoming.Discs);
        var mergedConfidence = MergeNumberMap(existing.ConfidenceByField, incoming.ConfidenceByField);
        var mergedFieldSources = MergeStringMap(existing.FieldSources, incoming.FieldSources);
        var mergedOccupancy = MergeBoolMap(existing.DiscSlotOccupancy, incoming.DiscSlotOccupancy);

        return existing with
        {
            Level = MaxNumber(existing.Level, incoming.Level),
            LevelCap = MaxNumber(existing.LevelCap, incoming.LevelCap),
            Mindscape = MaxNumber(existing.Mindscape, incoming.Mindscape),
            MindscapeCap = MaxNumber(existing.MindscapeCap, incoming.MindscapeCap),
            Stats = mergedStats,
            Weapon = mergedWeapon,
            WeaponPresent = (existing.WeaponPresent ?? false) || (incoming.WeaponPresent ?? false),
            DiscSlotOccupancy = mergedOccupancy,
            Discs = mergedDiscs,
            ConfidenceByField = mergedConfidence,
            FieldSources = mergedFieldSources
        };
    }

    private static double? MaxNumber(double? left, double? right)
    {
        if (left is null)
        {
            return right;
        }
        if (right is null)
        {
            return left;
        }
        return Math.Max(left.Value, right.Value);
    }

    private static IReadOnlyDictionary<string, double>? MergeNumberMap(
        IReadOnlyDictionary<string, double>? left,
        IReadOnlyDictionary<string, double>? right
    )
    {
        if (left is null && right is null)
        {
            return null;
        }

        var output = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (left is not null)
        {
            foreach (var pair in left)
            {
                output[pair.Key] = pair.Value;
            }
        }
        if (right is not null)
        {
            foreach (var pair in right)
            {
                if (!output.TryGetValue(pair.Key, out var existing) || pair.Value > existing)
                {
                    output[pair.Key] = pair.Value;
                }
            }
        }
        return output;
    }

    private static IReadOnlyDictionary<string, string>? MergeStringMap(
        IReadOnlyDictionary<string, string>? left,
        IReadOnlyDictionary<string, string>? right
    )
    {
        if (left is null && right is null)
        {
            return null;
        }

        var output = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (left is not null)
        {
            foreach (var pair in left)
            {
                if (!string.IsNullOrWhiteSpace(pair.Value))
                {
                    output[pair.Key] = pair.Value;
                }
            }
        }
        if (right is not null)
        {
            foreach (var pair in right)
            {
                if (!string.IsNullOrWhiteSpace(pair.Value))
                {
                    output[pair.Key] = pair.Value;
                }
            }
        }
        return output;
    }

    private static IReadOnlyDictionary<string, bool>? MergeBoolMap(
        IReadOnlyDictionary<string, bool>? left,
        IReadOnlyDictionary<string, bool>? right
    )
    {
        if (left is null && right is null)
        {
            return null;
        }

        var output = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (left is not null)
        {
            foreach (var pair in left)
            {
                output[pair.Key] = pair.Value;
            }
        }
        if (right is not null)
        {
            foreach (var pair in right)
            {
                output[pair.Key] = output.TryGetValue(pair.Key, out var existing)
                    ? existing || pair.Value
                    : pair.Value;
            }
        }
        return output;
    }

    private static WeaponScanResult? SelectWeapon(WeaponScanResult? left, WeaponScanResult? right)
    {
        if (left is null)
        {
            return right;
        }
        if (right is null)
        {
            return left;
        }
        return WeaponScore(right) >= WeaponScore(left) ? right : left;
    }

    private static double WeaponScore(WeaponScanResult weapon)
    {
        var score = 0.0;
        if (!string.IsNullOrWhiteSpace(weapon.WeaponId))
        {
            score += 5.0;
        }
        if (!string.IsNullOrWhiteSpace(weapon.DisplayName))
        {
            score += 2.0;
        }
        if (weapon.Level is not null)
        {
            score += 1.0 + weapon.Level.Value / 100.0;
        }
        if (!string.IsNullOrWhiteSpace(weapon.BaseStatKey))
        {
            score += 0.5;
        }
        if (!string.IsNullOrWhiteSpace(weapon.AdvancedStatKey))
        {
            score += 0.5;
        }
        return score;
    }

    private static IReadOnlyList<DiscScanResult>? MergeDiscs(
        IReadOnlyList<DiscScanResult>? left,
        IReadOnlyList<DiscScanResult>? right
    )
    {
        if ((left is null || left.Count == 0) && (right is null || right.Count == 0))
        {
            return null;
        }

        var output = new Dictionary<int, DiscScanResult>();
        if (left is not null)
        {
            foreach (var disc in left)
            {
                var slot = disc.Slot ?? 0;
                if (slot <= 0)
                {
                    continue;
                }
                output[slot] = disc;
            }
        }
        if (right is not null)
        {
            foreach (var disc in right)
            {
                var slot = disc.Slot ?? 0;
                if (slot <= 0)
                {
                    continue;
                }
                if (!output.TryGetValue(slot, out var existing) || DiscScore(disc) >= DiscScore(existing))
                {
                    output[slot] = disc;
                }
            }
        }
        return output.Values.OrderBy(disc => disc.Slot ?? 0).ToArray();
    }

    private static double DiscScore(DiscScanResult disc)
    {
        var score = 0.0;
        if (disc.Slot is not null)
        {
            score += 1.0;
        }
        if (!string.IsNullOrWhiteSpace(disc.SetId))
        {
            score += 3.0;
        }
        if (!string.IsNullOrWhiteSpace(disc.DisplayName))
        {
            score += 1.0;
        }
        if (disc.Level is not null)
        {
            score += 1.0 + disc.Level.Value / 30.0;
        }
        if (!string.IsNullOrWhiteSpace(disc.MainStatKey))
        {
            score += 0.5;
        }
        if (disc.Substats is { Count: > 0 })
        {
            score += 0.5 + disc.Substats.Count / 10.0;
        }
        return score;
    }

    private static double ReadConfidence(
        IReadOnlyDictionary<string, double>? values,
        string key
    )
    {
        if (values is null)
        {
            return 0.0;
        }
        return values.TryGetValue(key, out var value) ? value : 0.0;
    }

    private static double Round4(double value) => Math.Round(value, 4);
}
