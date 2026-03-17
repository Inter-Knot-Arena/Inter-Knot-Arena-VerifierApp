using System.Runtime.InteropServices;
using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Windows.Forms;
using VerifierApp.Core.Services;

namespace VerifierApp.WorkerHost;

public sealed class NativeBridge : INativeBridge
{
    private bool _hardInputLockActive;
    private bool _softInputLockActive;

    public string CurrentInputLockMode =>
        _hardInputLockActive
            ? "hard"
            : _softInputLockActive
                ? "soft"
                : "none";

    public bool TryFocusGameWindow()
    {
        try
        {
            var target = FindGameProcess();
            if (target is null || target.MainWindowHandle == IntPtr.Zero)
            {
                return false;
            }

            return FocusWindow(target.MainWindowHandle);
        }
        catch
        {
            return false;
        }
    }

    public bool TryLockInput(bool preferSoft = false)
    {
        _hardInputLockActive = false;
        _softInputLockActive = false;

        if (preferSoft && IsSoftInputLockAllowed() && TryFocusGameWindow())
        {
            _softInputLockActive = true;
            return true;
        }

        if (IkaNativeLockInput() == 1)
        {
            _hardInputLockActive = true;
            return true;
        }

        if (!IsSoftInputLockAllowed() || !TryFocusGameWindow())
        {
            return false;
        }

        _softInputLockActive = true;
        return true;
    }

    public void UnlockInput()
    {
        if (_hardInputLockActive)
        {
            _ = IkaNativeUnlockInput();
        }

        _hardInputLockActive = false;
        _softInputLockActive = false;
    }

