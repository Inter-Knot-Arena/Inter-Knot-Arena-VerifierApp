using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace VerifierApp.Core.Services;

public enum ElevationRelaunchOutcome
{
    NotNeeded,
    Relaunched,
    Cancelled,
    Failed,
}

public static class ElevationRelaunchHelper
{
    public static ElevationRelaunchOutcome TryRelaunchIfGameRequiresElevation(
        INativeBridge nativeBridge,
        string executablePath,
        IEnumerable<string> arguments,
        out string? errorMessage,
        out int? relaunchedExitCode,
        bool waitForExit = false
    )
    {
        errorMessage = null;
        relaunchedExitCode = null;
        var status = nativeBridge.InspectGameWindowStatus();
        if (!string.Equals(status.BlockingIssue, "game_requires_elevated_verifier", StringComparison.OrdinalIgnoreCase) ||
            status.CurrentProcessElevated)
        {
            return ElevationRelaunchOutcome.NotNeeded;
        }

        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            errorMessage = "Could not resolve the current executable path for administrator relaunch.";
            return ElevationRelaunchOutcome.Failed;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = JoinArguments(arguments),
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            var process = Process.Start(startInfo);
            if (process is null)
            {
                errorMessage = "Administrator relaunch did not start.";
                return ElevationRelaunchOutcome.Failed;
            }

            if (waitForExit)
            {
                process.WaitForExit();
                relaunchedExitCode = process.ExitCode;
            }

            return ElevationRelaunchOutcome.Relaunched;
        }
        catch (Win32Exception ex) when ((uint)ex.NativeErrorCode == 1223)
        {
            errorMessage = "Administrator permission was cancelled.";
            return ElevationRelaunchOutcome.Cancelled;
        }
        catch (Exception ex)
        {
            errorMessage = $"Administrator relaunch failed: {ex.Message}";
            return ElevationRelaunchOutcome.Failed;
        }
    }

    private static string JoinArguments(IEnumerable<string> arguments) =>
        string.Join(" ", arguments.Select(QuoteArgument));

    private static string QuoteArgument(string argument)
    {
        if (string.IsNullOrEmpty(argument))
        {
            return "\"\"";
        }

        if (!argument.Any(ch => char.IsWhiteSpace(ch) || ch == '"'))
        {
            return argument;
        }

        var builder = new StringBuilder();
        builder.Append('"');
        var backslashCount = 0;
        foreach (var ch in argument)
        {
            if (ch == '\\')
            {
                backslashCount += 1;
                continue;
            }

            if (ch == '"')
            {
                builder.Append('\\', backslashCount * 2 + 1);
                builder.Append('"');
                backslashCount = 0;
                continue;
            }

            if (backslashCount > 0)
            {
                builder.Append('\\', backslashCount);
                backslashCount = 0;
            }
            builder.Append(ch);
        }

        if (backslashCount > 0)
        {
            builder.Append('\\', backslashCount * 2);
        }

        builder.Append('"');
        return builder.ToString();
    }
}
