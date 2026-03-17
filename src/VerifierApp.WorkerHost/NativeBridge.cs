using System.Runtime.InteropServices;
using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;
using VerifierApp.Core.Services;

namespace VerifierApp.WorkerHost;

public sealed class NativeBridge : INativeBridge
{
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

    public bool TryLockInput() => IkaNativeLockInput() == 1;

    public void UnlockInput()
    {
        _ = IkaNativeUnlockInput();
    }

    public bool ExecuteScanScript(string script, int stepDelayMs)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            return true;
        }

        var normalizedDelay = stepDelayMs <= 0 ? 120 : stepDelayMs;
        return IkaNativeExecuteScanScript(script, normalizedDelay) == 1;
    }

    public string CaptureFrameHash()
    {
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
