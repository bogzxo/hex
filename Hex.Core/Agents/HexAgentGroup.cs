using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace Hex.Core;

/// <summary>
/// An agent's run on Linux and macOS, started in a session of its own (and so a process group of its own, its id the
/// run's own) the way a job holds it on Windows (HexAgentJob). Whatever it starts is in the group too unless it makes a
/// session of its own on purpose, so killing the group ends all of it, what a shell started and left included, which
/// killing the run's tree never found once their parent had gone. Every group alive is told to a watcher, a small sh
/// of Hex's own reading a pipe only Hex holds, that kills them all when the pipe closes, which is when Hex is gone
/// however it went. .NET's Process can't start anything in a group of its own, so it's started with posix_spawn.
/// </summary>
internal sealed unsafe partial class HexAgentGroup : IDisposable
{
    /// <summary>Its process id, the id of its session and group too.</summary>
    public int Id { get; }

    /// <summary>Its stdin, stdout and stderr, Hex's ends of the pipes. Null where it wasn't given any.</summary>
    public Stream? Input { get; }
    public Stream? Output { get; }
    public Stream? Error { get; }

    private readonly ManualResetEventSlim ended = new();
    private int exitCode = -1;
    private int killed;
    private readonly bool watched;

    private HexAgentGroup(int id, Stream? input, Stream? output, Stream? error, bool watched)
    {
        Id = id;
        Input = input;
        Output = output;
        Error = error;
        this.watched = watched;

        // Reaped as it goes, by its own id, so nothing else of Hex's that waits on its own children is in the way
        new Thread(Reap) { IsBackground = true, Name = "agent wait" }.Start();
    }

    /// <summary>Whether it ended, the group's first process, whatever is left of the rest.</summary>
    public bool HasExited => ended.IsSet;

    /// <summary>Its exit code once it's gone, 128 and the signal when one ended it.</summary>
    public int WaitForExit()
    {
        ended.Wait();
        return exitCode;
    }

