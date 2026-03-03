using System.Reflection;
using System.Runtime.InteropServices;

namespace VerifierApp.WorkerHost;

public static class NativeLibraryBootstrap
{
    private static readonly object Sync = new();
    private static bool _initialized;
    private static string? _nativeDllPath;

    public static void Initialize(string nativeDllPath)
    {
        lock (Sync)
        {
            if (_initialized)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(nativeDllPath) || !File.Exists(nativeDllPath))
            {
                throw new FileNotFoundException("Bundled native DLL is missing.", nativeDllPath);
            }

            _nativeDllPath = nativeDllPath;
            NativeLibrary.SetDllImportResolver(
                typeof(NativeBridge).Assembly,
                Resolve
            );
            _initialized = true;
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (_nativeDllPath is null)
        {
            return IntPtr.Zero;
        }
        if (!libraryName.Equals("ika_native.dll", StringComparison.OrdinalIgnoreCase) &&
            !libraryName.Equals("ika_native", StringComparison.OrdinalIgnoreCase))
        {
            return IntPtr.Zero;
        }
        return NativeLibrary.Load(_nativeDllPath);
    }
}
