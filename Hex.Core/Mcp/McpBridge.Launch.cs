using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Hex.Core;

// Starting a Hex for the bridge, and the console it may have been given. On Windows the Hex that's started gets no console
// and none of the bridge's handles. Inheriting them (which Process.Start does with every handle that can be) kept the
// client's pipes open in a Hex that lives on after the bridge, and the client never heard the bridge's stdout end. It's
// let out of the client's job as well where the job allows, so a client that takes its whole tree down on the way out
// doesn't take the editor with it.
internal static partial class McpBridge
{
    /// <summary>
    /// Whether stdin is a client's rather than a person's, a pipe or a file and not the console. <c>Hex --mcp</c> typed at
    /// a prompt is somebody wanting Hex with its server on, not a bridge waiting on a keyboard for JSON. A Release Hex
    /// started from a prompt has no stdin at all, which is no client either.
    /// </summary>
    public static bool StdinIsAClient()
    {
        if (!OperatingSystem.IsWindows())
            return Console.IsInputRedirected;

        nint input = GetStdHandle(STD_INPUT_HANDLE);
        return input != 0 && input != -1 && GetFileType(input) is FILE_TYPE_PIPE or FILE_TYPE_DISK;
    }

    /// <summary>Lets go of a console nobody else is on, the one Windows made for a Debug Hex started by a program without one.</summary>
    private static void DropOwnConsole()
    {
        if (!OperatingSystem.IsWindows())
            return;

        uint[] on = new uint[4];
        if (GetConsoleProcessList(on, (uint)on.Length) == 1)
            FreeConsole();
    }

    /// <summary>Starts a program with arguments, detached from this one, its process id back.</summary>
    private static int Launch(string program, string[] arguments)
    {
        if (!OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in arguments)
                start.ArgumentList.Add(argument);

            Process started = Process.Start(start) ?? throw new InvalidOperationException("nothing was started");

            // Its output read and dropped, so it never fills a pipe and stops, and its stdin never the client's
            started.OutputDataReceived += (_, _) => { };
            started.ErrorDataReceived += (_, _) => { };
            started.BeginOutputReadLine();
            started.BeginErrorReadLine();
            return started.Id;
        }

        string line = string.Join(' ', new[] { program }.Concat(arguments).Select(Quoted));
        int pid = CreateDetached(program, line, CREATE_BREAKAWAY_FROM_JOB);

        // A job that won't let its processes out says no to the breakaway, the Hex is started in it then
        if (pid == 0 && Marshal.GetLastPInvokeError() == ERROR_ACCESS_DENIED)
            pid = CreateDetached(program, line, 0);
        if (pid == 0)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        return pid;
    }

    private static unsafe int CreateDetached(string program, string line, uint extra)
    {
        var startup = new StartupInfo { Size = sizeof(StartupInfo) };
        char[] command = (line + '\0').ToCharArray();
        fixed (char* text = command)
        {
            if (!CreateProcessW(program, text, 0, 0, false, DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP | CREATE_UNICODE_ENVIRONMENT | extra,
                0, AppContext.BaseDirectory, ref startup, out ProcessInformation made))
                return 0;

            CloseHandle(made.Thread);
            CloseHandle(made.Process);
            return made.ProcessId;
        }
    }

    /// <summary>An argument as Windows reads a command line back, in quotes with the backslashes before a quote doubled.</summary>
    internal static string Quoted(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0)
            return argument;

        var quoted = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\')
            {
                slashes++;
                continue;
            }

            quoted.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            slashes = 0;
            quoted.Append(c);
        }

        quoted.Append('\\', slashes * 2).Append('"');
        return quoted.ToString();
    }

    private const int STD_INPUT_HANDLE = -10;
    private const uint FILE_TYPE_DISK = 1, FILE_TYPE_PIPE = 3;
    private const uint DETACHED_PROCESS = 0x8, CREATE_NEW_PROCESS_GROUP = 0x200, CREATE_UNICODE_ENVIRONMENT = 0x400, CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
    private const int ERROR_ACCESS_DENIED = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public nint Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, Reserved2Size;
        public nint Reserved2, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process, Thread;
        public int ProcessId, ThreadId;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CreateProcessW(string application, char* commandLine, nint processAttributes, nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, nint environment, string currentDirectory, ref StartupInfo startup, out ProcessInformation made);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetStdHandle(int which);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetFileType(nint handle);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetConsoleProcessList([Out] uint[] processes, uint count);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeConsole();
}