    /// <summary>
    /// A program started in a session and group of its own, in a folder with an environment, its stdin piped from Hex
    /// and its stdout and stderr to it, or left as Hex's own where <paramref name="printed"/> says not, and told to the
    /// watcher where <paramref name="watched"/> says. Throws Win32Exception when it couldn't be started, as Process.Start
    /// does, and EntryPointNotFoundException where the C library hasn't what it takes.
    /// </summary>
    public static HexAgentGroup Start(string file, IReadOnlyList<string> arguments, string? folder, IEnumerable<KeyValuePair<string, string?>> environment, bool printed = true, bool watched = true)
    {
        // Ends the child takes, Hex's kept. Made close-on-exec, so nothing else Hex starts meanwhile holds them; the
        // child's are copied onto its 0, 1 and 2, which aren't
        AnonymousPipeServerStream? input = new(PipeDirection.Out, HandleInheritability.None);
        AnonymousPipeServerStream? output = printed ? new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None) : null;
        AnonymousPipeServerStream? error = printed ? new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None) : null;

        try
        {
            int id = Spawn(file, arguments, folder, environment,
                (int?)input?.ClientSafePipeHandle.DangerousGetHandle(),
                (int?)output?.ClientSafePipeHandle.DangerousGetHandle(),
                (int?)error?.ClientSafePipeHandle.DangerousGetHandle());

            input?.DisposeLocalCopyOfClientHandle();
            output?.DisposeLocalCopyOfClientHandle();
            error?.DisposeLocalCopyOfClientHandle();

            var group = new HexAgentGroup(id, input, output, error, watched);
            if (watched)
                Watcher.Add(id);
            return group;
        }
        catch
        {
            input?.Dispose();
            output?.Dispose();
            error?.Dispose();
            throw;
        }
    }

    /// <summary>Every process in its group killed, the first one's ended or not, and the watcher told it's gone.</summary>
    public void Kill()
    {
        if (Interlocked.Exchange(ref killed, 1) == 0)
        {
            // The id isn't given to another process while anything is left in its group, the first process (which can't
            // leave it, it leads the session) included until it's reaped
            kill(-Id, SIGKILL);
            if (watched)
                Watcher.Remove(Id);
        }
    }

    public void Dispose()
    {
        Kill();
        Input?.Dispose();
        Output?.Dispose();
        Error?.Dispose();
    }

    private void Reap()
    {
        while (true)
        {
            int status;
            int got = waitpid(Id, &status, 0);
            if (got == Id)
            {
                // Exited, its code in the second byte; or ended by a signal, its number in the first seven bits
                exitCode = (status & 0x7f) == 0 ? (status >> 8) & 0xff : 128 + (status & 0x7f);
                break;
            }
            if (got == -1 && Marshal.GetLastPInvokeError() != EINTR)
                break;
        }

        ended.Set();
    }

    // posix_spawn with the session (or, where the C library can't, the group) of its own, the signals Hex ignores or
    // handles set back as a program expects them, and the pipes on 0, 1 and 2. The attributes and file actions are opaque
    // and differently sized (glibc's are a few hundred bytes, macOS's a pointer), so they get room enough for any
    private static int Spawn(string file, IReadOnlyList<string> arguments, string? folder, IEnumerable<KeyValuePair<string, string?>> environment, int? stdin, int? stdout, int? stderr)
    {
        if (file.Contains('/'))
            file = Path.GetFullPath(file);

        var strings = new List<nint>();
        nint Utf8(string text)
        {
            nint made = Marshal.StringToCoTaskMemUTF8(text);
            strings.Add(made);
            return made;
        }

        nint* argv = null, envp = null;
        byte* attributes = null, actions = null, signals = null;
        bool attributesMade = false, actionsMade = false;
        try
        {
            argv = (nint*)NativeMemory.AllocZeroed((nuint)(arguments.Count + 2), (nuint)sizeof(nint));
            argv[0] = Utf8(file);
            for (int i = 0; i < arguments.Count; i++)
                argv[i + 1] = Utf8(arguments[i]);

            var variables = environment.Where(variable => variable.Value is not null).Select(variable => $"{variable.Key}={variable.Value}").ToList();
            envp = (nint*)NativeMemory.AllocZeroed((nuint)(variables.Count + 1), (nuint)sizeof(nint));
            for (int i = 0; i < variables.Count; i++)
                envp[i] = Utf8(variables[i]);

            attributes = (byte*)NativeMemory.AllocZeroed(OpaqueSize);
            actions = (byte*)NativeMemory.AllocZeroed(OpaqueSize);
            signals = (byte*)NativeMemory.AllocZeroed(OpaqueSize);

            Check(posix_spawnattr_init(attributes));
            attributesMade = true;
            Check(posix_spawn_file_actions_init(actions));
            actionsMade = true;

            if (stdin is { } i0)
                Check(posix_spawn_file_actions_adddup2(actions, i0, 0));
            if (stdout is { } i1)
                Check(posix_spawn_file_actions_adddup2(actions, i1, 1));
            if (stderr is { } i2)
                Check(posix_spawn_file_actions_adddup2(actions, i2, 2));
            if (!string.IsNullOrEmpty(folder))
                Check(posix_spawn_file_actions_addchdir_np(actions, folder));

            // Nothing blocked, and what .NET ignores (a broken pipe) or catches back to what it was, since an ignored
            // signal stays ignored through exec
            sigemptyset(signals);
            Check(posix_spawnattr_setsigmask(attributes, signals));
            foreach (int signal in (ReadOnlySpan<int>)[SIGHUP, SIGINT, SIGQUIT, SIGPIPE, SIGTERM, SIGTTIN, SIGTTOU])
                sigaddset(signals, signal);
            Check(posix_spawnattr_setsigdefault(attributes, signals));

            // A session of its own: a group of its own, and no terminal it could be stopped by reading from or have hung
            // up on it. Older C libraries without that make a group of their own
            short common = POSIX_SPAWN_SETSIGDEF | POSIX_SPAWN_SETSIGMASK;
            short session = OperatingSystem.IsMacOS() ? POSIX_SPAWN_SETSID_MACOS : POSIX_SPAWN_SETSID_LINUX;
            Check(posix_spawnattr_setflags(attributes, (short)(common | session)));

            int id;
            int failed = posix_spawnp(&id, file, actions, attributes, argv, envp);
            if (failed == EINVAL)
            {
                Check(posix_spawnattr_setflags(attributes, (short)(common | POSIX_SPAWN_SETPGROUP)));
                Check(posix_spawnattr_setpgroup(attributes, 0));
                failed = posix_spawnp(&id, file, actions, attributes, argv, envp);
            }
            if (failed != 0)
                throw new Win32Exception(failed, $"{file} couldn't be started: {Marshal.GetPInvokeErrorMessage(failed)}");

            return id;
        }
        finally
        {
            if (actionsMade)
                posix_spawn_file_actions_destroy(actions);
            if (attributesMade)
                posix_spawnattr_destroy(attributes);
            NativeMemory.Free(actions);
            NativeMemory.Free(attributes);
            NativeMemory.Free(signals);
            NativeMemory.Free(argv);
            NativeMemory.Free(envp);
            foreach (nint made in strings)
                Marshal.FreeCoTaskMem(made);
        }
    }

    private static void Check(int result)
    {
        if (result != 0)
            throw new Win32Exception(result);
    }

    /// <summary>
    /// The one sh per Hex that kills every agent group left when Hex is gone. It reads "+id" and "-id" lines down a pipe
    /// whose end only Hex holds, keeps the ids it was told of, and when the pipe closes (Hex closed, crashed or was killed,
    /// the kernel closes it all the same) kills each group still on its list. Started with the first group, again when
    /// it's found gone, told every live group then.
    /// </summary>
    internal static class Watcher
    {
        private const string Script = """
            exec >/dev/null 2>&1
            trap '' HUP INT
            groups=' '
            while read -r line; do
              case $line in
                +*) groups="$groups${line#+} " ;;
                -*) g=${line#-}; case $groups in *" $g "*) groups="${groups%% $g *} ${groups#* $g }" ;; esac ;;
              esac
            done
            for g in $groups; do kill -9 -"$g" 2>/dev/null; done
            """;

        private static readonly Lock gate = new();
        private static readonly HashSet<int> live = [];
        private static HexAgentGroup? watcher;
        private static StreamWriter? to;

        public static void Add(int id)
        {
            lock (gate)
            {
                live.Add(id);
                Say($"+{id}");
            }
        }

        public static void Remove(int id)
        {
            lock (gate)
            {
                if (live.Remove(id))
                    Say($"-{id}");
            }
        }

        // A line said to the watcher, started when there's none or it's gone, which is told every live group instead
        private static void Say(string line)
        {
            if (to is not null && !watcher!.HasExited)
            {
                try
                {
                    to.Write(line + "\n");
                    to.Flush();
                    return;
                }
                catch (IOException)
                {
                }
            }

            Drop();
            if (live.Count == 0)
                return;

            try
            {
                watcher = Start("/bin/sh", ["-c", Script.ReplaceLineEndings("\n")], "/", Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                    .Select(variable => KeyValuePair.Create((string)variable.Key, (string?)variable.Value)), printed: false, watched: false);
                to = new StreamWriter(watcher.Input!, new UTF8Encoding(false));
                foreach (int group in live)
                    to.Write($"+{group}\n");
                to.Flush();
            }
            catch (Exception e) when (e is Win32Exception or IOException or EntryPointNotFoundException or DllNotFoundException)
            {
                // No watcher, the groups end only when they're stopped
                Drop();
            }
        }

        // The watcher let go of, killed only while it's there to kill, its id anybody's once it's reaped
        private static void Drop()
        {
            if (watcher is { HasExited: false })
                watcher.Kill();
            try
            {
                to?.Dispose();
            }
            catch (IOException)
            {
            }
            watcher?.Input?.Dispose();
            to = null;
            watcher = null;
        }
    }

    private const nuint OpaqueSize = 1024;
    private const int EINTR = 4, EINVAL = 22;
    private const int SIGHUP = 1, SIGINT = 2, SIGQUIT = 3, SIGKILL = 9, SIGPIPE = 13, SIGTERM = 15, SIGTTIN = 21, SIGTTOU = 22;
    private const short POSIX_SPAWN_SETPGROUP = 0x02, POSIX_SPAWN_SETSIGDEF = 0x04, POSIX_SPAWN_SETSIGMASK = 0x08;
    private const short POSIX_SPAWN_SETSID_LINUX = 0x80, POSIX_SPAWN_SETSID_MACOS = 0x400;

    private const string Libc = "libc";

    // "libc" is a name no loader finds as it is: glibc's libc.so is a linker script (where there is one at all, with the
    // headers installed), so it's taken to glibc's libc.so.6 or to macOS's libSystem, and otherwise left to the runtime
    // (musl's libc.so is the library itself)
    static HexAgentGroup()
    {
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(HexAgentGroup).Assembly, (name, _, _) =>
            {
                if (name != Libc)
                    return 0;
                string actual = OperatingSystem.IsMacOS() ? "/usr/lib/libSystem.B.dylib" : "libc.so.6";
                return NativeLibrary.TryLoad(actual, out nint loaded) ? loaded : 0;
            });
        }
        catch (InvalidOperationException)
        {
            // Something else of Hex.Core's resolves its libraries already
        }
    }

    [LibraryImport(Libc, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int posix_spawnp(int* pid, string file, byte* actions, byte* attributes, nint* argv, nint* envp);

    [LibraryImport(Libc)]
    private static partial int posix_spawnattr_init(byte* attributes);

    [LibraryImport(Libc)]
    private static partial int posix_spawnattr_destroy(byte* attributes);

    [LibraryImport(Libc)]
    private static partial int posix_spawnattr_setflags(byte* attributes, short flags);

    [LibraryImport(Libc)]
    private static partial int posix_spawnattr_setpgroup(byte* attributes, int group);

    [LibraryImport(Libc)]
    private static partial int posix_spawnattr_setsigmask(byte* attributes, byte* signals);

    [LibraryImport(Libc)]
    private static partial int posix_spawnattr_setsigdefault(byte* attributes, byte* signals);

    [LibraryImport(Libc)]
    private static partial int posix_spawn_file_actions_init(byte* actions);

    [LibraryImport(Libc)]
    private static partial int posix_spawn_file_actions_destroy(byte* actions);

    [LibraryImport(Libc)]
    private static partial int posix_spawn_file_actions_adddup2(byte* actions, int from, int to);

    [LibraryImport(Libc, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int posix_spawn_file_actions_addchdir_np(byte* actions, string folder);

    [LibraryImport(Libc)]
    private static partial int sigemptyset(byte* signals);

    [LibraryImport(Libc)]
    private static partial int sigaddset(byte* signals, int signal);

    [LibraryImport(Libc, SetLastError = true)]
    private static partial int waitpid(int pid, int* status, int options);

    [LibraryImport(Libc, SetLastError = true)]
    private static partial int kill(int pid, int signal);
}
