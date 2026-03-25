using System.Globalization;
using System.Text.Json;
using VerifierApp.Core.Models;

namespace VerifierApp.Core.Services;

internal sealed record RuntimeScreenCaptureStep(
    string Role,
    string? Script = null,
    int? AgentSlotIndex = null,
    int? SlotIndex = null,
    string? ScreenAlias = null,
    int? PageIndex = null,
    int StepDelayMs = 120,
    int PostDelayMs = 350,
    bool Capture = true,
    bool ExpectFrameChange = true,
    bool RequiresVisibleSliceEntry = false,
    LiveSafeSurfaceKind ExpectedSurfaceKind = LiveSafeSurfaceKind.Unknown
);

internal enum RuntimeScreenCaptureMode
{
    VisibleSlice,
    FullRosterPage,
}

internal static class RuntimeScreenCapturePlan
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string DefaultPreset = "VISIBLE_SLICE_AGENT_DETAIL_V1";
    private const string RichEquipmentPreset = "VISIBLE_SLICE_AGENT_DETAIL_EQUIPMENT_AMP_BETA";

    public static string DefaultVisibleSliceEntryScript(
        LayoutProfileKind layoutProfile = LayoutProfileKind.Wide16x9
    )
    {
        var profile = LiveLayoutProfiles.Get(layoutProfile);
        return Click(profile.HomeAgentsClickPoint.X, profile.HomeAgentsClickPoint.Y);
    }

    public static IReadOnlyList<RuntimeScreenCaptureStep> LoadActivePlan(
        RuntimeScreenCaptureMode mode = RuntimeScreenCaptureMode.VisibleSlice,
        RosterScanProfile scanProfile = RosterScanProfile.Fast,
        LayoutProfileKind layoutProfile = LayoutProfileKind.Wide16x9
    )
    {
        var envPlan = LoadFromEnvironment(mode);
        if (envPlan.Count > 0)
        {
            return envPlan;
        }

        return LoadDefaultPreset(mode, scanProfile, layoutProfile);
    }

    private static IReadOnlyList<RuntimeScreenCaptureStep> LoadFromEnvironment(RuntimeScreenCaptureMode mode)
    {
        var inlineJson = Environment.GetEnvironmentVariable("IKA_EXTRA_SCREEN_CAPTURE_PLAN_JSON");
        var planPath = Environment.GetEnvironmentVariable("IKA_EXTRA_SCREEN_CAPTURE_PLAN_PATH");
        var raw = string.IsNullOrWhiteSpace(inlineJson)
            ? ReadFromFile(planPath)
            : inlineJson;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        try
        {
            var steps = JsonSerializer.Deserialize<List<RuntimeScreenCaptureStep>>(raw, JsonOptions);
            if (steps is null || steps.Count == 0)
            {
                return [];
            }

            return steps
                .Where(IsValid)
                .Select(step => Normalize(step, mode))
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static IReadOnlyList<RuntimeScreenCaptureStep> LoadDefaultPreset(
        RuntimeScreenCaptureMode mode,
        RosterScanProfile scanProfile,
        LayoutProfileKind layoutProfile
    )
    {
        var preset = Environment.GetEnvironmentVariable("IKA_DEFAULT_OCR_CAPTURE_PLAN");
        if (string.IsNullOrWhiteSpace(preset))
        {
            preset = DefaultPreset;
        }

        if (preset.Equals("OFF", StringComparison.OrdinalIgnoreCase) ||
            preset.Equals("NONE", StringComparison.OrdinalIgnoreCase) ||
            preset.Equals("DISABLED", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        if (preset.Equals(DefaultPreset, StringComparison.OrdinalIgnoreCase))
        {
            return CreateVisibleSliceAgentDetailPlan(mode, scanProfile, layoutProfile);
        }
        if (preset.Equals(RichEquipmentPreset, StringComparison.OrdinalIgnoreCase))
        {
            return CreateVisibleSliceRichEquipmentPlan(mode, scanProfile, layoutProfile);
        }

        return [];
    }

    private static string ReadFromFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return File.Exists(path)
                ? File.ReadAllText(path)
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsValid(RuntimeScreenCaptureStep step)
    {
        var hasScript = !string.IsNullOrWhiteSpace(step.Script);
        var hasRole = !string.IsNullOrWhiteSpace(step.Role);
        if (!hasScript)
        {
            return false;
        }
        if (step.Capture && !hasRole)
        {
            return false;
        }
        if (step.AgentSlotIndex is <= 0)
        {
            return false;
        }
        if (step.SlotIndex is not null && (step.SlotIndex < 1 || step.SlotIndex > 6))
        {
            return false;
        }
        return true;
    }

    private static RuntimeScreenCaptureStep Normalize(RuntimeScreenCaptureStep step, RuntimeScreenCaptureMode mode)
    {
        var role = step.Role.Trim();
        var alias = string.IsNullOrWhiteSpace(step.ScreenAlias)
            ? $"{(string.IsNullOrWhiteSpace(role) ? "step" : role)}_slot{step.AgentSlotIndex}"
            : step.ScreenAlias.Trim();
        var stepDelayMs = step.StepDelayMs > 0 ? step.StepDelayMs : 120;
        var postDelayMs = step.PostDelayMs >= 0 ? step.PostDelayMs : 350;
        var pageIndex = step.PageIndex is > 0 ? step.PageIndex : null;
        return step with
        {
            Role = role,
            ScreenAlias = alias,
            PageIndex = pageIndex,
            StepDelayMs = stepDelayMs,
            PostDelayMs = postDelayMs,
            RequiresVisibleSliceEntry = mode == RuntimeScreenCaptureMode.VisibleSlice && step.RequiresVisibleSliceEntry
        };
    }

    private static IReadOnlyList<RuntimeScreenCaptureStep> CreateVisibleSliceAgentDetailPlan(
        RuntimeScreenCaptureMode mode,
        RosterScanProfile scanProfile,
        LayoutProfileKind layoutProfile
    ) =>
        CreateGameplayAgentGridPlan(includeEquipment: false, mode, scanProfile, layoutProfile);

    private static IReadOnlyList<RuntimeScreenCaptureStep> CreateVisibleSliceRichEquipmentPlan(
        RuntimeScreenCaptureMode mode,
        RosterScanProfile scanProfile,
        LayoutProfileKind layoutProfile
    ) =>
        CreateGameplayAgentGridPlan(includeEquipment: true, mode, scanProfile, layoutProfile);

    private static IReadOnlyList<RuntimeScreenCaptureStep> CreateGameplayAgentGridPlan(
        bool includeEquipment,
        RuntimeScreenCaptureMode mode,
        RosterScanProfile scanProfile,
        LayoutProfileKind layoutProfile
    )
    {
        var profile = LiveLayoutProfiles.Get(layoutProfile);
        var requiresVisibleSliceEntry = mode == RuntimeScreenCaptureMode.VisibleSlice;
        var exitAgentGridWhenDone = mode == RuntimeScreenCaptureMode.VisibleSlice;
        var includeDiskDetails = ShouldIncludeDiskDetails(mode, scanProfile);
        var steps = new List<RuntimeScreenCaptureStep>();
        for (var index = 0; index < profile.VisibleAgentGridPoints.Count; index++)
        {
            var point = profile.VisibleAgentGridPoints[index];
            var agentSlotIndex = point.AgentSlotIndex;
            steps.Add(CreateAgentSelectStep(agentSlotIndex, point.X, point.Y, requiresVisibleSliceEntry));
            steps.Add(CreateAgentDetailCapture(agentSlotIndex, profile));
            if (includeEquipment)
            {
                AddEquipmentFlow(steps, agentSlotIndex, includeDiskDetails, profile);
            }
            else
            {
                AddReturnToAgentGridStep(steps, agentSlotIndex);
            }
        }

        if (exitAgentGridWhenDone)
        {
            steps.Add(
                new RuntimeScreenCaptureStep(
                    Role: string.Empty,
                    Script: "ESC",
                    AgentSlotIndex: profile.VisibleAgentGridPoints.Count,
                    ScreenAlias: "exit_agent_grid",
                    StepDelayMs: 120,
                    PostDelayMs: 240,
                    Capture: false,
                    ExpectFrameChange: true
                )
            );
        }

        return steps;
    }

    private static bool ShouldIncludeDiskDetails(RuntimeScreenCaptureMode mode, RosterScanProfile scanProfile)
    {
        if (scanProfile == RosterScanProfile.Deep)
        {
            return true;
        }

        var value = Environment.GetEnvironmentVariable("IKA_INCLUDE_DISK_DETAILS");
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("on", StringComparison.OrdinalIgnoreCase);
        }

        return mode == RuntimeScreenCaptureMode.VisibleSlice;
    }

    private static RuntimeScreenCaptureStep CreateAgentSelectStep(
        int agentSlotIndex,
        double x,
        double y,
        bool requiresVisibleSliceEntry = false
    ) =>
        new(
            Role: string.Empty,
            Script: Click(x, y),
            AgentSlotIndex: agentSlotIndex,
            ScreenAlias: $"select_agent_{agentSlotIndex}",
            StepDelayMs: 120,
            PostDelayMs: 650,
            Capture: false,
            ExpectFrameChange: true,
            RequiresVisibleSliceEntry: requiresVisibleSliceEntry,
            ExpectedSurfaceKind: LiveSafeSurfaceKind.Roster
        );

    private static RuntimeScreenCaptureStep CreateAgentDetailCapture(
        int agentSlotIndex,
        LayoutProfile profile,
        bool requiresVisibleSliceEntry = false
    ) =>
        new(
            Role: "agent_detail",
            Script: Click(profile.BaseButtonPoint.X, profile.BaseButtonPoint.Y),
            AgentSlotIndex: agentSlotIndex,
            ScreenAlias: $"agent_{agentSlotIndex}_detail",
            StepDelayMs: 120,
            PostDelayMs: 850,
            Capture: true,
            ExpectFrameChange: true,
            RequiresVisibleSliceEntry: requiresVisibleSliceEntry
        );

    private static void AddEquipmentFlow(
        ICollection<RuntimeScreenCaptureStep> steps,
        int agentSlotIndex,
        bool includeDiskDetails,
        LayoutProfile profile
    )
    {
        steps.Add(
            new RuntimeScreenCaptureStep(
                Role: "equipment",
                Script: Click(profile.EquipmentButtonPoint.X, profile.EquipmentButtonPoint.Y),
                AgentSlotIndex: agentSlotIndex,
                ScreenAlias: $"agent_{agentSlotIndex}_equipment",
                StepDelayMs: 120,
                PostDelayMs: 650,
                Capture: true,
                ExpectFrameChange: true,
                ExpectedSurfaceKind: LiveSafeSurfaceKind.AgentDetail
            )
        );
        steps.Add(
            new RuntimeScreenCaptureStep(
                Role: "amplifier_detail",
                Script: Click(profile.AmplifierClickPoint.X, profile.AmplifierClickPoint.Y),
                AgentSlotIndex: agentSlotIndex,
                ScreenAlias: $"agent_{agentSlotIndex}_amplifier",
                StepDelayMs: 120,
                PostDelayMs: 650,
                Capture: true,
                ExpectFrameChange: true,
                ExpectedSurfaceKind: LiveSafeSurfaceKind.Equipment
            )
        );
        steps.Add(
            new RuntimeScreenCaptureStep(
                Role: string.Empty,
                Script: "ESC",
                AgentSlotIndex: agentSlotIndex,
                ScreenAlias: $"exit_agent_{agentSlotIndex}_amplifier",
                StepDelayMs: 120,
                PostDelayMs: 220,
                Capture: false,
                ExpectFrameChange: true
            )
        );
        if (includeDiskDetails)
        {
            foreach (var point in profile.DiskSlotPoints)
            {
                steps.Add(
                    new RuntimeScreenCaptureStep(
                        Role: "disk_detail",
                        Script: Click(point.X, point.Y),
                        AgentSlotIndex: agentSlotIndex,
                        SlotIndex: point.SlotIndex,
                        ScreenAlias: $"agent_{agentSlotIndex}_disk_{point.SlotIndex}",
                        StepDelayMs: 120,
                        PostDelayMs: 700,
                        Capture: true,
                        ExpectFrameChange: true,
                        ExpectedSurfaceKind: LiveSafeSurfaceKind.Equipment
                    )
                );
                steps.Add(
                    new RuntimeScreenCaptureStep(
                        Role: string.Empty,
                        Script: "ESC",
                        AgentSlotIndex: agentSlotIndex,
                        SlotIndex: point.SlotIndex,
                        ScreenAlias: $"exit_agent_{agentSlotIndex}_disk_{point.SlotIndex}",
                        StepDelayMs: 120,
                        PostDelayMs: 450,
                        Capture: false,
                        ExpectFrameChange: true
                    )
                );
            }
        }
        AddReturnToAgentGridStep(steps, agentSlotIndex);
    }

    private static void AddReturnToAgentGridStep(ICollection<RuntimeScreenCaptureStep> steps, int agentSlotIndex)
    {
        steps.Add(
            new RuntimeScreenCaptureStep(
                Role: string.Empty,
                Script: "ESC",
                AgentSlotIndex: agentSlotIndex,
                ScreenAlias: $"return_to_agent_grid_{agentSlotIndex}",
                StepDelayMs: 120,
                PostDelayMs: 220,
                Capture: false,
                ExpectFrameChange: true
            )
        );
    }

    private static string Click(double x, double y) =>
        $"CLICK:{x.ToString("0.000", CultureInfo.InvariantCulture)}:{y.ToString("0.000", CultureInfo.InvariantCulture)}";
}
