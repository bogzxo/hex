using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Hex.Core.Notebooks;

// Stopping what the kernel's running without stopping the kernel. A kernel that says it's interrupted by a message gets
// one on its control channel. Otherwise it's a signal, SIGINT on Linux and macOS, and on Windows (which has no SIGINT to
// send another process) an event the kernel was handed at its start and waits on, which is how Jupyter does it there.
// ipykernel's interrupt_request only reaches what the kernel itself started, so the signal it is
internal sealed partial class Kernel
{
    private IntPtr interruptEvent;

    /// <summary>Stops the cell that's running, the kernel and what it knows stay.</summary>
    public void Interrupt()
    {
        if (spec.InterruptByMessage)
        {
            Send(CONTROL, JupyterMessage.Make(session, "interrupt_request", []));
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            if (interruptEvent != IntPtr.Zero)
                SetEvent(interruptEvent);
        }
        else if (process is { HasExited: false } running)
        {
            _ = kill(running.Id, SIGINT);
        }
    }

    // Helper method to make the event a Windows kernel waits on, one it inherits, its number given it in its environment
    private void PrepareInterrupt(ProcessStartInfo start)
    {
        if (!OperatingSystem.IsWindows() || spec.InterruptByMessage)
            return;

        var inherited = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = 1 };
        interruptEvent = CreateEventW(ref inherited, 0, 0, IntPtr.Zero);
        if (interruptEvent != IntPtr.Zero)
            start.Environment["JPY_INTERRUPT_EVENT"] = interruptEvent.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private void ReleaseInterrupt()
    {
        if (interruptEvent != IntPtr.Zero && OperatingSystem.IsWindows())
            CloseHandle(interruptEvent);
        interruptEvent = IntPtr.Zero;
    }

    private const int SIGINT = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr CreateEventW(ref SECURITY_ATTRIBUTES attributes, int manualReset, int initialState, IntPtr name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetEvent(IntPtr handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int kill(int pid, int signal);
}
