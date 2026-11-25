using System.Diagnostics;
using System.Globalization;

namespace Hex.Core;

/// <summary>What the graphics card has in use now, for the Local AI window's overview. NVIDIA's nvidia-smi says it; elsewhere it isn't known.</summary>
internal static class LocalAiGpu
{
    /// <summary>The MiB of the biggest NVIDIA card in use and its total, null when nvidia-smi isn't there. Slow: never on the editor's thread.</summary>
    public static (long UsedMiB, long TotalMiB)? Usage()
    {
        if (Volatile.Read(ref missing))
            return null;
        try
        {
            using var process = Process.Start(new ProcessStartInfo("nvidia-smi", "--query-gpu=memory.used,memory.total --format=csv,noheader,nounits")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            });
            if (process is null)
                return null;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(3000))
            {
                try { process.Kill(true); } catch (InvalidOperationException) { }
                return null;
            }
            return process.ExitCode == 0 ? Parse(output.GetAwaiter().GetResult()) : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No nvidia-smi on this machine (a failed start throws): it isn't looked for again, a driver installed later is seen at the next start
            Volatile.Write(ref missing, true);
            return null;
        }
        catch (Exception e) when (e is InvalidOperationException or IOException)
        {
            return null;
        }
    }

    private static bool missing;

    /// <summary>The biggest card of nvidia-smi's "used, total" lines in MiB, null for nothing readable.</summary>
    public static (long UsedMiB, long TotalMiB)? Parse(string said)
    {
        (long, long)? best = null;
        foreach (string line in said.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length >= 2 && long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long used)
                && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long total) && (best is null || total > best.Value.Item2))
                best = (used, total);
        }
        return best;
    }
}
