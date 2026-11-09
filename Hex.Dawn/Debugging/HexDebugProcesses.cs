using System.Diagnostics;

namespace Hex.Dawn;

// Start time prevents a process picker entry from silently attaching to a reused PID.
internal sealed record HexDebugProcess(int Id, string Name, DateTime Started)
{
    public string Label => $"{Name} ({Id})";

    public static HexDebugProcess? Find(int id)
    {
        if (id <= 0 || id == Environment.ProcessId) return null;
        try
        {
            using var process = Process.GetProcessById(id);
            return process.HasExited ? null : new(id, process.ProcessName, process.StartTime.ToUniversalTime());
        }
        catch (Exception) { return null; } // Exited, inaccessible, or protected by the OS.
    }

    public bool IsCurrent() => Find(Id) is {} current && current.Started == Started;

    public static IReadOnlyList<HexDebugProcess> List()
    {
        var found = new List<HexDebugProcess>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id != Environment.ProcessId && !process.HasExited)
                        found.Add(new(process.Id, process.ProcessName, process.StartTime.ToUniversalTime()));
                }
                catch (Exception) { } // One inaccessible process must not hide the rest.
            }
        }
        return found.OrderBy(process => process.Name, StringComparer.OrdinalIgnoreCase).ThenBy(process => process.Id).ToArray();
    }
}
