using System.Diagnostics;
using System.Drawing;
using VerifierApp.Core.Models;

namespace VerifierApp.Core.Services;

public sealed class ScanOrchestrator
{
    private readonly IVerifierApiClient? _apiClient;
    private readonly IWorkerClient _worker;
    private readonly INativeBridge _nativeBridge;
    private const string DefaultLiveEntryScript = "ESC,ESC";
    private static readonly object TraceLock = new();
    private bool _gameFocusKnownGood;

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

    public ScanOrchestrator(
        IWorkerClient worker,
        INativeBridge nativeBridge
    )
    {
        _worker = worker;
        _nativeBridge = nativeBridge;
    }

    public async Task<RosterScanResult> CaptureRosterScanAsync(
        string regionHint,
        bool fullSync,
        string locale,
        string resolution,
        RosterScanProfile scanProfile,
        CancellationToken ct
    )
    {
        _gameFocusKnownGood = false;
        await EnsureGameFocusedAsync(ct);

        try
        {
            var sessionId = Guid.NewGuid().ToString("N");
            AppendTrace(
                $"scan-runtime orchestrator-mvid={typeof(ScanOrchestrator).Module.ModuleVersionId:N} rosterProbePolicy=selection_only focusCache=true"
            );
            var scanScript = Environment.GetEnvironmentVariable("IKA_SCAN_SCRIPT");
            if (string.IsNullOrWhiteSpace(scanScript))
            {
                scanScript = DefaultLiveEntryScript;
            }
            var scanScriptUsesPointerInput = ScriptUsesPointerInput(scanScript);
            var preferSoftInputLock = ScriptUsesPointerInput(scanScript) ||
                                      RuntimeScreenCapturePlan.LoadActivePlan(
                                              RuntimeScreenCaptureMode.VisibleSlice,
                                              scanProfile,
                                              LayoutProfileKind.Wide16x9
                                          )
                                          .Any(step => ScriptUsesPointerInput(step.Script));
            var scanStepDelayMs = ReadPositiveIntFromEnvironment(
                "IKA_SCAN_SCRIPT_STEP_DELAY_MS",
                string.Equals(scanScript, DefaultLiveEntryScript, StringComparison.OrdinalIgnoreCase) ? 500 : 120
            );
            var scanPostDelayMs = ReadNonNegativeIntFromEnvironment(
                "IKA_SCAN_SCRIPT_POST_DELAY_MS",
                string.Equals(scanScript, DefaultLiveEntryScript, StringComparison.OrdinalIgnoreCase) ? 1600 : 550
            );
            var locked = _nativeBridge.TryLockInput(preferSoftInputLock);
            if (!locked)
            {
                throw new InvalidOperationException("Scan aborted: OS input lock failed.");
            }

            var shouldRunScanScript = true;
            if (string.Equals(scanScript, DefaultLiveEntryScript, StringComparison.OrdinalIgnoreCase) ||
                scanScriptUsesPointerInput)
            {
                var entrySurface = await InspectVisibleSliceEntrySurfaceAsync(0, ct);
                AppendTrace(
                    $"scan-entry-surface surface={entrySurface.SurfaceKind.ToString().ToLowerInvariant()} danger={entrySurface.LooksDangerous.ToString().ToLowerInvariant()} danger_kind={entrySurface.DangerKind ?? "<none>"} home={entrySurface.LooksLikeHomeScreen.ToString().ToLowerInvariant()} roster={entrySurface.LooksLikeRosterScreen.ToString().ToLowerInvariant()} layout_supported={entrySurface.LooksLikeSupportedWideLayout.ToString().ToLowerInvariant()} aspect={entrySurface.AspectRatio:F4} size={entrySurface.Width}x{entrySurface.Height}"
                );
                shouldRunScanScript = !entrySurface.LooksLikeHomeScreen && !entrySurface.LooksLikeRosterScreen;
                if (scanScriptUsesPointerInput &&
                    entrySurface.SurfaceKind is not LiveSafeSurfaceKind.Home and not LiveSafeSurfaceKind.Roster)
                {
                    throw new InvalidOperationException(
                        $"Scan aborted: pointer-based scan entry script requires a safe home or roster surface, but the current surface is {entrySurface.SurfaceKind.ToString().ToLowerInvariant()}."
                    );
                }
            }

            if (shouldRunScanScript)
            {
                await ExecuteGameScriptAsync(
                    scanScript,
                    scanStepDelayMs,
                    scanPostDelayMs,
                    expectFrameChange: true,
                    failureContext: "native scan automation script",
                    ct
                );
            }
            else
            {
                AppendTrace("native scan automation script skipped because current live UI is already at a scan entry surface");
            }

            var scan = fullSync
                ? await ExecuteFullRosterScanAsync(sessionId, regionHint, locale, resolution, scanProfile, ct)
                : await ExecuteVisibleSliceScanAsync(sessionId, regionHint, locale, resolution, scanProfile, ct);

            scan = scan with
            {
                ScanMeta = AppendScanMeta(
                    scan.ScanMeta,
                    $"verifier_scan_profile_{scanProfile.ToString().ToLowerInvariant()}"
                )
            };

            if (!string.IsNullOrWhiteSpace(scan.ErrorCode))
            {
                throw new InvalidOperationException(
                    $"Scan aborted [{scan.ErrorCode}]: {scan.ErrorMessage ?? "worker scan failure"}."
                );
            }

            return AttachInputLockMetadata(scan);
        }
        finally
        {
            _nativeBridge.UnlockInput();
        }
    }

