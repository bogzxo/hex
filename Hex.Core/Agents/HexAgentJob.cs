using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Hex.Core;

/// <summary>
/// The processes of one agent's run held together, on Windows a job object the run's program is put in as it starts, so
/// everything it starts is in it too, however deep and whatever ends in between. Ending the job ends every one of them,
/// and the job ends of itself when Hex does, closed or crashed or killed. Killing the run's tree found only what was still
/// joined to it by its parents: a self test a shell started and then left, or a build's server, went on with nobody
/// watching it, and an agent whose turn outlived a restart of Hex went on working beside the one started after it. What
/// asks to break away (the bridge opening a Hex window of its own) is let go. Linux and macOS have a session and group
/// of the run's own instead (HexAgentGroup).
/// </summary>
internal sealed partial class HexAgentJob : IDisposable
{
    private nint job;

    private HexAgentJob(nint job) => this.job = job;

    /// <summary>A job holding the process from here on, null where there are none or it couldn't be made.</summary>
    public static HexAgentJob? Hold(Process process)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        nint made = CreateJobObjectW(0, null);
        if (made == 0)
            return null;

        var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = KILL_ON_JOB_CLOSE | BREAKAWAY_OK } };
        if (!SetInformationJobObject(made, EXTENDED_LIMIT_INFORMATION, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
        {
            CloseHandle(made);
            return null;
        }

        try
        {
            if (!AssignProcessToJobObject(made, process.Handle))
            {
                CloseHandle(made);
                return null;
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Gone before it could be held, there's nothing to hold
            CloseHandle(made);
            return null;
        }

        return new HexAgentJob(made);
    }

    /// <summary>Every process in it ended, and the job let go.</summary>
    public void Dispose()
    {
        nint held = Interlocked.Exchange(ref job, 0);
        if (held == 0)
            return;

        TerminateJobObject(held, 1);
        CloseHandle(held);
    }

    private const uint KILL_ON_JOB_CLOSE = 0x2000, BREAKAWAY_OK = 0x800;
    private const int EXTENDED_LIMIT_INFORMATION = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateJobObjectW(nint attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(nint job, int infoClass, ref ExtendedLimits info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateJobObject(nint job, uint exitCode);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
