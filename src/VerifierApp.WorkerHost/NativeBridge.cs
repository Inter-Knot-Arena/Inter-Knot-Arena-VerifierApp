using System.Runtime.InteropServices;
using System.Drawing;
using System.Drawing.Imaging;
using VerifierApp.Core.Services;

namespace VerifierApp.WorkerHost;

public sealed class NativeBridge : INativeBridge
{
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
}