    public bool ExecuteScanScript(string script, int stepDelayMs)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            return true;
        }

        var normalizedDelay = stepDelayMs <= 0 ? 120 : stepDelayMs;
        if (!TryFocusGameWindow())
        {
            return false;
        }

        if (ShouldUseManagedKeyInput(script))
        {
            return ExecuteManagedKeyScript(script, normalizedDelay);
        }

        return IkaNativeExecuteScanScript(script, normalizedDelay) == 1;
    }

    public string CaptureFrameHash()
    {
        try
        {
            var target = FindGameProcess();
            if (target is not null &&
                target.MainWindowHandle != IntPtr.Zero &&
                TryCaptureWindowHash(target.MainWindowHandle, out var gameWindowHash))
            {
                return gameWindowHash;
            }
        }
        catch
        {
            // Fall through to native desktop hash fallback.
        }

        var buffer = new byte[65];
        var result = IkaNativeCaptureFrameHash(buffer, buffer.Length);
        if (result <= 0)
        {
            return string.Empty;
        }
        return System.Text.Encoding.ASCII.GetString(buffer).TrimEnd('\0');
    }

    public bool CaptureGameWindowPng(string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return false;
        }

        try
        {
            var target = FindGameProcess();
            if (target is null || target.MainWindowHandle == IntPtr.Zero)
            {
                return false;
            }

            return CaptureWindowPng(target.MainWindowHandle, outputPath);
        }
        catch
        {
            return false;
        }
    }

    public bool CaptureDesktopPng(string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return false;
        }

        try
        {
            var targetPath = Path.GetFullPath(outputPath);
            var directory = Path.GetDirectoryName(targetPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return false;
            }
            Directory.CreateDirectory(directory);

            var width = GetSystemMetrics(SystemMetricCxScreen);
            var height = GetSystemMetrics(SystemMetricCyScreen);
            if (width <= 0 || height <= 0)
            {
                return false;
            }

            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(0, 0, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);
            bitmap.Save(targetPath, ImageFormat.Png);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private const int SystemMetricCxScreen = 0;
    private const int SystemMetricCyScreen = 1;
    private const int ShowWindowRestore = 9;

    private static Process? FindGameProcess()
    {
        var processName = Environment.GetEnvironmentVariable("IKA_GAME_PROCESS_NAME");
        if (string.IsNullOrWhiteSpace(processName))
        {
            processName = "ZenlessZoneZero";
        }

        var titleHint = Environment.GetEnvironmentVariable("IKA_GAME_WINDOW_TITLE");
        var normalizedProcessName = Path.GetFileNameWithoutExtension(processName.Trim());
        if (string.IsNullOrWhiteSpace(normalizedProcessName))
        {
            return null;
        }

        return Process
            .GetProcessesByName(normalizedProcessName)
            .Where(process => process.MainWindowHandle != IntPtr.Zero)
            .OrderByDescending(process => MatchesWindowTitle(process, titleHint))
            .ThenByDescending(process => process.StartTime)
            .FirstOrDefault();
    }

    private static bool MatchesWindowTitle(Process process, string? titleHint)
    {
        if (string.IsNullOrWhiteSpace(titleHint))
        {
            return true;
        }

        try
        {
            return process.MainWindowTitle.Contains(titleHint.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSoftInputLockAllowed()
    {
        var raw = Environment.GetEnvironmentVariable("IKA_ALLOW_SOFT_INPUT_LOCK");
        return !string.IsNullOrWhiteSpace(raw) &&
               (raw.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                raw.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                raw.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                raw.Equals("on", StringComparison.OrdinalIgnoreCase));
    }

    private bool ShouldUseManagedKeyInput(string script)
    {
        if (string.IsNullOrWhiteSpace(script) || !IsKeyOnlyScript(script))
        {
            return false;
        }

        if (_softInputLockActive)
        {
            return true;
        }

        var raw = Environment.GetEnvironmentVariable("IKA_KEY_SCRIPT_BACKEND");
        return !string.IsNullOrWhiteSpace(raw) &&
               (raw.Equals("managed", StringComparison.OrdinalIgnoreCase) ||
                raw.Equals("sendkeys", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsKeyOnlyScript(string script)
    {
        foreach (var token in script.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryParseWaitToken(token, out _))
            {
                continue;
            }

            if (!TryResolveManagedSendKey(token, out _))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ExecuteManagedKeyScript(string script, int stepDelayMs)
    {
        try
        {
            foreach (var token in script.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (TryParseWaitToken(token, out var waitMs))
                {
                    Thread.Sleep(waitMs);
                    continue;
                }

                if (!TryResolveManagedSendKey(token, out var sendKey))
                {
                    return false;
                }

                SendKeys.SendWait(sendKey);
                Thread.Sleep(stepDelayMs);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseWaitToken(string token, out int waitMs)
    {
        waitMs = 0;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        const string waitPrefix = "WAIT:";
        const string sleepPrefix = "SLEEP:";
        var normalized = token.Trim();
        if (normalized.StartsWith(waitPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(normalized[waitPrefix.Length..], out waitMs) && waitMs >= 0;
        }
        if (normalized.StartsWith(sleepPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(normalized[sleepPrefix.Length..], out waitMs) && waitMs >= 0;
        }

        return false;
    }

    private static bool TryResolveManagedSendKey(string token, out string sendKey)
    {
        sendKey = string.Empty;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        switch (token.Trim().ToUpperInvariant())
        {
            case "ESC":
            case "ESCAPE":
                sendKey = "{ESC}";
                return true;
            case "TAB":
                sendKey = "{TAB}";
                return true;
            case "ENTER":
            case "RETURN":
                sendKey = "{ENTER}";
                return true;
            case "SPACE":
                sendKey = " ";
                return true;
            case "LEFT":
                sendKey = "{LEFT}";
                return true;
            case "RIGHT":
                sendKey = "{RIGHT}";
                return true;
            case "UP":
                sendKey = "{UP}";
                return true;
            case "DOWN":
                sendKey = "{DOWN}";
                return true;
            case "F1":
            case "F2":
            case "F3":
            case "F4":
                sendKey = "{" + token.Trim().ToUpperInvariant() + "}";
                return true;
            case "I":
                sendKey = "i";
                return true;
            case "C":
                sendKey = "c";
                return true;
            default:
                return false;
        }
    }

    private static bool CaptureWindowPng(IntPtr windowHandle, string outputPath)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return false;
        }

        var targetPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(targetPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }
        Directory.CreateDirectory(directory);

        if (!GetWindowRect(windowHandle, out var rect))
        {
            return false;
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);
        bitmap.Save(targetPath, ImageFormat.Png);
        return true;
    }

    private static bool TryCaptureWindowHash(IntPtr windowHandle, out string hash)
    {
        hash = string.Empty;
        if (windowHandle == IntPtr.Zero)
        {
            return false;
        }

        if (!GetWindowRect(windowHandle, out var rect))
        {
            return false;
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);

        var data = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb
        );
        try
        {
            var byteCount = Math.Abs(data.Stride) * data.Height;
            var buffer = new byte[byteCount];
            Marshal.Copy(data.Scan0, buffer, 0, byteCount);
            hash = Convert.ToHexString(SHA256.HashData(buffer)).ToLowerInvariant();
            return true;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static bool FocusWindow(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            if (IsIconic(windowHandle))
            {
                _ = ShowWindowAsync(windowHandle, ShowWindowRestore);
            }

            var foregroundWindow = GetForegroundWindow();
            var targetThreadId = GetWindowThreadProcessId(windowHandle, out _);
            var foregroundThreadId = foregroundWindow != IntPtr.Zero
                ? GetWindowThreadProcessId(foregroundWindow, out _)
                : 0;
            var currentThreadId = GetCurrentThreadId();

            if (foregroundThreadId != 0 && foregroundThreadId != currentThreadId)
            {
                _ = AttachThreadInput(foregroundThreadId, currentThreadId, true);
            }
            if (targetThreadId != 0 && targetThreadId != currentThreadId)
            {
                _ = AttachThreadInput(targetThreadId, currentThreadId, true);
            }

            try
            {
                _ = BringWindowToTop(windowHandle);
                _ = SetForegroundWindow(windowHandle);
                _ = SetFocus(windowHandle);
            }
            finally
            {
                if (targetThreadId != 0 && targetThreadId != currentThreadId)
                {
                    _ = AttachThreadInput(targetThreadId, currentThreadId, false);
                }
                if (foregroundThreadId != 0 && foregroundThreadId != currentThreadId)
                {
                    _ = AttachThreadInput(foregroundThreadId, currentThreadId, false);
                }
            }

            return GetForegroundWindow() == windowHandle;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("ika_native.dll", EntryPoint = "ika_native_lock_input", CallingConvention = CallingConvention.Cdecl)]
    private static extern int IkaNativeLockInput();

    [DllImport("ika_native.dll", EntryPoint = "ika_native_unlock_input", CallingConvention = CallingConvention.Cdecl)]
    private static extern int IkaNativeUnlockInput();

    [DllImport("ika_native.dll", EntryPoint = "ika_native_capture_frame_hash", CallingConvention = CallingConvention.Cdecl)]
    private static extern int IkaNativeCaptureFrameHash(byte[] outputBuffer, int outputBufferLength);

    [DllImport("ika_native.dll", EntryPoint = "ika_native_execute_scan_script", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int IkaNativeExecuteScanScript(string script, int stepDelayMs);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
