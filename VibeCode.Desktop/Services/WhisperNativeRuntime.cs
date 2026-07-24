using System.IO;
using System.Runtime.InteropServices;
using Whisper.net.LibraryLoader;

namespace VibeCode.Services;

/// <summary>
/// Points Whisper.net at native libraries extracted by a self-contained single-file app.
/// Whisper.net probes beside the executable, while the .NET host extracts bundled native
/// files to a private temp directory and exposes that directory through AppContext.
/// </summary>
internal static class WhisperNativeRuntime
{
    private const string NativeSearchDirectoriesKey = "NATIVE_DLL_SEARCH_DIRECTORIES";

    public static void Configure()
    {
        if (RuntimeOptions.LoadedLibrary.HasValue || !string.IsNullOrWhiteSpace(RuntimeOptions.LibraryPath)) return;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            _ => null,
        };
        if (architecture is null) return;

        var searchDirectories = AppContext.GetData(NativeSearchDirectoriesKey) as string;
        if (string.IsNullOrWhiteSpace(searchDirectories)) return;

        var runtimeRoot = searchDirectories
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(root =>
                File.Exists(Path.Combine(root, "runtimes", $"win-{architecture}", "whisper.dll")));
        if (runtimeRoot is null) return;

        // Whisper.net takes DirectoryName(LibraryPath), then probes that directory's runtimes/ child.
        // The marker does not need to exist; it lets the package retain its normal runtime selection logic.
        RuntimeOptions.LibraryPath = Path.Combine(runtimeRoot, ".vibecode-whisper-root");
    }
}
