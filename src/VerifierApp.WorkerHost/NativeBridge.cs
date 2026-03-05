using System.Runtime.InteropServices;
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

    [DllImport("ika_native.dll", EntryPoint = "ika_native_lock_input", CallingConvention = CallingConvention.Cdecl)]
    private static extern int IkaNativeLockInput();

    [DllImport("ika_native.dll", EntryPoint = "ika_native_unlock_input", CallingConvention = CallingConvention.Cdecl)]
    private static extern int IkaNativeUnlockInput();

    [DllImport("ika_native.dll", EntryPoint = "ika_native_capture_frame_hash", CallingConvention = CallingConvention.Cdecl)]
    private static extern int IkaNativeCaptureFrameHash(byte[] outputBuffer, int outputBufferLength);

    [DllImport("ika_native.dll", EntryPoint = "ika_native_execute_scan_script", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int IkaNativeExecuteScanScript(string script, int stepDelayMs);
}
