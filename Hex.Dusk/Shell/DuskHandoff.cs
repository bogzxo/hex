using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;

using Microsoft.Win32.SafeHandles;

using Horizon.Core.Processes;

namespace Hex.Dusk;

/// <summary>
/// Dusk taking the consoles Windows passes on while it's the default terminal (Hex.Shell's HexDefaultTerminal). Windows
/// Terminal's console host asks COM for Dusk's class and calls ITerminalHandoff3 on it with the console, and what comes
/// over becomes a tab (TryTake, on Dusk's own thread). A Dusk that's running registers the class as it starts, so the
/// consoles come to it as tabs, and with none running Windows starts Hex --dusk -Embedding for one.
/// <para>
/// The interface is Windows Terminal's (src/host/proxy/ITerminalHandoff.idl), and the proxy that carries its handles from
/// one process to the other is the one Windows Terminal installs. COM lets go of the handles it brought once the call is
/// over, so the ones kept are copies, and the two pipes handed back are COM's to take over.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class DuskHandoff
{
    private const int S_OK = 0, E_POINTER = unchecked((int)0x80004003), CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);
    private const uint CLSCTX_LOCAL_SERVER = 0x4, REGCLS_MULTIPLEUSE = 1, COINIT_MULTITHREADED = 0;
    private const uint DUPLICATE_SAME_ACCESS = 0x2, SYNCHRONIZE = 0x00100000, PROCESS_TERMINATE = 0x1, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    private static readonly ConcurrentQueue<(PseudoTerminal Console, string Title)> arrived = new();
    private static readonly StrategyBasedComWrappers wrappers = new();
    private static readonly Lock gate = new();
    private static Thread? listener;
    private static ManualResetEventSlim? stop;

    /// <summary>Whether the class is registered with COM now, consoles come here.</summary>
    public static bool Listening { get; private set; }

    /// <summary>
    /// Registers Dusk's class with COM, on a thread of its own in the multithreaded apartment that stays for as long as
    /// it's registered (the calls come on COM's threads). Waits for it to be done, which is a moment.
    /// </summary>
    public static void Listen()
    {
        lock (gate)
        {
            if (listener is not null)
                return;

            var ready = new ManualResetEventSlim(false);
            var stopping = new ManualResetEventSlim(false);
            stop = stopping;
            listener = new Thread(() =>
            {
                CoInitializeEx(0, COINIT_MULTITHREADED);
                nint factory = wrappers.GetOrCreateComInterfaceForObject(new HandoffFactory(), CreateComInterfaceFlags.None);
                Guid dusk = Hex.Shell.HexDefaultTerminal.DuskClass;
                int registered = CoRegisterClassObject(&dusk, factory, CLSCTX_LOCAL_SERVER, REGCLS_MULTIPLEUSE, out uint cookie);
                Listening = registered == S_OK;
                if (!Listening)
                    Console.WriteLine($"[Dusk] The default terminal's class couldn't be registered, 0x{registered:X8}");
                ready.Set();

                stopping.Wait();
                if (Listening)
                    CoRevokeClassObject(cookie);
                Listening = false;
                Marshal.Release(factory);
            }) { IsBackground = true, Name = "dusk handoff" };
            listener.Start();
            ready.Wait(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>Takes the class back off COM, Dusk isn't the default terminal any more.</summary>
    public static void Stop()
    {
        lock (gate)
        {
            stop?.Set();
            listener?.Join(TimeSpan.FromSeconds(2));
            listener = null;
            stop = null;
        }
    }

    /// <summary>A console that was passed on and isn't a tab yet, with what it's called. Dusk's own thread, every tick.</summary>
    public static bool TryTake(out PseudoTerminal console, out string title)
    {
        if (arrived.TryDequeue(out var next))
        {
            (console, title) = next;
            return true;
        }

        console = null!;
        title = string.Empty;
        return false;
    }

    /// <summary>
    /// A console passed on. The pipes the console host reads its input from and writes its output to are made here and
    /// handed back (ITerminalHandoff3 lets the terminal make them), Dusk keeping the other ends, and copies are kept of the
    /// signal pipe, the console's reference and the two processes. On COM's thread.
    /// </summary>
    internal static int Establish(nint* input, nint* output, nint signal, nint reference, nint server, nint client, TerminalStartupInfo* startup)
    {
        if (input is null || output is null)
            return E_POINTER;

        try
        {
            SafeFileHandle ownSignal = Copy(signal), ownReference = Copy(reference), ownServer = Copy(server);
            SafeFileHandle ownClient = Copy(client, SYNCHRONIZE | PROCESS_TERMINATE | PROCESS_QUERY_LIMITED_INFORMATION);

            if (!CreatePipe(out nint consoleReads, out nint duskWrites, 0, 0))
                return Marshal.GetHRForLastWin32Error();
            if (!CreatePipe(out nint duskReads, out nint consoleWrites, 0, 0))
            {
                CloseHandle(consoleReads);
                CloseHandle(duskWrites);
                return Marshal.GetHRForLastWin32Error();
            }

            string title = startup is not null && startup->Title != 0 ? Marshal.PtrToStringBSTR(startup->Title) : string.Empty;
            if (title.Length == 0)
                title = ProgramOf(ownClient) ?? "console";

            PseudoTerminal console = PseudoTerminal.HandedOver(new SafeFileHandle(duskWrites, true), new SafeFileHandle(duskReads, true),
                ownSignal, ownReference, ownServer, ownClient, title);
            *input = consoleReads;
            *output = consoleWrites;
            arrived.Enqueue((console, title));
            Console.WriteLine($"[Dusk] A console was passed on, {title}");
            return S_OK;
        }
        catch (Exception problem)
        {
            Console.WriteLine($"[Dusk] A console passed on couldn't be taken, {problem.Message}");
            return problem.HResult != 0 ? problem.HResult : unchecked((int)0x80004005);
        }
    }

    // A handle of COM's copied for keeping, with the same access, or with what's asked for when it can be had
    private static SafeFileHandle Copy(nint handle, uint access = 0)
    {
        nint self = GetCurrentProcess();
        if (access != 0 && DuplicateHandle(self, handle, self, out nint asked, access, false, 0))
            return new SafeFileHandle(asked, true);
        if (!DuplicateHandle(self, handle, self, out nint copied, 0, false, DUPLICATE_SAME_ACCESS))
            throw new System.ComponentModel.Win32Exception();
        return new SafeFileHandle(copied, true);
    }

    // What the program is called, for a tab with no title of its own
    private static string? ProgramOf(SafeFileHandle process)
    {
        char* buffer = stackalloc char[1024];
        uint size = 1024;
        return QueryFullProcessImageNameW(process, 0, buffer, ref size) ? Path.GetFileName(new string(buffer, 0, (int)size)) : null;
    }

    /* COM */

    /// <summary>What the console host says about the console it passes on, TERMINAL_STARTUP_INFO.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct TerminalStartupInfo
    {
        public nint Title, IconPath;
        public int IconIndex;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow;
    }

    [GeneratedComInterface]
    [Guid("6F23DA90-15C5-4203-9DB0-64E73F1B1B00")]
    internal partial interface ITerminalHandoff3
    {
        [PreserveSig]
        int EstablishPtyHandoff(nint* input, nint* output, nint signal, nint reference, nint server, nint client, TerminalStartupInfo* startup);
    }

    [GeneratedComInterface]
    [Guid("00000001-0000-0000-C000-000000000046")]
    internal partial interface IClassFactory
    {
        [PreserveSig]
        int CreateInstance(nint outer, Guid* iid, nint* made);

        [PreserveSig]
        int LockServer(int locked);
    }

    [GeneratedComClass]
    internal sealed partial class Handoff : ITerminalHandoff3
    {
        public int EstablishPtyHandoff(nint* input, nint* output, nint signal, nint reference, nint server, nint client, TerminalStartupInfo* startup) =>
            Establish(input, output, signal, reference, server, client, startup);
    }

    [GeneratedComClass]
    internal sealed partial class HandoffFactory : IClassFactory
    {
        public int CreateInstance(nint outer, Guid* iid, nint* made)
        {
            if (made is null || iid is null)
                return E_POINTER;
            *made = 0;
            if (outer != 0)
                return CLASS_E_NOAGGREGATION;

            nint unknown = wrappers.GetOrCreateComInterfaceForObject(new Handoff(), CreateComInterfaceFlags.None);
            int found = Marshal.QueryInterface(unknown, in *iid, out nint asked);
            Marshal.Release(unknown);
            *made = asked;
            return found;
        }

        public int LockServer(int locked) => S_OK;
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint model);

    [LibraryImport("ole32.dll")]
    private static partial int CoRegisterClassObject(Guid* clsid, nint unknown, uint context, uint flags, out uint cookie);

    [LibraryImport("ole32.dll")]
    private static partial int CoRevokeClassObject(uint cookie);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreatePipe(out nint read, out nint write, nint attributes, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DuplicateHandle(nint sourceProcess, nint source, nint targetProcess, out nint target, uint access,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageNameW(SafeFileHandle process, uint flags, char* name, ref uint size);
}
