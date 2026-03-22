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
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenElevationClass = 20;
    private bool _hardInputLockActive;
    private bool _softInputLockActive;
    private bool _softInputLockWasFallback;

    public string CurrentInputLockMode =>
        _hardInputLockActive
            ? "hard"
            : _softInputLockActive
                ? "soft"
                : "none";

    public bool SoftInputLockWasFallback => _softInputLockWasFallback;

    public GameWindowStatus InspectGameWindowStatus()
    {
        var currentProcessElevated = TryGetProcessElevation(Process.GetCurrentProcess().Id, out var currentElevated) &&
                                     currentElevated;
        var target = FindGameProcess();
        if (target is null)
        {
            return new GameWindowStatus(
                GameProcessFound: false,
                GameWindowFound: false,
                CurrentProcessElevated: currentProcessElevated,
                GameProcessElevated: false,
                GameProcessId: null,
                MainWindowTitle: null,
                BlockingIssue: "game_process_not_found"
            );
        }

        var hasWindow = target.MainWindowHandle != IntPtr.Zero;
        var gameProcessElevated = TryGetProcessElevation(target.Id, out var targetElevated) &&
                                  targetElevated;
        string? blockingIssue = null;
        if (!hasWindow)
        {
            blockingIssue = "game_window_missing";
        }
        else if (gameProcessElevated && !currentProcessElevated)
        {
            blockingIssue = "game_requires_elevated_verifier";
        }

        return new GameWindowStatus(
            GameProcessFound: true,
            GameWindowFound: hasWindow,
            CurrentProcessElevated: currentProcessElevated,
            GameProcessElevated: gameProcessElevated,
            GameProcessId: target.Id,
            MainWindowTitle: SafeReadWindowTitle(target),
            BlockingIssue: blockingIssue
        );
    }

    public bool TryFocusGameWindow()
    {
        var timeoutMs = ReadPositiveIntFromEnvironment("IKA_GAME_FOCUS_CALL_TIMEOUT_MS", 1_500);
        return RunBounded(TryFocusGameWindowCore, timeoutMs, false);
    }

    private bool TryFocusGameWindowCore()
    {
        try
        {
            var status = InspectGameWindowStatus();
            if (!status.CanInjectInput)
            {
                return false;
            }

            var target = FindGameProcess();
            if (target is null || target.MainWindowHandle == IntPtr.Zero)
            {
                return false;
            }

            return FocusWindow(target.MainWindowHandle) || TryAppActivate(target);
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
        _softInputLockWasFallback = false;

        if (!InspectGameWindowStatus().CanInjectInput)
        {
            return false;
        }

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
        _softInputLockWasFallback = true;
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
        _softInputLockWasFallback = false;
    }

    public bool ExecuteScanScript(string script, int stepDelayMs)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            return true;
        }

        var normalizedDelay = stepDelayMs <= 0 ? 120 : stepDelayMs;
        var timeoutMs = ComputeNativeScriptTimeoutMs(script, normalizedDelay);
        return RunBounded(() => ExecuteScanScriptCore(script, normalizedDelay), timeoutMs, false);
    }

    private bool ExecuteScanScriptCore(string script, int normalizedDelay)
    {
        if (!InspectGameWindowStatus().CanInjectInput)
        {
            return false;
        }
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
                TryCaptureWindowHashWithTimeout(target.MainWindowHandle, out var gameWindowHash))
            {
                return gameWindowHash;
            }
        }
        catch
        {
            // Fall through to native desktop hash fallback.
        }

        var timeoutMs = ReadPositiveIntFromEnvironment("IKA_GAME_FRAME_HASH_TIMEOUT_MS", 900);
        return RunBounded(
            () =>
            {
                var buffer = new byte[65];
                var result = IkaNativeCaptureFrameHash(buffer, buffer.Length);
                return result <= 0
                    ? string.Empty
                    : System.Text.Encoding.ASCII.GetString(buffer).TrimEnd('\0');
            },
            timeoutMs,
            string.Empty
        );
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

            return TryCaptureWindowPngWithTimeout(target.MainWindowHandle, outputPath);
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
    private const byte VirtualKeyMenu = 0x12;
    private const uint KeyeventfKeyUp = 0x0002;

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

    private static string SafeReadWindowTitle(Process process)
    {
        try
        {
            return process.MainWindowTitle;
        }
        catch
        {
            return string.Empty;
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

        var raw = Environment.GetEnvironmentVariable("IKA_KEY_SCRIPT_BACKEND");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (raw.Equals("native", StringComparison.OrdinalIgnoreCase) ||
            raw.Equals("raw", StringComparison.OrdinalIgnoreCase) ||
            raw.Equals("scan", StringComparison.OrdinalIgnoreCase) ||
            raw.Equals("scancode", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return raw.Equals("managed", StringComparison.OrdinalIgnoreCase) ||
               raw.Equals("sendkeys", StringComparison.OrdinalIgnoreCase);
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

        if (!TryGetClientScreenRect(windowHandle, out var rect))
        {
            return false;
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        var tempPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);
            bitmap.Save(tempPath, ImageFormat.Png);

            if (File.Exists(targetPath))
            {
                File.Delete(targetPath);
            }

            File.Move(tempPath, targetPath);
            return true;
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // ignored
            }
        }
    }

    private static bool TryCaptureWindowHash(IntPtr windowHandle, out string hash)
    {
        hash = string.Empty;
        if (windowHandle == IntPtr.Zero)
        {
            return false;
        }

        if (!TryGetClientScreenRect(windowHandle, out var rect))
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

    private static bool TryCaptureWindowPngWithTimeout(IntPtr windowHandle, string outputPath)
    {
        var timeoutMs = ReadPositiveIntFromEnvironment("IKA_GAME_CAPTURE_TIMEOUT_MS", 1800);
        return RunBounded(() => CaptureWindowPng(windowHandle, outputPath), timeoutMs, false);
    }

    private static int ComputeNativeScriptTimeoutMs(string script, int stepDelayMs)
    {
        var minimumTimeoutMs = ReadPositiveIntFromEnvironment("IKA_NATIVE_SCRIPT_TIMEOUT_MS", 6_000);
        var tokenCount = CountScriptTokens(script);
        var estimatedDurationMs = (tokenCount * Math.Max(stepDelayMs, 120)) + 1_800;
        return Math.Max(minimumTimeoutMs, estimatedDurationMs);
    }

    private static int CountScriptTokens(string script)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            return 0;
        }

        return script
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Length;
    }

    private static bool TryCaptureWindowHashWithTimeout(IntPtr windowHandle, out string hash)
    {
        var timeoutMs = ReadPositiveIntFromEnvironment("IKA_GAME_FRAME_HASH_TIMEOUT_MS", 900);
        var computed = RunBounded(
            () => TryCaptureWindowHash(windowHandle, out var value) ? value : string.Empty,
            timeoutMs,
            string.Empty
        );
        hash = computed;
        return !string.IsNullOrWhiteSpace(hash);
    }

    private static T RunBounded<T>(Func<T> operation, int timeoutMs, T fallback)
    {
        try
        {
            var task = Task.Run(operation);
            if (task.Wait(timeoutMs))
            {
                return task.Result;
            }
        }
        catch
        {
            // ignored
        }

        return fallback;
    }

    private static int ReadPositiveIntFromEnvironment(string name, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out var parsed) && parsed > 0 ? parsed : fallback;
    }

    private static bool TryGetClientScreenRect(IntPtr windowHandle, out Rect rect)
    {
        rect = default;
        if (windowHandle == IntPtr.Zero)
        {
            return false;
        }

        if (!GetClientRect(windowHandle, out var clientRect))
        {
            return false;
        }

        var topLeft = new PointNative { X = clientRect.Left, Y = clientRect.Top };
        var bottomRight = new PointNative { X = clientRect.Right, Y = clientRect.Bottom };
        if (!ClientToScreen(windowHandle, ref topLeft) || !ClientToScreen(windowHandle, ref bottomRight))
        {
            return false;
        }

        if (bottomRight.X <= topLeft.X || bottomRight.Y <= topLeft.Y)
        {
            return false;
        }

        rect = new Rect
        {
            Left = topLeft.X,
            Top = topLeft.Y,
            Right = bottomRight.X,
            Bottom = bottomRight.Y,
        };
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
                TapAltKey();
                _ = BringWindowToTop(windowHandle);
                _ = SetForegroundWindow(windowHandle);
                _ = SetActiveWindow(windowHandle);
                _ = SetFocus(windowHandle);
                Thread.Sleep(90);
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

    private static void TapAltKey()
    {
        keybd_event(VirtualKeyMenu, 0, 0, UIntPtr.Zero);
        keybd_event(VirtualKeyMenu, 0, KeyeventfKeyUp, UIntPtr.Zero);
    }

    private static bool TryAppActivate(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return false;
            }

            Microsoft.VisualBasic.Interaction.AppActivate(process.Id);
            Thread.Sleep(180);
            process.Refresh();
            var handle = process.MainWindowHandle;
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            return GetForegroundWindow() == handle || FocusWindow(handle);
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
    private static extern IntPtr SetActiveWindow(IntPtr hWnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool GetClientRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool ClientToScreen(IntPtr hWnd, ref PointNative lpPoint);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        int tokenInformationClass,
        out TokenElevation tokenInformation,
        int tokenInformationLength,
        out int returnLength
    );

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private static bool TryGetProcessElevation(int processId, out bool isElevated)
    {
        isElevated = false;
        var processHandle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (processHandle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            if (!OpenProcessToken(processHandle, TokenQuery, out var tokenHandle) || tokenHandle == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                var tokenElevation = new TokenElevation();
                var resultLength = 0;
                if (!GetTokenInformation(
                        tokenHandle,
                        TokenElevationClass,
                        out tokenElevation,
                        Marshal.SizeOf<TokenElevation>(),
                        out resultLength
                    ))
                {
                    return false;
                }

                isElevated = tokenElevation.TokenIsElevated != 0;
                return true;
            }
            finally
            {
                _ = CloseHandle(tokenHandle);
            }
        }
        finally
        {
            _ = CloseHandle(processHandle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointNative
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenElevation
    {
        public int TokenIsElevated;
    }
}
