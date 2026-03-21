using System.Globalization;
using System.Text.Json;

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
    bool RequiresVisibleSliceEntry = false
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
    private static readonly (int AgentSlotIndex, double X, double Y)[] VisibleAgentGridPoints =
    [
        (1, 0.597, 0.135),
        (2, 0.688, 0.197),
        (3, 0.814, 0.124),
    ];
    private static readonly (int SlotIndex, double X, double Y)[] DiskSlotPoints =
    [
        (1, 0.632, 0.284),
        (2, 0.571, 0.487),
        (3, 0.632, 0.691),
        (4, 0.825, 0.691),
        (5, 0.866, 0.487),
        (6, 0.825, 0.284),
    ];
    private const double HomeAgentsIconX = 0.660;
    private const double HomeAgentsIconY = 0.905;
    private const double BaseButtonX = 0.606;
    private const double BaseButtonY = 0.776;
    private const double EquipmentButtonX = 0.823;
    private const double EquipmentButtonY = 0.907;
    private const double AmplifierX = 0.694;
    private const double AmplifierY = 0.496;

    public static string DefaultVisibleSliceEntryScript => Click(HomeAgentsIconX, HomeAgentsIconY);

    public static IReadOnlyList<RuntimeScreenCaptureStep> LoadActivePlan(
        RuntimeScreenCaptureMode mode = RuntimeScreenCaptureMode.VisibleSlice
    )
    {
        var envPlan = LoadFromEnvironment(mode);
        if (envPlan.Count > 0)
        {
            return envPlan;
        }

        return LoadDefaultPreset(mode);
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

    private static IReadOnlyList<RuntimeScreenCaptureStep> LoadDefaultPreset(RuntimeScreenCaptureMode mode)
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
            return CreateVisibleSliceAgentDetailPlan(mode);
        }
        if (preset.Equals(RichEquipmentPreset, StringComparison.OrdinalIgnoreCase))
        {
            return CreateVisibleSliceRichEquipmentPlan(mode);
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

    private static IReadOnlyList<RuntimeScreenCaptureStep> CreateVisibleSliceAgentDetailPlan(RuntimeScreenCaptureMode mode) =>
        CreateGameplayAgentGridPlan(includeEquipment: false, mode);

    private static IReadOnlyList<RuntimeScreenCaptureStep> CreateVisibleSliceRichEquipmentPlan(RuntimeScreenCaptureMode mode) =>
        CreateGameplayAgentGridPlan(includeEquipment: true, mode);

    private static IReadOnlyList<RuntimeScreenCaptureStep> CreateGameplayAgentGridPlan(
        bool includeEquipment,
        RuntimeScreenCaptureMode mode
    )
    {
        var requiresVisibleSliceEntry = mode == RuntimeScreenCaptureMode.VisibleSlice;
        var exitAgentGridWhenDone = mode == RuntimeScreenCaptureMode.VisibleSlice;
        var steps = new List<RuntimeScreenCaptureStep>();
        for (var index = 0; index < VisibleAgentGridPoints.Length; index++)
        {
            var point = VisibleAgentGridPoints[index];
            var agentSlotIndex = point.AgentSlotIndex;
            steps.Add(CreateAgentSelectStep(agentSlotIndex, point.X, point.Y, requiresVisibleSliceEntry));
            steps.Add(CreateAgentDetailCapture(agentSlotIndex));
            if (includeEquipment)
            {
                AddEquipmentFlow(steps, agentSlotIndex);
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
                    AgentSlotIndex: VisibleAgentGridPoints.Length,
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
            PostDelayMs: 900,
            Capture: false,
            ExpectFrameChange: true,
            RequiresVisibleSliceEntry: requiresVisibleSliceEntry
        );

    private static RuntimeScreenCaptureStep CreateAgentDetailCapture(int agentSlotIndex, bool requiresVisibleSliceEntry = false) =>
        new(
            Role: "agent_detail",
            Script: Click(BaseButtonX, BaseButtonY),
            AgentSlotIndex: agentSlotIndex,
            ScreenAlias: $"agent_{agentSlotIndex}_detail",
            StepDelayMs: 120,
            PostDelayMs: 1150,
            Capture: true,
            ExpectFrameChange: true,
            RequiresVisibleSliceEntry: requiresVisibleSliceEntry
        );

    private static void AddEquipmentFlow(ICollection<RuntimeScreenCaptureStep> steps, int agentSlotIndex)
    {
        steps.Add(
            new RuntimeScreenCaptureStep(
                Role: "equipment",
                Script: Click(EquipmentButtonX, EquipmentButtonY),
                AgentSlotIndex: agentSlotIndex,
                ScreenAlias: $"agent_{agentSlotIndex}_equipment",
                StepDelayMs: 120,
                PostDelayMs: 900,
                Capture: true,
                ExpectFrameChange: true
            )
        );
        steps.Add(
            new RuntimeScreenCaptureStep(
                Role: "amplifier_detail",
                Script: Click(AmplifierX, AmplifierY),
                AgentSlotIndex: agentSlotIndex,
                ScreenAlias: $"agent_{agentSlotIndex}_amplifier",
                StepDelayMs: 120,
                PostDelayMs: 900,
                Capture: true,
                ExpectFrameChange: true
            )
        );
        steps.Add(
            new RuntimeScreenCaptureStep(
                Role: string.Empty,
                Script: "ESC",
                AgentSlotIndex: agentSlotIndex,
                ScreenAlias: $"exit_agent_{agentSlotIndex}_amplifier",
                StepDelayMs: 120,
                PostDelayMs: 280,
                Capture: false,
                ExpectFrameChange: true
            )
        );
        foreach (var (slotIndex, x, y) in DiskSlotPoints)
        {
            steps.Add(
                new RuntimeScreenCaptureStep(
                    Role: "disk_detail",
                    Script: Click(x, y),
                    AgentSlotIndex: agentSlotIndex,
                    SlotIndex: slotIndex,
                    ScreenAlias: $"agent_{agentSlotIndex}_disk_{slotIndex}",
                    StepDelayMs: 120,
                    PostDelayMs: 950,
                    Capture: true,
                    ExpectFrameChange: true
                )
            );
            steps.Add(
                new RuntimeScreenCaptureStep(
                    Role: string.Empty,
                    Script: "ESC",
                    AgentSlotIndex: agentSlotIndex,
                    SlotIndex: slotIndex,
                    ScreenAlias: $"exit_agent_{agentSlotIndex}_disk_{slotIndex}",
                    StepDelayMs: 120,
                    PostDelayMs: 700,
                    Capture: false,
                    ExpectFrameChange: true
                )
            );
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
                PostDelayMs: 380,
                Capture: false,
                ExpectFrameChange: true
            )
        );
    }

    private static string Click(double x, double y) =>
        $"CLICK:{x.ToString("0.000", CultureInfo.InvariantCulture)}:{y.ToString("0.000", CultureInfo.InvariantCulture)}";
}
