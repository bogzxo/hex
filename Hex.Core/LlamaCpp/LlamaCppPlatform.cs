using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Hex.Core;

/// <summary>The graphics card llama.cpp would run on: who made it, its name, its memory, and the CUDA its driver runs (NVIDIA's).</summary>
internal sealed record LlamaCppGpu(string Vendor, string Name, long MemoryMiB, Version? Cuda = null)
{
    public const string NVIDIA = "nvidia", AMD = "amd", APPLE = "apple", INTEL = "intel", OTHER = "other";
}

/// <summary>
/// The machine llama.cpp is set up for: its system (windows, macos, linux), its processor (x64, arm64), its memory and
/// its graphics card, null when none was found. Found once by <see cref="Detect"/>, every fact what the system says, the
/// card's from nvidia-smi for NVIDIA's and the system's list of display adapters for the rest.
/// </summary>
internal sealed partial record LlamaCppPlatform(string Os, string Arch, long MemoryMiB, LlamaCppGpu? Gpu)
{
    public const string WINDOWS = "windows", MACOS = "macos", LINUX = "linux";

    /// <summary>The memory llama.cpp can count on: a GPU's own, or on a Mac with one memory for both, the part the GPU may use.</summary>
    public long VramMiB => Gpu is { Vendor: LlamaCppGpu.APPLE } ? MemoryMiB * 2 / 3 : Gpu?.MemoryMiB ?? 0;

    /// <summary>The system this is, in llama.cpp's words.</summary>
    public static string ThisOs => OperatingSystem.IsWindows() ? WINDOWS : OperatingSystem.IsMacOS() ? MACOS : LINUX;

    /// <summary>The processor this is, x64 or arm64.</summary>
    public static string ThisArch => RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";

    /// <summary>What this machine is, asked of the system and the card's own tools. Slow (a few hundred ms), never on the editor's thread.</summary>
    public static LlamaCppPlatform Detect()
    {
        string os = ThisOs, arch = ThisArch;
        long memory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
        LlamaCppGpu? gpu = null;

        if (os == MACOS && arch == "arm64")
            gpu = new LlamaCppGpu(LlamaCppGpu.APPLE, "Apple silicon", memory);
        else if (Run("nvidia-smi", "--query-gpu=name,memory.total,driver_version --format=csv,noheader,nounits") is { } cards
                 && Run("nvidia-smi", string.Empty) is { } header)
            gpu = NvidiaGpu(cards, header);
        gpu ??= os == WINDOWS ? WindowsGpu() : os == LINUX ? LinuxGpu() : null;

        return new LlamaCppPlatform(os, arch, memory, gpu);
    }

    /// <summary>
    /// The biggest NVIDIA card nvidia-smi lists (name, memory in MiB, driver per line) and the CUDA its driver runs, from
    /// the header of nvidia-smi on its own ("CUDA Version: 12.4", newer drivers "CUDA UMD Version: 13.5"). Null with no card.
    /// </summary>
    public static LlamaCppGpu? NvidiaGpu(string cards, string header)
    {
        LlamaCppGpu? best = null;
        foreach (string line in cards.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long mib))
                continue;
            if (best is null || mib > best.MemoryMiB)
                best = new LlamaCppGpu(LlamaCppGpu.NVIDIA, parts[0], mib);
        }

        if (best is null)
            return null;
        Match cuda = CudaVersion().Match(header);
        return cuda.Success && Version.TryParse(cuda.Groups[1].Value, out Version? version) ? best with { Cuda = version } : best;
    }

    [GeneratedRegex(@"CUDA (?:UMD )?Version:\s*([0-9]+\.[0-9]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CudaVersion();

    /// <summary>Who made a display adapter, by its name, as the vendors name their cards.</summary>
    public static string VendorOf(string name) => name.ToLowerInvariant() switch
    {
        var n when n.Contains("nvidia") || n.Contains("geforce") || n.Contains("quadro") || n.Contains("rtx") => LlamaCppGpu.NVIDIA,
        var n when n.Contains("amd") || n.Contains("radeon") => LlamaCppGpu.AMD,
        var n when n.Contains("intel") || n.Contains("arc") || n.Contains("iris") => LlamaCppGpu.INTEL,
        _ => LlamaCppGpu.OTHER,
    };

    // Windows says its adapters through PowerShell; the memory it says stops at 4 GiB, so it's a floor, not the card's own
    private static LlamaCppGpu? WindowsGpu()
    {
        string? said = Run("powershell", "-NoProfile -NonInteractive -Command \"Get-CimInstance Win32_VideoController | ForEach-Object { $_.Name + '|' + $_.AdapterRAM }\"");
        if (said is null)
            return null;
        LlamaCppGpu? best = null;
        foreach (string line in said.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = line.Split('|');
            string vendor = VendorOf(parts[0]);
            if (vendor == LlamaCppGpu.OTHER && parts[0].Contains("Basic", StringComparison.OrdinalIgnoreCase))
                continue;
            long mib = parts.Length > 1 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long bytes) ? bytes / (1024 * 1024) : 0;
            var card = new LlamaCppGpu(vendor, parts[0], mib);
            if (best is null || Rank(card) > Rank(best))
                best = card;
        }
        return best;
    }

    // Linux lists its adapters through lspci; their memory isn't said there, rocm-smi says an AMD card's
    private static LlamaCppGpu? LinuxGpu()
    {
        string? said = Run("lspci", string.Empty);
        if (said is null)
            return null;
        string? line = said.Split('\n').FirstOrDefault(each => each.Contains("VGA", StringComparison.Ordinal) || each.Contains("3D controller", StringComparison.Ordinal));
        if (line is null)
            return null;
        string name = line[(line.IndexOf(": ", StringComparison.Ordinal) + 2)..].Trim();
        return new LlamaCppGpu(VendorOf(name), name, 0);
    }

    // A discrete card before a built-in one
    private static int Rank(LlamaCppGpu card) => card.Vendor switch
    {
        LlamaCppGpu.NVIDIA => 3,
        LlamaCppGpu.AMD => 2,
        LlamaCppGpu.INTEL => 1,
        _ => 0,
    } * 1_000_000 + (int)Math.Min(card.MemoryMiB, 999_999);

    // What a program prints, null when it isn't there, fails or takes more than five seconds
    private static string? Run(string program, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(program, arguments)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            });
            if (process is null)
                return null;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(true); } catch (InvalidOperationException) { }
                return null;
            }
            return process.ExitCode == 0 ? output.GetAwaiter().GetResult() : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
