using System.IO;
using System.Runtime.InteropServices;
using Whisper.net.LibraryLoader;

namespace VibeCode.Services;

/// <summary>
/// Chooses which whisper.cpp build dictation runs on, and points Whisper.net at native libraries extracted by a
/// self-contained single-file app. Whisper.net probes beside the executable, while the .NET host extracts bundled
/// native files to a private temp directory and exposes that directory through AppContext.
/// </summary>
internal static class WhisperNativeRuntime
{
    private const string NativeSearchDirectoriesKey = "NATIVE_DLL_SEARCH_DIRECTORIES";

    // GPU first, CPU as the fallback — the only two builds this app ships. Whisper.net's stock order also probes
    // Cuda/CoreML/OpenVino, which we deliberately do not bundle, so naming ours skips four guaranteed-miss probes.
    //
    // Both no-GPU paths were tested and are safe, which is what makes GPU-first the right default rather than a
    // gamble (4.6 s clip, this box):
    //   * Vulkan natives unavailable (loader can't load the DLL) -> Whisper.net silently moves to Cpu.  1.85 s
    //   * Vulkan DLL loads but no usable device (no driver, headless, a VM) -> ggml logs the miss and computes on
    //     the CPU inside the Vulkan build. Correct text, no crash, no hang.                             1.48 s
    //   * A working GPU.                                                                                0.22 s
    private static readonly List<RuntimeLibrary> PreferGpu =
        new() { RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu };

    /// <summary>True once a GPU whisper build is the one actually loaded. Meaningless before the first factory load,
    /// which is where Whisper.net resolves the runtime — ask after it, not before.</summary>
    public static bool UsesGpu => RuntimeOptions.LoadedLibrary is RuntimeLibrary.Vulkan;

    public static void Configure()
    {
        if (RuntimeOptions.LoadedLibrary.HasValue || !string.IsNullOrWhiteSpace(RuntimeOptions.LibraryPath)) return;
        RuntimeOptions.RuntimeLibraryOrder = PreferGpu;
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