    public async Task<RosterImportResult> ExecuteRosterScanAsync(
        string regionHint,
        bool fullSync,
        string locale,
        string resolution,
        RosterScanProfile scanProfile,
        CancellationToken ct
    )
    {
        var scan = await CaptureRosterScanAsync(regionHint, fullSync, locale, resolution, scanProfile, ct);
        if (_apiClient is null)
        {
            throw new InvalidOperationException("Roster import client is not configured for this scan orchestrator.");
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

    private async Task<RosterScanResult> ExecuteVisibleSliceScanAsync(
        string sessionId,
        string regionHint,
        string locale,
        string resolution,
        RosterScanProfile scanProfile,
        CancellationToken ct
    )
    {
        await EnsureVisibleSliceRosterEntryAsync(ct);
        var rosterSurface = await InspectVisibleSliceEntrySurfaceAsync(900, ct);

        var normalizeScript = ReadScriptFromEnvironment("IKA_VISIBLE_SLICE_INITIAL_NORMALIZE_SCRIPT", string.Empty);
        var normalizeStepDelayMs = ReadPositiveIntFromEnvironment("IKA_VISIBLE_SLICE_INITIAL_NORMALIZE_STEP_DELAY_MS", 120);
        var normalizePostDelayMs = ReadNonNegativeIntFromEnvironment("IKA_VISIBLE_SLICE_INITIAL_NORMALIZE_POST_DELAY_MS", 260);
        if (!string.IsNullOrWhiteSpace(normalizeScript))
        {
            await ExecuteGameScriptAsync(
                normalizeScript,
                normalizeStepDelayMs,
                normalizePostDelayMs,
                expectFrameChange: true,
                failureContext: "visible slice normalize roster cursor",
                ct
            );
        }

        var runtimeCaptures = await CaptureExtraScreensAsync(
            sessionId,
            ct,
            pageIndex: 1,
            mode: RuntimeScreenCaptureMode.VisibleSlice,
            scanProfile: scanProfile,
            layoutProfile: rosterSurface.LayoutProfileKind
        );
        try
        {
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
        finally
        {
            CleanupRuntimeCaptureArtifacts(runtimeCaptures);
        }
    }

    private async Task<RosterScanResult> ExecuteFullRosterScanAsync(
        string sessionId,
        string regionHint,
        string locale,
        string resolution,
        RosterScanProfile scanProfile,
        CancellationToken ct
    )
    {
        var initialSurface = await InspectVisibleSliceEntrySurfaceAsync(0, ct);
        AppendTrace(
            $"full-sync initial-surface home={initialSurface.LooksLikeHomeScreen.ToString().ToLowerInvariant()} roster={initialSurface.LooksLikeRosterScreen.ToString().ToLowerInvariant()} layout_supported={initialSurface.LooksLikeSupportedWideLayout.ToString().ToLowerInvariant()} layout_profile={initialSurface.LayoutProfileKind.ToString().ToLowerInvariant()} aspect={initialSurface.AspectRatio:F4} size={initialSurface.Width}x{initialSurface.Height}"
        );
        await EnsureVisibleSliceRosterEntryAsync(ct);

        var maxPages = ReadPositiveIntFromEnvironment("IKA_FULL_SYNC_MAX_PAGES", 64);
        var maxStalledPages = ReadPositiveIntFromEnvironment("IKA_FULL_SYNC_MAX_STALLED_PAGES", 3);
        var expectedAgentCount = ReadNonNegativeIntFromEnvironment("IKA_EXPECTED_AGENT_COUNT", 20);
        var maxWallMs = ReadNonNegativeIntFromEnvironment("IKA_FULL_SYNC_MAX_WALL_MS", 300_000);
        var initialPageResetSteps = ReadNonNegativeIntFromEnvironment("IKA_FULL_SYNC_INITIAL_PAGE_RESET_STEPS", 16);
        var initialPageResetScript = ReadScriptFromEnvironment(
            "IKA_FULL_SYNC_INITIAL_PAGE_RESET_SCRIPT",
            BuildRepeatedScript("WHEEL:120", initialPageResetSteps)
        );
        var initialPageResetPostDelayMs = ReadNonNegativeIntFromEnvironment(
            "IKA_FULL_SYNC_INITIAL_PAGE_RESET_POST_DELAY_MS",
            750
        );
        var initialUpSteps = ReadNonNegativeIntFromEnvironment("IKA_FULL_SYNC_INITIAL_UP_STEPS", 24);
        var initialPostDelayMs = ReadNonNegativeIntFromEnvironment("IKA_FULL_SYNC_INITIAL_POST_DELAY_MS", 400);
        var initialColumnNormalizeScript = ReadScriptFromEnvironment(
            "IKA_FULL_SYNC_INITIAL_COLUMN_NORMALIZE_SCRIPT",
            "LEFT,LEFT"
        );
        var pageAdvanceScript = ReadScriptFromEnvironment("IKA_FULL_SYNC_PAGE_ADVANCE_SCRIPT", "WHEEL:-120");
        var pageAdvanceStepDelayMs = ReadPositiveIntFromEnvironment("IKA_FULL_SYNC_PAGE_ADVANCE_STEP_DELAY_MS", 120);
        var pageAdvancePostDelayMs = ReadNonNegativeIntFromEnvironment("IKA_FULL_SYNC_PAGE_ADVANCE_POST_DELAY_MS", 650);
        var pageNormalizeScript = ReadScriptFromEnvironment("IKA_FULL_SYNC_PAGE_NORMALIZE_SCRIPT", "LEFT,LEFT");
        var pageNormalizeStepDelayMs = ReadPositiveIntFromEnvironment("IKA_FULL_SYNC_PAGE_NORMALIZE_STEP_DELAY_MS", 120);
        var pageNormalizePostDelayMs = ReadNonNegativeIntFromEnvironment("IKA_FULL_SYNC_PAGE_NORMALIZE_POST_DELAY_MS", 220);

        if (!string.IsNullOrWhiteSpace(initialPageResetScript))
        {
            await ExecuteGameScriptAsync(
                initialPageResetScript,
                pageAdvanceStepDelayMs,
                initialPageResetPostDelayMs,
                expectFrameChange: false,
                failureContext: "full sync reset roster scroll before page 1",
                ct
            );
        }
        if (initialUpSteps > 0)
        {
            var resetScript = BuildRepeatedScript("UP", initialUpSteps);
            if (!string.IsNullOrWhiteSpace(resetScript))
            {
                await ExecuteGameScriptAsync(
                    resetScript,
                    pageNormalizeStepDelayMs,
                    initialPostDelayMs,
                    expectFrameChange: false,
                    failureContext: "full sync reset roster cursor",
                    ct
                );
            }
        }
        if (!string.IsNullOrWhiteSpace(initialColumnNormalizeScript))
        {
            await ExecuteGameScriptAsync(
                initialColumnNormalizeScript,
                pageNormalizeStepDelayMs,
                pageNormalizePostDelayMs,
                expectFrameChange: false,
                failureContext: "full sync normalize roster cursor column before page 1",
                ct
            );
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
        var fullSyncStopwatch = Stopwatch.StartNew();

        for (var pageIndex = 0; pageIndex < maxPages; pageIndex++)
        {
            ct.ThrowIfCancellationRequested();
            if (maxWallMs > 0 && fullSyncStopwatch.ElapsedMilliseconds > maxWallMs)
            {
                AppendTrace($"full-sync budget-exceeded before page {pageIndex + 1}: wallMs={fullSyncStopwatch.ElapsedMilliseconds}");
                throw new InvalidOperationException(
                    $"Scan aborted: full sync exceeded the time budget of {maxWallMs} ms."
                );
            }

            var pageSessionId = $"{sessionId}-page-{pageIndex + 1:D2}";
            AppendTrace(
                $"full-sync page {pageIndex + 1}: capture-start session={pageSessionId}"
            );
            var runtimeCaptures = await CaptureExtraScreensAsync(
                pageSessionId,
                ct,
                pageIndex: pageIndex + 1,
                mode: RuntimeScreenCaptureMode.FullRosterPage,
                scanProfile: scanProfile,
                layoutProfile: initialSurface.LayoutProfileKind
            );
            AppendTrace(
                $"full-sync page {pageIndex + 1}: capture-complete files={runtimeCaptures.Count}"
            );
            if (runtimeCaptures.Count == 0)
            {
                if (pageIndex == 0)
                {
                    throw new InvalidOperationException(
                        "Scan aborted: full sync found no scannable roster slots on page 1."
                    );
                }

                reachedTerminalSlice = true;
                break;
            }
            RosterScanResult pageScan;
            try
            {
                pageScan = await RunWorkerScanAsync(
                    pageSessionId,
                    regionHint,
                    fullSync: false,
                    locale,
                    resolution,
                    runtimeCaptures,
                    ct
                );
                AppendTrace(
                    $"full-sync page {pageIndex + 1}: worker-complete agents={pageScan.Agents?.Count ?? 0} error={pageScan.ErrorCode ?? "none"}"
                );
            }
            finally
            {
                CleanupRuntimeCaptureArtifacts(runtimeCaptures);
            }
            if (!string.IsNullOrWhiteSpace(pageScan.ErrorCode))
            {
                throw new InvalidOperationException(
                    $"Scan aborted [{pageScan.ErrorCode}]: {pageScan.ErrorMessage ?? "worker scan failure"}."
                );
            }
            if (maxWallMs > 0 && fullSyncStopwatch.ElapsedMilliseconds > maxWallMs)
            {
                AppendTrace($"full-sync budget-exceeded after page {pageIndex + 1}: wallMs={fullSyncStopwatch.ElapsedMilliseconds}");
                throw new InvalidOperationException(
                    $"Scan aborted: full sync exceeded the time budget of {maxWallMs} ms."
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
            if (expectedAgentCount > 0 && mergedAgents.Count >= expectedAgentCount)
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
            await EnsureVisibleSliceRosterEntryAsync(ct);
            if (!string.IsNullOrWhiteSpace(pageNormalizeScript))
            {
                await ExecuteGameScriptAsync(
                    pageNormalizeScript,
                    pageNormalizeStepDelayMs,
                    pageNormalizePostDelayMs,
                    expectFrameChange: true,
                    failureContext: $"full sync normalize roster cursor before advancing from page {pageIndex + 1}",
                    ct
                );
            }
            await ExecuteGameScriptAsync(
                pageAdvanceScript,
                pageAdvanceStepDelayMs,
                pageAdvancePostDelayMs,
                expectFrameChange: true,
                failureContext: $"full sync advance after page {pageIndex + 1}",
                ct
            );
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
        int? pageIndex = null,
        RuntimeScreenCaptureMode mode = RuntimeScreenCaptureMode.VisibleSlice,
        RosterScanProfile scanProfile = RosterScanProfile.Fast,
        LayoutProfileKind layoutProfile = LayoutProfileKind.Wide16x9
    )
    {
        var plan = RuntimeScreenCapturePlan.LoadActivePlan(mode, scanProfile, layoutProfile);
        if (plan.Count == 0)
        {
            return [];
        }

        var tempRoot = ResolveRuntimeTempRoot("screen_captures", sessionId);
        var unavailableAgentSlots = mode == RuntimeScreenCaptureMode.FullRosterPage
            ? await InspectUnavailableFullRosterSlotsAsync(sessionId, tempRoot, pageIndex, ct)
            : new HashSet<int>();
        var loggedUnavailableAgentSlots = new HashSet<int>();
        var rosterScreenKnownGood = mode == RuntimeScreenCaptureMode.FullRosterPage && pageIndex is > 0;
        var includesDiskDetails = plan.Any(step => string.Equals(step.Role, "disk_detail", StringComparison.OrdinalIgnoreCase));
        AppendTrace(
            $"capture {sessionId}: policy mode={mode} scanProfile={scanProfile.ToString().ToLowerInvariant()} steps={plan.Count} rosterProbePolicy=selection_only rosterFastPath={rosterScreenKnownGood.ToString().ToLowerInvariant()} focusCache=true diskDetails={includesDiskDetails.ToString().ToLowerInvariant()}"
        );

        var captures = new List<ScreenCaptureInput>();
        var currentSafeSurfaceKind = LiveSafeSurfaceKind.Roster;
        EquipmentOverviewInspectionResult? currentEquipmentOverview = null;
        var skipNextAmplifierExit = false;
        int? skipNextDiskExitSlot = null;
        for (var index = 0; index < plan.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var step = plan[index];
            if (mode == RuntimeScreenCaptureMode.FullRosterPage && RequiresRosterScreen(step))
            {
                if (rosterScreenKnownGood)
                {
                    AppendTrace(
                        $"capture {sessionId}: step {index + 1} roster-fast-path alias={step.ScreenAlias ?? string.Empty}"
                    );
                }
                else
                {
                    AppendTrace(
                        $"capture {sessionId}: step {index + 1} verify-roster-before-step alias={step.ScreenAlias ?? string.Empty}"
                    );
                    await EnsureVisibleSliceRosterEntryAsync(ct);
                    var selectionSurface = await InspectVisibleSliceEntrySurfaceAsync(index + 1000, ct);
                    AppendTrace(
                        $"capture {sessionId}: step {index + 1} roster-step-surface home={selectionSurface.LooksLikeHomeScreen.ToString().ToLowerInvariant()} roster={selectionSurface.LooksLikeRosterScreen.ToString().ToLowerInvariant()}"
                    );
                    if (!selectionSurface.LooksLikeRosterScreen)
                    {
                        throw new InvalidOperationException(
                            $"Scan aborted: roster step {index + 1} is not on the roster screen."
                        );
                    }

                    rosterScreenKnownGood = true;
                    currentSafeSurfaceKind = LiveSafeSurfaceKind.Roster;
                }
            }
            if (step.RequiresVisibleSliceEntry)
            {
                await EnsureVisibleSliceRosterEntryAsync(ct);
                rosterScreenKnownGood = true;
                currentSafeSurfaceKind = LiveSafeSurfaceKind.Roster;
            }
            if (ShouldSkipUnavailableAgentSlotStep(step, unavailableAgentSlots))
            {
                if (step.AgentSlotIndex is int agentSlotIndex && loggedUnavailableAgentSlots.Add(agentSlotIndex))
                {
                    AppendTrace(
                        $"capture {sessionId}: roster-preflight skip agent-slot {agentSlotIndex}"
                    );
                }
                currentEquipmentOverview = null;
                skipNextAmplifierExit = false;
                skipNextDiskExitSlot = null;
                continue;
            }
            if (ShouldSkipAmplifierDetailStep(step, currentEquipmentOverview))
            {
                AppendTrace(
                    $"capture {sessionId}: step {index + 1} skip amplifier-detail alias={step.ScreenAlias ?? string.Empty}"
                );
                skipNextAmplifierExit = true;
                continue;
            }
            if (ShouldSkipAmplifierExitStep(step, skipNextAmplifierExit))
            {
                AppendTrace(
                    $"capture {sessionId}: step {index + 1} skip amplifier-exit alias={step.ScreenAlias ?? string.Empty}"
                );
                skipNextAmplifierExit = false;
                continue;
            }
            if (ShouldSkipDiskDetailStep(step, currentEquipmentOverview))
            {
                AppendTrace(
                    $"capture {sessionId}: step {index + 1} skip disk-detail alias={step.ScreenAlias ?? string.Empty} slot={step.SlotIndex?.ToString() ?? "na"}"
                );
                skipNextDiskExitSlot = step.SlotIndex;
                continue;
            }
            if (ShouldSkipDiskExitStep(step, skipNextDiskExitSlot))
            {
                AppendTrace(
                    $"capture {sessionId}: step {index + 1} skip disk-exit alias={step.ScreenAlias ?? string.Empty} slot={step.SlotIndex?.ToString() ?? "na"}"
                );
                skipNextDiskExitSlot = null;
                continue;
            }
            AppendTrace(
                $"capture {sessionId}: step {index + 1} start role={step.Role} alias={step.ScreenAlias ?? string.Empty} agent={step.AgentSlotIndex?.ToString() ?? "na"} slot={step.SlotIndex?.ToString() ?? "na"} capture={step.Capture}"
            );
            currentSafeSurfaceKind = await ValidateSafeSurfaceBeforeStepAsync(
                sessionId,
                index + 1,
                step,
                currentSafeSurfaceKind,
                ct
            );
            await ExecuteGameScriptAsync(
                step.Script ?? string.Empty,
                step.StepDelayMs,
                step.PostDelayMs,
                step.ExpectFrameChange,
                $"extra capture step {index + 1} ({step.Role})",
                ct
            );
            rosterScreenKnownGood = UpdateRosterScreenStateKnownGood(step, rosterScreenKnownGood);
            currentSafeSurfaceKind = UpdateKnownSafeSurfaceKind(step, currentSafeSurfaceKind);
            AppendTrace(
                $"capture {sessionId}: step {index + 1} script-complete role={step.Role} alias={step.ScreenAlias ?? string.Empty}"
            );

            if (!step.Capture)
            {
                continue;
            }

            var capturePageIndex = step.PageIndex ?? pageIndex;
            var fileName = BuildCaptureFileName(index + 1, step, capturePageIndex);
            var outputPath = Path.Combine(tempRoot, fileName);
            var windowStatus = _nativeBridge.InspectGameWindowStatus();
            if (!windowStatus.CanInjectInput)
            {
                throw new InvalidOperationException(BuildGameWindowFailureMessage(windowStatus));
            }
            if (!await TryCaptureRuntimePngWithRetriesAsync(outputPath, ct))
            {
                throw new InvalidOperationException(
                    $"Scan aborted: game-window capture failed for step {index + 1} ({step.Role})."
                );
            }
            AppendTrace(
                $"capture {sessionId}: step {index + 1} png={Path.GetFileName(outputPath)}"
            );

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

            if (string.Equals(step.Role, "equipment", StringComparison.OrdinalIgnoreCase))
            {
                currentEquipmentOverview = await TryInspectEquipmentOverviewAsync(outputPath, ct);
                AppendTrace(
                    $"capture {sessionId}: equipment-inspection weaponPresent={(currentEquipmentOverview?.WeaponPresent?.ToString() ?? "null")} discs={(currentEquipmentOverview?.DiscSlotOccupancy?.Count ?? 0)}"
                );
            }
            if (RequiresPostCaptureSurfaceValidation(step))
            {
                currentSafeSurfaceKind = ValidateCapturedSafeSurface(
                    sessionId,
                    index + 1,
                    step,
                    outputPath,
                    currentEquipmentOverview
                );
            }
        }

        return captures;
    }

    private async Task<HashSet<int>> InspectUnavailableFullRosterSlotsAsync(
        string sessionId,
        string tempRoot,
        int? pageIndex,
        CancellationToken ct
    )
    {
        if (pageIndex is not int capturePageIndex || capturePageIndex <= 0)
        {
            return [];
        }

        var strictUiSafety = ReadBooleanFlagFromEnvironment("IKA_STRICT_UI_SAFETY_GUARDS", true);
        var outputPath = Path.Combine(tempRoot, $"00_roster_preflight_page_{capturePageIndex:D2}.png");
        try
        {
            var windowStatus = _nativeBridge.InspectGameWindowStatus();
            if (!windowStatus.CanInjectInput)
            {
                AppendTrace(
                    $"capture {sessionId}: roster-preflight skipped reason=window-not-ready"
                );
                return [];
            }
            if (!await TryCaptureRuntimePngWithRetriesAsync(outputPath, ct))
            {
                AppendTrace(
                    $"capture {sessionId}: roster-preflight skipped reason=capture-failed"
                );
                if (strictUiSafety)
                {
                    throw new InvalidOperationException("Scan aborted: roster preflight capture failed.");
                }
                return [];
            }

            var inspections = LiveUiDetector.InspectVisibleRosterSlots(outputPath);
            if (inspections.Count == 0)
            {
                AppendTrace(
                    $"capture {sessionId}: roster-preflight skipped reason=no-slot-inspections"
                );
                if (strictUiSafety)
                {
                    throw new InvalidOperationException("Scan aborted: roster preflight could not validate visible agent slots.");
                }
                return [];
            }
            if (!LiveUiDetector.LooksLikeAgentRosterScreen(outputPath))
            {
                AppendTrace(
                    $"capture {sessionId}: roster-preflight skipped reason=not-roster-screen"
                );
                if (strictUiSafety)
                {
                    throw new InvalidOperationException("Scan aborted: roster preflight detected a non-roster screen.");
                }
                return [];
            }

            AppendTrace(
                $"capture {sessionId}: roster-preflight {string.Join(", ", inspections.Select(inspection => $"slot={inspection.AgentSlotIndex}:owned={inspection.LooksOwned.ToString().ToLowerInvariant()}:lock={inspection.LockBadgeDetected.ToString().ToLowerInvariant()}:visual_lock={inspection.LooksUnavailableVisualStyle.ToString().ToLowerInvariant()}:badge_lock={inspection.LooksUnavailableByLockBadge.ToString().ToLowerInvariant()}:sat={inspection.MeanCenterSaturation:F1}:color={inspection.CenterColorPixelFraction:F3}:luma={inspection.MeanCenterLuma:F1}"))}"
            );
            return inspections
                .Where(inspection => !inspection.LooksOwned)
                .Select(inspection => inspection.AgentSlotIndex)
                .ToHashSet();
        }
        catch (Exception ex)
        {
            AppendTrace(
                $"capture {sessionId}: roster-preflight skipped reason={ex.GetType().Name}:{ex.Message}"
            );
            if (strictUiSafety)
            {
                throw;
            }
            return [];
        }
        finally
        {
            DeleteFileQuietly(outputPath);
        }
    }

    private async Task<EquipmentOverviewInspectionResult?> TryInspectEquipmentOverviewAsync(
        string imagePath,
        CancellationToken ct
    )
    {
        try
        {
            return await _worker.InspectEquipmentOverviewAsync(imagePath, ct);
        }
        catch
        {
            return null;
        }
    }

    private static bool RequiresPostCaptureSurfaceValidation(RuntimeScreenCaptureStep step) =>
        string.Equals(step.Role, "agent_detail", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(step.Role, "equipment", StringComparison.OrdinalIgnoreCase);

    private LiveSafeSurfaceKind ValidateCapturedSafeSurface(
        string sessionId,
        int stepNumber,
        RuntimeScreenCaptureStep step,
        string outputPath,
        EquipmentOverviewInspectionResult? equipmentOverview
    )
    {
        var inspection = LiveUiDetector.InspectAgentProfileSurface(outputPath);
        if (string.Equals(step.Role, "agent_detail", StringComparison.OrdinalIgnoreCase))
        {
            AppendTrace(
                $"capture {sessionId}: step {stepNumber} agent-detail-surface home={inspection.LooksLikeHomeScreen.ToString().ToLowerInvariant()} roster={inspection.LooksLikeRosterScreen.ToString().ToLowerInvariant()} detail={inspection.LooksLikeAgentDetailScreen.ToString().ToLowerInvariant()} equipment={inspection.LooksLikeEquipmentScreen.ToString().ToLowerInvariant()} baseTab={inspection.BaseStatsHighlightFraction:F4} equipTab={inspection.EquipmentHighlightFraction:F4}"
            );
            if (inspection.LooksLikeRosterScreen || !inspection.LooksLikeAgentDetailScreen)
            {
                throw new InvalidOperationException(
                    $"Scan aborted: agent detail capture for slot {step.AgentSlotIndex?.ToString() ?? "unknown"} did not land on the expected safe detail screen."
                );
            }

            return LiveSafeSurfaceKind.AgentDetail;
        }

        if (string.Equals(step.Role, "equipment", StringComparison.OrdinalIgnoreCase))
        {
            AppendTrace(
                $"capture {sessionId}: step {stepNumber} equipment-surface home={inspection.LooksLikeHomeScreen.ToString().ToLowerInvariant()} roster={inspection.LooksLikeRosterScreen.ToString().ToLowerInvariant()} detail={inspection.LooksLikeAgentDetailScreen.ToString().ToLowerInvariant()} equipment={inspection.LooksLikeEquipmentScreen.ToString().ToLowerInvariant()} baseTab={inspection.BaseStatsHighlightFraction:F4} equipTab={inspection.EquipmentHighlightFraction:F4} overview={(equipmentOverview is not null).ToString().ToLowerInvariant()}"
            );
            if (inspection.LooksLikeRosterScreen ||
                inspection.LooksLikeAgentDetailScreen ||
                !inspection.LooksLikeEquipmentScreen ||
                equipmentOverview is null)
            {
                throw new InvalidOperationException(
                    $"Scan aborted: equipment capture for slot {step.AgentSlotIndex?.ToString() ?? "unknown"} did not land on the expected safe equipment screen."
                );
            }

            return LiveSafeSurfaceKind.Equipment;
        }

        return LiveSafeSurfaceKind.Unknown;
    }

    private static bool ShouldSkipAmplifierDetailStep(
        RuntimeScreenCaptureStep step,
        EquipmentOverviewInspectionResult? equipmentOverview
    )
    {
        return string.Equals(step.Role, "amplifier_detail", StringComparison.OrdinalIgnoreCase) &&
               equipmentOverview is not null &&
               equipmentOverview.WeaponPresent is false;
    }

    private static bool ShouldSkipUnavailableAgentSlotStep(
        RuntimeScreenCaptureStep step,
        IReadOnlySet<int> unavailableAgentSlots
    ) =>
        step.AgentSlotIndex is int agentSlotIndex &&
        unavailableAgentSlots.Contains(agentSlotIndex);

    private static bool ShouldSkipAmplifierExitStep(RuntimeScreenCaptureStep step, bool skipNextAmplifierExit)
    {
        return skipNextAmplifierExit &&
               string.IsNullOrWhiteSpace(step.Role) &&
               (step.ScreenAlias?.Contains("_amplifier", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static bool ShouldSkipDiskDetailStep(
        RuntimeScreenCaptureStep step,
        EquipmentOverviewInspectionResult? equipmentOverview
    )
    {
        if (!string.Equals(step.Role, "disk_detail", StringComparison.OrdinalIgnoreCase) ||
            equipmentOverview?.DiscSlotOccupancy is null ||
            step.SlotIndex is not int slotIndex)
        {
            return false;
        }

        return equipmentOverview.DiscSlotOccupancy.TryGetValue(slotIndex.ToString(), out var present) && !present;
    }

    private static bool ShouldSkipDiskExitStep(RuntimeScreenCaptureStep step, int? skipNextDiskExitSlot)
    {
        return skipNextDiskExitSlot is int slotIndex &&
               string.IsNullOrWhiteSpace(step.Role) &&
               step.SlotIndex == slotIndex &&
               (step.ScreenAlias?.Contains("_disk_", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private async Task EnsureGameFocusedAsync(
        CancellationToken ct,
        bool force = false,
        string failureMessage = "Scan aborted: game window could not be focused."
    )
    {
        var focusAttempts = ReadPositiveIntFromEnvironment("IKA_GAME_FOCUS_MAX_ATTEMPTS", 4);
        var focusRetryDelayMs = ReadNonNegativeIntFromEnvironment("IKA_GAME_FOCUS_RETRY_DELAY_MS", 220);
        for (var attempt = 0; attempt < focusAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var status = _nativeBridge.InspectGameWindowStatus();
            if (!status.CanInjectInput)
            {
                _gameFocusKnownGood = false;
                throw new InvalidOperationException(BuildGameWindowFailureMessage(status));
            }

            if (!force && _gameFocusKnownGood)
            {
                return;
            }

            if (_nativeBridge.TryFocusGameWindow())
            {
                var refocusDelayMs = ReadNonNegativeIntFromEnvironment("IKA_GAME_CAPTURE_REFOCUS_DELAY_MS", 90);
                if (refocusDelayMs > 0)
                {
                    await Task.Delay(refocusDelayMs, ct);
                }
                _gameFocusKnownGood = true;
                return;
            }

            if (attempt < focusAttempts - 1 && focusRetryDelayMs > 0)
            {
                await Task.Delay(focusRetryDelayMs, ct);
            }
        }

        _gameFocusKnownGood = false;
        throw new InvalidOperationException(failureMessage);
    }

    private static string BuildGameWindowFailureMessage(GameWindowStatus status)
    {
        return status.BlockingIssue switch
        {
            "game_process_not_found" => "Scan aborted: ZenlessZoneZero process was not found.",
            "game_window_missing" => "Scan aborted: game window is not available.",
            "game_requires_elevated_verifier" =>
                "Scan aborted: ZenlessZoneZero is running as administrator. Start VerifierApp as administrator too.",
            _ => "Scan aborted: game window is not ready for live input."
        };
    }

    private async Task EnsureVisibleSliceRosterEntryAsync(CancellationToken ct)
    {
        var entryScript = ReadScriptFromEnvironment(
            "IKA_VISIBLE_SLICE_ENTRY_SCRIPT",
            string.Empty
        );
        if (string.IsNullOrWhiteSpace(entryScript))
        {
            entryScript = string.Empty;
        }

        var entryStepDelayMs = ReadPositiveIntFromEnvironment("IKA_VISIBLE_SLICE_ENTRY_STEP_DELAY_MS", 120);
        var entryPostDelayMs = ReadNonNegativeIntFromEnvironment("IKA_VISIBLE_SLICE_ENTRY_POST_DELAY_MS", 1100);
        var entryStabilizeDelayMs = ReadNonNegativeIntFromEnvironment("IKA_VISIBLE_SLICE_ENTRY_STABILIZE_DELAY_MS", 650);
        var recoveryScript = ReadScriptFromEnvironment("IKA_VISIBLE_SLICE_ENTRY_RECOVERY_SCRIPT", "ESC,ESC");
        var recoveryMaxAttempts = ReadPositiveIntFromEnvironment("IKA_VISIBLE_SLICE_ENTRY_RECOVERY_MAX_ATTEMPTS", 5);
        var recoveryStepDelayMs = ReadPositiveIntFromEnvironment("IKA_VISIBLE_SLICE_ENTRY_RECOVERY_STEP_DELAY_MS", 500);
        var recoveryPostDelayMs = ReadNonNegativeIntFromEnvironment("IKA_VISIBLE_SLICE_ENTRY_RECOVERY_POST_DELAY_MS", 1600);

        for (var attempt = 0; attempt < recoveryMaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            await EnsureGameFocusedAsync(ct);
            var entrySurface = await InspectVisibleSliceEntrySurfaceAsync(attempt + 1, ct);
            if (!entrySurface.LooksLikeSupportedWideLayout)
            {
                throw new InvalidOperationException(
                    $"Scan aborted: the current game window layout ({entrySurface.Width}x{entrySurface.Height}, aspect {entrySurface.AspectRatio:F4}) is outside the supported wide-layout family."
                );
            }
            if (entrySurface.LooksLikeRosterScreen)
            {
                AppendTrace($"visible-slice-entry attempt={attempt + 1}: already-at-roster");
                return;
            }
            var effectiveEntryScript = string.IsNullOrWhiteSpace(entryScript)
                ? RuntimeScreenCapturePlan.DefaultVisibleSliceEntryScript(entrySurface.LayoutProfileKind)
                : entryScript;
            if (await TryEnterVisibleSliceRosterFromHomeAsync(
                    entrySurface,
                    attempt + 1,
                    effectiveEntryScript,
                    entryStepDelayMs,
                    entryPostDelayMs,
                    entryStabilizeDelayMs,
                    ct))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(recoveryScript))
            {
                break;
            }

            await TryExecuteGameScriptAsync(
                recoveryScript,
                recoveryStepDelayMs,
                recoveryPostDelayMs,
                expectFrameChange: false,
                ct
            );
            var postRecoverySurface = await InspectVisibleSliceEntrySurfaceAsync(attempt + 201, ct);
            AppendTrace(
                $"visible-slice-entry post-recovery attempt={attempt + 1}: surface={postRecoverySurface.SurfaceKind.ToString().ToLowerInvariant()} danger={postRecoverySurface.LooksDangerous.ToString().ToLowerInvariant()} home={postRecoverySurface.LooksLikeHomeScreen.ToString().ToLowerInvariant()} roster={postRecoverySurface.LooksLikeRosterScreen.ToString().ToLowerInvariant()} layout_supported={postRecoverySurface.LooksLikeSupportedWideLayout.ToString().ToLowerInvariant()} aspect={postRecoverySurface.AspectRatio:F4}"
            );
            if (postRecoverySurface.LooksLikeRosterScreen)
            {
                return;
            }
            if (await TryEnterVisibleSliceRosterFromHomeAsync(
                    postRecoverySurface,
                    attempt + 1,
                    effectiveEntryScript,
                    entryStepDelayMs,
                    entryPostDelayMs,
                    entryStabilizeDelayMs,
                    ct))
            {
                return;
            }
        }

        throw new InvalidOperationException(
            "Scan aborted: could not reach the agent roster screen from the current live UI state."
        );
    }

    private async Task<EntrySurfaceProbe> InspectVisibleSliceEntrySurfaceAsync(int attempt, CancellationToken ct)
    {
        var stateProbePath = await CaptureVisibleSliceStateProbeAsync(attempt, ct);
        try
        {
            var inspection = LiveUiDetector.InspectLiveSafetySurface(stateProbePath);
            return new EntrySurfaceProbe(
                inspection.SurfaceKind == LiveSafeSurfaceKind.Home,
                inspection.SurfaceKind == LiveSafeSurfaceKind.Roster,
                inspection.SurfaceKind,
                inspection.LayoutProfileKind,
                inspection.Width,
                inspection.Height,
                inspection.AspectRatio,
                inspection.LooksLikeSupportedWideLayout,
                inspection.LooksDangerous,
                inspection.DangerKind
            );
        }
        finally
        {
            DeleteFileQuietly(stateProbePath);
        }
    }

    private async Task<string> CaptureVisibleSliceStateProbeAsync(int attempt, CancellationToken ct)
    {
        var tempRoot = ResolveRuntimeTempRoot("entry_state");
        var path = Path.Combine(tempRoot, $"visible_slice_entry_{attempt:D2}_{Guid.NewGuid():N}.png");
        if (await TryCaptureRuntimePngWithRetriesAsync(path, ct))
        {
            return path;
        }

        throw new InvalidOperationException(
            "Scan aborted: failed to capture the game window while normalizing live UI state."
        );
    }

    private async Task<bool> TryEnterVisibleSliceRosterFromHomeAsync(
        EntrySurfaceProbe surface,
        int attempt,
        string entryScript,
        int entryStepDelayMs,
        int entryPostDelayMs,
        int entryStabilizeDelayMs,
        CancellationToken ct
    )
    {
        if (surface.SurfaceKind != LiveSafeSurfaceKind.Home ||
            !await TryExecuteGameScriptAsync(entryScript, entryStepDelayMs, entryPostDelayMs, expectFrameChange: true, ct))
        {
            return false;
        }

        if (entryStabilizeDelayMs > 0)
        {
            await Task.Delay(entryStabilizeDelayMs, ct);
        }

        var postEntrySurface = await InspectVisibleSliceEntrySurfaceAsync(attempt + 101, ct);
        AppendTrace(
            $"visible-slice-entry post-entry attempt={attempt}: surface={postEntrySurface.SurfaceKind.ToString().ToLowerInvariant()} danger={postEntrySurface.LooksDangerous.ToString().ToLowerInvariant()} home={postEntrySurface.LooksLikeHomeScreen.ToString().ToLowerInvariant()} roster={postEntrySurface.LooksLikeRosterScreen.ToString().ToLowerInvariant()} layout_supported={postEntrySurface.LooksLikeSupportedWideLayout.ToString().ToLowerInvariant()} aspect={postEntrySurface.AspectRatio:F4}"
        );
        return postEntrySurface.LooksLikeRosterScreen;
    }

    private async Task<LiveSafeSurfaceKind> ValidateSafeSurfaceBeforeStepAsync(
        string sessionId,
        int stepNumber,
        RuntimeScreenCaptureStep step,
        LiveSafeSurfaceKind currentSafeSurfaceKind,
        CancellationToken ct
    )
    {
        if (!ScriptUsesPointerInput(step.Script) || step.ExpectedSurfaceKind == LiveSafeSurfaceKind.Unknown)
        {
            return currentSafeSurfaceKind;
        }

        if (currentSafeSurfaceKind == step.ExpectedSurfaceKind)
        {
            return currentSafeSurfaceKind;
        }

        var probe = await InspectVisibleSliceEntrySurfaceAsync(stepNumber + 3000, ct);
        AppendTrace(
            $"capture {sessionId}: step {stepNumber} pre-click-surface expected={step.ExpectedSurfaceKind.ToString().ToLowerInvariant()} current={currentSafeSurfaceKind.ToString().ToLowerInvariant()} observed={probe.SurfaceKind.ToString().ToLowerInvariant()} danger={probe.LooksDangerous.ToString().ToLowerInvariant()} danger_kind={probe.DangerKind ?? "<none>"}"
        );
        if (!probe.LooksLikeSupportedWideLayout)
        {
            throw new InvalidOperationException(
                $"Scan aborted: pre-click safety probe for step {stepNumber} is outside the supported wide-layout family ({probe.Width}x{probe.Height}, aspect {probe.AspectRatio:F4})."
            );
        }

        if (probe.LooksDangerous || probe.SurfaceKind != step.ExpectedSurfaceKind)
        {
            throw new InvalidOperationException(
                $"Scan aborted: pre-click safety guard expected {step.ExpectedSurfaceKind.ToString().ToLowerInvariant()} before step {stepNumber}, but observed {probe.SurfaceKind.ToString().ToLowerInvariant()}."
            );
        }

        return probe.SurfaceKind;
    }

    private async Task ExecuteGameScriptAsync(
        string script,
        int stepDelayMs,
        int postDelayMs,
        bool expectFrameChange,
        string failureContext,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            return;
        }

        var maxAttempts = ReadPositiveIntFromEnvironment("IKA_CAPTURE_STEP_MAX_ATTEMPTS", 2);
        var retryDelayMs = ReadNonNegativeIntFromEnvironment("IKA_CAPTURE_STEP_RETRY_DELAY_MS", 180);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            AppendTrace(
                $"script-start context={failureContext} attempt={attempt}/{maxAttempts} expectFrameChange={expectFrameChange} script={script}"
            );
            AppendTrace($"script-stage context={failureContext} attempt={attempt}/{maxAttempts} stage=ensure-focus-start");
            await EnsureGameFocusedAsync(ct, force: attempt > 1);
            AppendTrace($"script-stage context={failureContext} attempt={attempt}/{maxAttempts} stage=ensure-focus-complete");
            AppendTrace($"script-stage context={failureContext} attempt={attempt}/{maxAttempts} stage=before-hash-start");
            var beforeHash = expectFrameChange ? NormalizeFrameHash(_nativeBridge.CaptureFrameHash()) : string.Empty;
            AppendTrace(
                $"script-stage context={failureContext} attempt={attempt}/{maxAttempts} stage=before-hash-complete empty={string.IsNullOrWhiteSpace(beforeHash)}"
            );

            AppendTrace($"script-stage context={failureContext} attempt={attempt}/{maxAttempts} stage=execute-start");
            if (!_nativeBridge.ExecuteScanScript(script, stepDelayMs))
            {
                _gameFocusKnownGood = false;
                AppendTrace(
                    $"script-failed context={failureContext} attempt={attempt}/{maxAttempts} stage=execute"
                );
                if (attempt == maxAttempts)
                {
                    throw new InvalidOperationException($"Scan aborted: {failureContext} failed.");
                }

                if (retryDelayMs > 0)
                {
                    await Task.Delay(retryDelayMs, ct);
                }
                continue;
            }
            AppendTrace($"script-stage context={failureContext} attempt={attempt}/{maxAttempts} stage=execute-complete");

            if (postDelayMs > 0)
            {
                await Task.Delay(postDelayMs, ct);
            }

            if (!expectFrameChange)
            {
                return;
            }

            AppendTrace($"script-stage context={failureContext} attempt={attempt}/{maxAttempts} stage=after-hash-start");
            var afterHash = NormalizeFrameHash(_nativeBridge.CaptureFrameHash());
            AppendTrace(
                $"script-stage context={failureContext} attempt={attempt}/{maxAttempts} stage=after-hash-complete empty={string.IsNullOrWhiteSpace(afterHash)}"
            );
            if (string.IsNullOrWhiteSpace(beforeHash) ||
                string.IsNullOrWhiteSpace(afterHash) ||
                !string.Equals(beforeHash, afterHash, StringComparison.OrdinalIgnoreCase))
            {
                _gameFocusKnownGood = true;
                AppendTrace(
                    $"script-success context={failureContext} attempt={attempt}/{maxAttempts}"
                );
                return;
            }

            _gameFocusKnownGood = false;
            AppendTrace(
                $"script-failed context={failureContext} attempt={attempt}/{maxAttempts} stage=frame-unchanged"
            );
            if (attempt == maxAttempts)
            {
                throw new InvalidOperationException(
                    $"Scan aborted: {failureContext} did not change the game frame."
                );
            }

            if (retryDelayMs > 0)
            {
                await Task.Delay(retryDelayMs, ct);
            }
        }
    }

    private async Task<bool> TryExecuteGameScriptAsync(
        string script,
        int stepDelayMs,
        int postDelayMs,
        bool expectFrameChange,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            return true;
        }

        AppendTrace($"script-try-stage script={script} stage=ensure-focus-start");
        await EnsureGameFocusedAsync(ct);
        AppendTrace($"script-try-stage script={script} stage=ensure-focus-complete");
        AppendTrace($"script-try-stage script={script} stage=before-hash-start");
        var beforeHash = expectFrameChange ? NormalizeFrameHash(_nativeBridge.CaptureFrameHash()) : string.Empty;
        AppendTrace($"script-try-stage script={script} stage=before-hash-complete empty={string.IsNullOrWhiteSpace(beforeHash)}");
        AppendTrace($"script-try-stage script={script} stage=execute-start");
        if (!_nativeBridge.ExecuteScanScript(script, stepDelayMs))
        {
            _gameFocusKnownGood = false;
            AppendTrace($"script-try-failed script={script} stage=execute");
            return false;
        }
        AppendTrace($"script-try-stage script={script} stage=execute-complete");

        if (postDelayMs > 0)
        {
            await Task.Delay(postDelayMs, ct);
        }

        if (!expectFrameChange)
        {
            _gameFocusKnownGood = true;
            return true;
        }

        AppendTrace($"script-try-stage script={script} stage=after-hash-start");
        var afterHash = NormalizeFrameHash(_nativeBridge.CaptureFrameHash());
        AppendTrace($"script-try-stage script={script} stage=after-hash-complete empty={string.IsNullOrWhiteSpace(afterHash)}");
        if (string.IsNullOrWhiteSpace(beforeHash) || string.IsNullOrWhiteSpace(afterHash))
        {
            _gameFocusKnownGood = false;
            AppendTrace($"script-try-failed script={script} stage=hash-empty");
            return false;
        }

        var changed = !string.Equals(beforeHash, afterHash, StringComparison.OrdinalIgnoreCase);
        _gameFocusKnownGood = changed;
        AppendTrace($"script-try-complete script={script} changed={changed}");
        return changed;
    }

    private static string NormalizeFrameHash(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static bool ScriptUsesPointerInput(string? script)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            return false;
        }

        return script.Contains("CLICK:", StringComparison.OrdinalIgnoreCase) ||
               script.Contains("DOUBLECLICK:", StringComparison.OrdinalIgnoreCase) ||
               script.Contains("DBLCLICK:", StringComparison.OrdinalIgnoreCase) ||
               script.Contains("WHEEL:", StringComparison.OrdinalIgnoreCase) ||
               script.Contains("WHEELUP", StringComparison.OrdinalIgnoreCase) ||
               script.Contains("WHEELDOWN", StringComparison.OrdinalIgnoreCase);
    }

    private static LiveSafeSurfaceKind UpdateKnownSafeSurfaceKind(
        RuntimeScreenCaptureStep step,
        LiveSafeSurfaceKind currentSafeSurfaceKind
    )
    {
        if (RestoresRosterScreen(step))
        {
            return LiveSafeSurfaceKind.Roster;
        }

        if (RestoresEquipmentScreen(step))
        {
            return LiveSafeSurfaceKind.Equipment;
        }

        if (LeavesRosterScreen(step) ||
            string.Equals(step.Role, "equipment", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(step.Role, "agent_detail", StringComparison.OrdinalIgnoreCase))
        {
            return LiveSafeSurfaceKind.Unknown;
        }

        return currentSafeSurfaceKind;
    }

    private static bool RequiresRosterScreen(RuntimeScreenCaptureStep step)
    {
        return IsRosterSelectionStep(step);
    }

    private static bool UpdateRosterScreenStateKnownGood(RuntimeScreenCaptureStep step, bool rosterScreenKnownGood)
    {
        if (RestoresRosterScreen(step))
        {
            return true;
        }

        if (LeavesRosterScreen(step))
        {
            return false;
        }

        return rosterScreenKnownGood;
    }

    private static bool IsRosterSelectionStep(RuntimeScreenCaptureStep step)
    {
        return string.IsNullOrWhiteSpace(step.Role) &&
               !string.IsNullOrWhiteSpace(step.ScreenAlias) &&
               step.ScreenAlias.StartsWith("select_agent_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RestoresRosterScreen(RuntimeScreenCaptureStep step)
    {
        return string.IsNullOrWhiteSpace(step.Role) &&
               !string.IsNullOrWhiteSpace(step.ScreenAlias) &&
               step.ScreenAlias.StartsWith("return_to_agent_grid_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RestoresEquipmentScreen(RuntimeScreenCaptureStep step)
    {
        return string.IsNullOrWhiteSpace(step.Role) &&
               (
                   step.ScreenAlias?.Contains("_amplifier", StringComparison.OrdinalIgnoreCase) == true ||
                   step.ScreenAlias?.Contains("_disk_", StringComparison.OrdinalIgnoreCase) == true
               );
    }

    private static bool LeavesRosterScreen(RuntimeScreenCaptureStep step)
    {
        if (IsRosterSelectionStep(step))
        {
            return true;
        }

        return string.IsNullOrWhiteSpace(step.Role) &&
               string.Equals(step.ScreenAlias, "exit_agent_grid", StringComparison.OrdinalIgnoreCase);
    }

    private RosterScanResult AttachInputLockMetadata(RosterScanResult scan)
    {
        var capabilities = scan.Capabilities is { Count: > 0 }
            ? new Dictionary<string, bool>(scan.Capabilities, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var lowConfReasons = scan.LowConfReasons is { Count: > 0 }
            ? new List<string>(scan.LowConfReasons)
            : new List<string>();
        var scanMeta = scan.ScanMeta;
        var mode = _nativeBridge.CurrentInputLockMode;

        capabilities["hardInputLockActive"] = string.Equals(mode, "hard", StringComparison.OrdinalIgnoreCase);
        capabilities["softInputLockActive"] = string.Equals(mode, "soft", StringComparison.OrdinalIgnoreCase);

        if (string.Equals(mode, "soft", StringComparison.OrdinalIgnoreCase))
        {
            if (_nativeBridge.SoftInputLockWasFallback &&
                !lowConfReasons.Contains("soft_input_lock_fallback", StringComparer.OrdinalIgnoreCase))
            {
                lowConfReasons.Add("soft_input_lock_fallback");
            }
            scanMeta = AppendScanMeta(scanMeta, "verifier_soft_input_lock");
            if (_nativeBridge.SoftInputLockWasFallback)
            {
                scanMeta = AppendScanMeta(scanMeta, "verifier_soft_input_lock_fallback");
            }
        }
        else if (string.Equals(mode, "hard", StringComparison.OrdinalIgnoreCase))
        {
            scanMeta = AppendScanMeta(scanMeta, "verifier_hard_input_lock");
        }

        return scan with
        {
            Capabilities = capabilities,
            LowConfReasons = lowConfReasons.Count > 0 ? lowConfReasons : null,
            ScanMeta = scanMeta
        };
    }

    private static string AppendScanMeta(string existing, string suffix)
    {
        if (string.IsNullOrWhiteSpace(existing))
        {
            return suffix;
        }
        if (existing.Contains(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return existing;
        }
        return $"{existing}+{suffix}";
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

    private static string BuildRepeatedScript(string token, int count)
    {
        if (string.IsNullOrWhiteSpace(token) || count <= 0)
        {
            return string.Empty;
        }

        return string.Join(",", Enumerable.Repeat(token.Trim(), count));
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
        var existingKnownEmptyWeapon = IsKnownEmptyAmplifierDetailWeapon(existing);
        var incomingKnownEmptyWeapon = IsKnownEmptyAmplifierDetailWeapon(incoming);
        var mergedStats = MergeNumberMap(existing.Stats, incoming.Stats);
        var mergedWeapon = existingKnownEmptyWeapon || incomingKnownEmptyWeapon
            ? null
            : SelectWeapon(existing.Weapon, incoming.Weapon);
        var mergedDiscs = MergeDiscs(existing.Discs, incoming.Discs);
        var mergedConfidence = MergeNumberMap(existing.ConfidenceByField, incoming.ConfidenceByField);
        var mergedFieldSources = MergeStringMap(existing.FieldSources, incoming.FieldSources);
        var mergedOccupancy = MergeBoolMap(existing.DiscSlotOccupancy, incoming.DiscSlotOccupancy);
        if (existingKnownEmptyWeapon || incomingKnownEmptyWeapon)
        {
            var fieldSources = mergedFieldSources is not null
                ? new Dictionary<string, string>(mergedFieldSources, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            fieldSources["weapon"] = "known_empty_from_amplifier_detail";
            fieldSources["weaponPresent"] = "amplifier_detail_empty_state";
            mergedFieldSources = fieldSources;
        }

        return existing with
        {
            Level = MaxNumber(existing.Level, incoming.Level),
            LevelCap = MaxNumber(existing.LevelCap, incoming.LevelCap),
            Mindscape = MaxNumber(existing.Mindscape, incoming.Mindscape),
            MindscapeCap = MaxNumber(existing.MindscapeCap, incoming.MindscapeCap),
            Stats = mergedStats,
            Weapon = mergedWeapon,
            WeaponPresent = existingKnownEmptyWeapon || incomingKnownEmptyWeapon
                ? false
                : (existing.WeaponPresent ?? false) || (incoming.WeaponPresent ?? false),
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

    private static bool IsKnownEmptyAmplifierDetailWeapon(AgentScanResult agent)
    {
        var weaponSource = agent.FieldSources is not null &&
                           agent.FieldSources.TryGetValue("weapon", out var rawWeaponSource)
            ? rawWeaponSource
            : string.Empty;
        var weaponPresentSource = agent.FieldSources is not null &&
                                  agent.FieldSources.TryGetValue("weaponPresent", out var rawWeaponPresentSource)
            ? rawWeaponPresentSource
            : string.Empty;
        return !HasMeaningfulWeapon(agent.Weapon) &&
               agent.WeaponPresent is false &&
               string.Equals(weaponSource, "known_empty_from_amplifier_detail", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(weaponPresentSource, "amplifier_detail_empty_state", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasMeaningfulWeapon(WeaponScanResult? weapon)
    {
        return weapon is not null && (
            !string.IsNullOrWhiteSpace(weapon.WeaponId) ||
            !string.IsNullOrWhiteSpace(weapon.DisplayName) ||
            weapon.Level is not null ||
            weapon.LevelCap is not null ||
            !string.IsNullOrWhiteSpace(weapon.BaseStatKey) ||
            weapon.BaseStatValue is not null ||
            !string.IsNullOrWhiteSpace(weapon.AdvancedStatKey) ||
            weapon.AdvancedStatValue is not null
        );
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
        if (weapon.BaseStatValue is not null)
        {
            score += 0.5;
        }
        if (weapon.AdvancedStatValue is not null)
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

    private static string ResolveRuntimeTempRoot(params string[] segments)
    {
        var baseRoot = ResolveRuntimeTempBaseRoot();
        var path = segments.Aggregate(baseRoot, Path.Combine);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string ResolveRuntimeTempBaseRoot()
    {
        var overrideRoot = Environment.GetEnvironmentVariable("IKA_RUNTIME_TEMP_ROOT");
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            var expandedRoot = Environment.ExpandEnvironmentVariables(overrideRoot.Trim());
            Directory.CreateDirectory(expandedRoot);
            return expandedRoot;
        }

        foreach (var candidate in EnumerateFallbackRuntimeTempRoots())
        {
            try
            {
                Directory.CreateDirectory(candidate);
                return candidate;
            }
            catch
            {
                // try the next candidate
            }
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "ika_verifier");
        if (HasSufficientFreeSpace(tempRoot, minFreeBytes: 256L * 1024L * 1024L))
        {
            Directory.CreateDirectory(tempRoot);
            return tempRoot;
        }

        Directory.CreateDirectory(tempRoot);
        return tempRoot;
    }

    private static IEnumerable<string> EnumerateFallbackRuntimeTempRoots()
    {
        if (!string.IsNullOrWhiteSpace(Environment.CurrentDirectory))
        {
            yield return Path.Combine(Environment.CurrentDirectory, "artifacts", "runtime_temp", "ika_verifier");
        }

        if (!string.IsNullOrWhiteSpace(AppContext.BaseDirectory))
        {
            yield return Path.Combine(AppContext.BaseDirectory, "artifacts", "runtime_temp", "ika_verifier");
        }
    }

    private static bool HasSufficientFreeSpace(string path, long minFreeBytes)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrWhiteSpace(root))
            {
                return true;
            }

            return new DriveInfo(root).AvailableFreeSpace >= minFreeBytes;
        }
        catch
        {
            return true;
        }
    }

    private static bool PathsShareDrive(string left, string right)
    {
        try
        {
            var leftRoot = Path.GetPathRoot(Path.GetFullPath(left));
            var rightRoot = Path.GetPathRoot(Path.GetFullPath(right));
            return string.Equals(leftRoot, rightRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void CleanupRuntimeCaptureArtifacts(IReadOnlyList<ScreenCaptureInput> captures)
    {
        if (captures.Count == 0)
        {
            return;
        }

        if (ReadBooleanFlagFromEnvironment("IKA_KEEP_RUNTIME_CAPTURES", false))
        {
            AppendTrace("runtime capture cleanup skipped because IKA_KEEP_RUNTIME_CAPTURES=1");
            return;
        }

        var parentDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var capture in captures)
        {
            if (string.IsNullOrWhiteSpace(capture.Path))
            {
                continue;
            }

            DeleteFileQuietly(capture.Path);

            var parent = Path.GetDirectoryName(capture.Path);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                parentDirectories.Add(parent);
            }
        }

        foreach (var parent in parentDirectories.OrderByDescending(value => value.Length))
        {
            DeleteDirectoryQuietly(parent);
        }
    }

    private bool TryCaptureRuntimePng(string outputPath)
    {
        if (_nativeBridge.CaptureGameWindowPng(outputPath))
        {
            return true;
        }

        AppendTrace($"runtime-capture window-capture-failed file={Path.GetFileName(outputPath)}");
        var allowDesktopFallback = ReadBooleanFlagFromEnvironment("IKA_ALLOW_DESKTOP_CAPTURE_FALLBACK", false);
        if (!allowDesktopFallback)
        {
            return false;
        }

        var captured = _nativeBridge.CaptureDesktopPng(outputPath);
        if (captured)
        {
            AppendTrace($"runtime-capture desktop-fallback file={Path.GetFileName(outputPath)}");
        }
        return captured;
    }

    private async Task<bool> TryCaptureRuntimePngWithRetriesAsync(string outputPath, CancellationToken ct)
    {
        var captureAttempts = ReadPositiveIntFromEnvironment("IKA_RUNTIME_CAPTURE_MAX_ATTEMPTS", 3);
        var retryDelayMs = ReadNonNegativeIntFromEnvironment("IKA_RUNTIME_CAPTURE_RETRY_DELAY_MS", 120);
        var fileReadyAttempts = ReadPositiveIntFromEnvironment("IKA_GAME_CAPTURE_FILE_READY_MAX_ATTEMPTS", 8);
        var readyDelayMs = ReadNonNegativeIntFromEnvironment("IKA_GAME_CAPTURE_FILE_READY_DELAY_MS", 60);

        for (var captureAttempt = 1; captureAttempt <= captureAttempts; captureAttempt++)
        {
            ct.ThrowIfCancellationRequested();
            DeleteFileQuietly(outputPath);
            await EnsureGameFocusedAsync(ct, force: captureAttempt > 1);
            if (TryCaptureRuntimePng(outputPath))
            {
                for (var readyAttempt = 0; readyAttempt < fileReadyAttempts; readyAttempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (File.Exists(outputPath))
                    {
                        var length = new FileInfo(outputPath).Length;
                        if (length > 0 && !LooksLikeBlackRuntimeFrame(outputPath))
                        {
                            _gameFocusKnownGood = true;
                            return true;
                        }
                    }

                    if (readyDelayMs > 0)
                    {
                        await Task.Delay(readyDelayMs, ct);
                    }
                }
            }

            AppendTrace(
                $"runtime-capture retry attempt={captureAttempt}/{captureAttempts} file={Path.GetFileName(outputPath)}"
            );
            _gameFocusKnownGood = false;

            if (captureAttempt < captureAttempts && retryDelayMs > 0)
            {
                await Task.Delay(retryDelayMs, ct);
            }
        }

        DeleteFileQuietly(outputPath);
        _gameFocusKnownGood = false;
        return false;
    }

    private static bool LooksLikeBlackRuntimeFrame(string outputPath)
    {
        try
        {
            using var bitmap = new Bitmap(outputPath);
            if (bitmap.Width <= 0 || bitmap.Height <= 0)
            {
                return true;
            }

            var sampleStepX = Math.Max(1, bitmap.Width / 96);
            var sampleStepY = Math.Max(1, bitmap.Height / 54);
            var darkSamples = 0;
            var brightSamples = 0;
            var totalSamples = 0;

            for (var y = 0; y < bitmap.Height; y += sampleStepY)
            {
                for (var x = 0; x < bitmap.Width; x += sampleStepX)
                {
                    var pixel = bitmap.GetPixel(x, y);
                    var luma = (pixel.R + pixel.G + pixel.B) / 3.0;
                    totalSamples++;
                    if (luma < 10.0)
                    {
                        darkSamples++;
                    }

                    if (luma > 40.0)
                    {
                        brightSamples++;
                    }
                }
            }

            if (totalSamples == 0)
            {
                return true;
            }

            var darkFraction = darkSamples / (double)totalSamples;
            var brightFraction = brightSamples / (double)totalSamples;
            return darkFraction >= 0.995 && brightFraction <= 0.002;
        }
        catch
        {
            return true;
        }
    }

    private static bool ReadBooleanFlagFromEnvironment(string envVar, bool fallback)
    {
        var raw = Environment.GetEnvironmentVariable(envVar);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => fallback,
        };
    }

    private static void AppendTrace(string message)
    {
        var rawPath = Environment.GetEnvironmentVariable("IKA_LIVE_SCAN_TRACE_PATH");
        if (string.IsNullOrWhiteSpace(rawPath) || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        try
        {
            var fullPath = Path.GetFullPath(rawPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            lock (TraceLock)
            {
                File.AppendAllText(
                    fullPath,
                    $"[{DateTime.UtcNow:O}] {message}{Environment.NewLine}"
                );
            }
        }
        catch
        {
            // ignored
        }
    }

    private static void DeleteFileQuietly(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignored
        }
    }

    private static void DeleteDirectoryQuietly(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // ignored
        }
    }

    private static double Round4(double value) => Math.Round(value, 4);
}

internal sealed record EntrySurfaceProbe(
    bool LooksLikeHomeScreen,
    bool LooksLikeRosterScreen,
    LiveSafeSurfaceKind SurfaceKind,
    LayoutProfileKind LayoutProfileKind,
    int Width,
    int Height,
    double AspectRatio,
    bool LooksLikeSupportedWideLayout,
    bool LooksDangerous,
    string DangerKind
);
