using System.Text.Json;

namespace VerifierApp.Core.Services;

internal sealed record RuntimeScreenCaptureStep(
    string Role,
    string? Script = null,
    int? AgentSlotIndex = null,
    int? SlotIndex = null,
    string? ScreenAlias = null,
    int StepDelayMs = 120,
    int PostDelayMs = 350,
    bool Capture = true
);

internal static class RuntimeScreenCapturePlan
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string DefaultPreset = "VISIBLE_SLICE_AGENT_DETAIL_V1";

    public static IReadOnlyList<RuntimeScreenCaptureStep> LoadActivePlan()
    {
        var envPlan = LoadFromEnvironment();
        if (envPlan.Count > 0)
        {
            return envPlan;
        }

        return LoadDefaultPreset();
    }

    private static IReadOnlyList<RuntimeScreenCaptureStep> LoadFromEnvironment()
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
                .Select(Normalize)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static IReadOnlyList<RuntimeScreenCaptureStep> LoadDefaultPreset()
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

        if (!preset.Equals(DefaultPreset, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        return CreateVisibleSliceAgentDetailPlan();
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

    private static RuntimeScreenCaptureStep Normalize(RuntimeScreenCaptureStep step)
    {
        var role = step.Role.Trim();
        var alias = string.IsNullOrWhiteSpace(step.ScreenAlias)
            ? $"{(string.IsNullOrWhiteSpace(role) ? "step" : role)}_slot{step.AgentSlotIndex}"
            : step.ScreenAlias.Trim();
        var stepDelayMs = step.StepDelayMs > 0 ? step.StepDelayMs : 120;
        var postDelayMs = step.PostDelayMs >= 0 ? step.PostDelayMs : 350;
        return step with
        {
            Role = role,
            ScreenAlias = alias,
            StepDelayMs = stepDelayMs,
            PostDelayMs = postDelayMs
        };
    }

    private static IReadOnlyList<RuntimeScreenCaptureStep> CreateVisibleSliceAgentDetailPlan() =>
    [
        new RuntimeScreenCaptureStep(
            Role: "agent_detail",
            Script: "ENTER",
            AgentSlotIndex: 1,
            ScreenAlias: "agent_1_detail",
            StepDelayMs: 120,
            PostDelayMs: 450,
            Capture: true
        ),
        new RuntimeScreenCaptureStep(
            Role: string.Empty,
            Script: "ESC,DOWN",
            AgentSlotIndex: 1,
            ScreenAlias: "move_to_agent_2",
            StepDelayMs: 120,
            PostDelayMs: 260,
            Capture: false
        ),
        new RuntimeScreenCaptureStep(
            Role: "agent_detail",
            Script: "ENTER",
            AgentSlotIndex: 2,
            ScreenAlias: "agent_2_detail",
            StepDelayMs: 120,
            PostDelayMs: 450,
            Capture: true
        ),
        new RuntimeScreenCaptureStep(
            Role: string.Empty,
            Script: "ESC,DOWN",
            AgentSlotIndex: 2,
            ScreenAlias: "move_to_agent_3",
            StepDelayMs: 120,
            PostDelayMs: 260,
            Capture: false
        ),
        new RuntimeScreenCaptureStep(
            Role: "agent_detail",
            Script: "ENTER",
            AgentSlotIndex: 3,
            ScreenAlias: "agent_3_detail",
            StepDelayMs: 120,
            PostDelayMs: 450,
            Capture: true
        ),
        new RuntimeScreenCaptureStep(
            Role: string.Empty,
            Script: "ESC",
            AgentSlotIndex: 3,
            ScreenAlias: "exit_agent_3_detail",
            StepDelayMs: 120,
            PostDelayMs: 200,
            Capture: false
        ),
    ];
}
