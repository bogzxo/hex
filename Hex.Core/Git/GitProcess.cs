using System.Buffers;
using System.Diagnostics;
using System.Text;

namespace Hex.Core.Git;

/// <summary>What git said, its exit code, what it wrote out and what it wrote to its errors.</summary>
internal readonly record struct GitResult(int Exit, string Output, string Error)
{
    public bool Ok => Exit == 0;

    /// <summary>The first line of what went wrong, for a card or the status bar.</summary>
    public string Why
    {
        get
        {
            string raw = Error.Length > 0 ? Error : Output;
            ReadOnlySpan<char> trimmed = raw.AsSpan().Trim();
            if (trimmed.IsEmpty)
                return string.Empty;
            int nl = trimmed.IndexOf('\n');
            if (nl >= 0)
                return trimmed[..nl].TrimEnd().ToString();
            return trimmed.Length == raw.Length ? raw : trimmed.ToString();
        }
    }
}

/// <summary>Parses git stdout and stderr directly from pooled character buffers without allocating intermediate strings.</summary>
internal delegate T GitOutputParser<out T>(int exitCode, ReadOnlySpan<char> output, ReadOnlySpan<char> error);

/// <summary>
/// git itself, run in a repository and waited for. Everything Hex knows about a repository comes from the git on the
/// PATH, the same one the terminal has, so it reads the config, the hooks and the credentials the user set up and never
/// disagrees with what they type. Nothing here is quick enough for the UI's thread, it's all for the background.
/// </summary>
internal static class GitProcess
{
    // Long enough for a status of a big repository on a cold disk, short enough that a git stuck asking for something
    // doesn't hold a background thread for ever
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly string[] VersionArguments = ["--version"];

    private static readonly Lock integrationGate = new();
    private static volatile bool enabled = true;
    private static Task<bool>? installed;

    internal static bool Enabled => enabled;

    internal static void SetEnabled(bool value)
    {
        lock (integrationGate)
        {
            if (enabled == value)
                return;
            enabled = value;
            installed = null;
        }
    }

    private static Task<bool>? Probe()
    {
        lock (integrationGate)
            return enabled ? installed ??= Task.Run(() => Run(Environment.CurrentDirectory, VersionArguments).Ok) : null;
    }

    /// <summary>Whether Git is enabled and installed. Waits for the probe, so use InstalledNow on the UI thread.</summary>
    public static bool Installed => Probe()?.GetAwaiter().GetResult() == true && enabled;

    /// <summary>The GitHub account every git is run as (the git_account setting), empty for whatever git works out itself.</summary>
    public static string Account
    {
        get => account;
        set => account = value?.Trim() ?? string.Empty;
    }

    private static volatile string account = string.Empty;

    /// <summary>Whether there's a git, null while that's still being asked (which this starts). Never waits.</summary>
    public static bool? InstalledNow => Probe() is { } probe ? probe.IsCompletedSuccessfully ? probe.Result : null : false;

    internal static bool ProbeStarted
    {
        get { lock (integrationGate) return installed is not null; }
    }

    internal const string DisabledReason = "Git support is turned off in Hex.";

    /// <summary>
    /// Runs git with some arguments in a folder, what's handed in going to its input, and waits for it. Never throws, a
    /// git that isn't there or never finishes comes back as an exit of -1 with the reason.
    /// </summary>
    public static GitResult Run(string folder, IReadOnlyList<string> arguments, string? input = null, TimeSpan? patience = null, bool unattended = false,
        IReadOnlyDictionary<string, string>? environment = null, CancellationToken cancel = default, Stream? standardOutput = null)
    {
        return RunCore(
            folder,
            arguments,
            static (exit, output, error) => new GitResult(
                exit,
                output.IsEmpty ? string.Empty : output.ToString(),
                error.IsEmpty ? string.Empty : error.ToString()),
            input,
            patience,
            unattended,
            environment,
            cancel,
            standardOutput);
    }

    /// <summary>
    /// Runs git and hands the pooled stdout and stderr character spans straight to <paramref name="parse"/>, avoiding
    /// giant intermediate string allocations for commands whose output is parsed into structured records.
    /// </summary>
    internal static T Run<T>(string folder, IReadOnlyList<string> arguments, GitOutputParser<T> parse, string? input = null,
        TimeSpan? patience = null, bool unattended = false, IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancel = default)
    {
        return RunCore(folder, arguments, parse, input, patience, unattended, environment, cancel, standardOutput: null);
    }

    /// <summary>Formats the first non-empty line of stderr (or stdout) from pooled spans without intermediate splits.</summary>
    internal static string FormatWhy(ReadOnlySpan<char> output, ReadOnlySpan<char> error)
    {
        ReadOnlySpan<char> trimmed = (error.Length > 0 ? error : output).Trim();
        if (trimmed.IsEmpty)
            return string.Empty;
        int nl = trimmed.IndexOf('\n');
        return nl >= 0 ? trimmed[..nl].TrimEnd().ToString() : trimmed.ToString();
    }

    /// <summary>Formats the full trimmed error message from stderr (or stdout) without double-trimming.</summary>
    internal static string FormatSaid(ReadOnlySpan<char> output, ReadOnlySpan<char> error)
    {
        ReadOnlySpan<char> err = error.Trim();
        return err.Length > 0 ? err.ToString() : output.Trim().ToString();
    }

    private static T RunCore<T>(string folder, IReadOnlyList<string> arguments, GitOutputParser<T> parse, string? input,
        TimeSpan? patience, bool unattended, IReadOnlyDictionary<string, string>? environment, CancellationToken cancel,
        Stream? standardOutput)
    {
        cancel.ThrowIfCancellationRequested();
        if (!enabled)
            return parse(-1, ReadOnlySpan<char>.Empty, DisabledReason.AsSpan());

        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom
        };

        // Paths as they are rather than octal escapes, and English, so what's said can be read the same everywhere
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("core.quotepath=false");

        // The GitHub account to sign in as, so a credential helper holding more than one (Git Credential Manager with a
        // work and a personal account) uses it rather than asking which every time. A username in the remote's URL or a
        // repository's own credential.username is more particular and still wins
        if (Account is { Length: > 0 } currentAccount)
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add($"credential.https://github.com.username={currentAccount}");
        }

        // Nobody asked for this one (a fetch in the background), so nothing pops up to ask who to sign in as. A helper
        // that can't sign in on its own gives up and the fetch fails quietly until somebody fetches by hand
        if (unattended)
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("credential.interactive=false");
            start.Environment["GCM_INTERACTIVE"] = "never";
        }

        for (int i = 0; i < arguments.Count; i++)
            start.ArgumentList.Add(arguments[i]);

        // A status every few seconds mustn't take the index lock out from under a commit typed in the terminal, and git
        // mustn't stop to ask for a password or launch an editor on a terminal nobody can see
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_EDITOR"] = "false";
        start.Environment["GIT_SEQUENCE_EDITOR"] = "false";
        start.Environment["LC_ALL"] = "C";
        // What the caller sets on top, an index of its own for a checkpoint that leaves the real one alone
        if (environment is not null)
        {
            foreach (var (name, value) in environment)
                start.Environment[name] = value;
        }

        try
        {
            using var git = StartEnabled(start);
            if (git is null)
                return parse(-1, ReadOnlySpan<char>.Empty, enabled ? "git didn't start".AsSpan() : DisabledReason.AsSpan());

            using var cancellation = cancel.UnsafeRegister(static state => Kill((Process)state!), git);
            // Closed here rather than left to the process's own Dispose, which leaves its pipes to the finalizer. An idle
            // Hex makes so little garbage that the collector hardly comes round, and a status every few seconds had
            // hundreds of pipe handles open within minutes
            using StreamReader outputReader = git.StandardOutput, errorReader = git.StandardError;
            Task<PooledCharBuffer> output = standardOutput is null
                ? ReadPooledCharsAsync(outputReader.BaseStream)
                : CopyOutputAsync(outputReader.BaseStream, standardOutput);
            Task<PooledCharBuffer> error = ReadPooledCharsAsync(errorReader.BaseStream);
            // Git can emit a result while it is still consuming stdin, as check-ignore --stdin does.
            Task writing = input is not null ? WriteInputAsync(git.StandardInput.BaseStream, input) : Task.CompletedTask;
            if (!git.WaitForExit(patience ?? Patience))
            {
                Kill(git);
                cancel.ThrowIfCancellationRequested();

                // The reads end once git has gone, before their pipes are closed under them
                try { Task.WaitAll([output, error, writing], TimeSpan.FromSeconds(1)); } catch (AggregateException) { }
                DisposeIfCompleted(output);
                DisposeIfCompleted(error);
                string timeout = $"git {string.Join(' ', arguments)} took longer than {(patience ?? Patience).TotalSeconds:0} seconds";
                return parse(-1, ReadOnlySpan<char>.Empty, timeout.AsSpan());
            }

            cancel.ThrowIfCancellationRequested();
            writing.GetAwaiter().GetResult();
            using PooledCharBuffer outBuf = output.GetAwaiter().GetResult();
            using PooledCharBuffer errBuf = error.GetAwaiter().GetResult();
            return parse(git.ExitCode, outBuf.Span, errBuf.Span);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            cancel.ThrowIfCancellationRequested();
            return parse(-1, ReadOnlySpan<char>.Empty, e.Message.AsSpan());
        }
    }

    internal static Process? StartEnabled(ProcessStartInfo start)
    {
        lock (integrationGate)
            return enabled ? Process.Start(start) : null;
    }

    private static void DisposeIfCompleted(Task<PooledCharBuffer> task)
    {
        if (task.IsCompletedSuccessfully)
            task.Result.Dispose();
    }

    private static async Task<PooledCharBuffer> CopyOutputAsync(Stream from, Stream to)
    {
        await from.CopyToAsync(to).ConfigureAwait(false);
        return default;
    }

    private static async Task<PooledCharBuffer> ReadPooledCharsAsync(Stream stream)
    {
        const int ByteChunkSize = 8192;
        byte[] byteBuf = ArrayPool<byte>.Shared.Rent(ByteChunkSize);
        char[]? charBuf = null;
        int charLen = 0;

        try
        {
            int read = await stream.ReadAsync(byteBuf.AsMemory(0, ByteChunkSize)).ConfigureAwait(false);
            if (read == 0)
                return default;

            Decoder decoder = Utf8NoBom.GetDecoder();
            // Skip a leading UTF-8 BOM if present, matching StreamReader(UTF8) behaviour
            int offset = read >= 3 && byteBuf[0] == 0xEF && byteBuf[1] == 0xBB && byteBuf[2] == 0xBF ? 3 : 0;
            int count = read - offset;

            charBuf = ArrayPool<char>.Shared.Rent(Math.Max(4096, count));
            while (true)
            {
                if (count > 0)
                {
                    int needed = decoder.GetCharCount(byteBuf, offset, count, flush: false);
                    EnsureCapacity(ref charBuf, charLen + needed);
                    charLen += decoder.GetChars(byteBuf, offset, count, charBuf, charLen, flush: false);
                }

                read = await stream.ReadAsync(byteBuf.AsMemory(0, ByteChunkSize)).ConfigureAwait(false);
                if (read == 0)
                    break;

                offset = 0;
                count = read;
            }

            // Flush any trailing incomplete UTF-8 byte sequence
            int tail = decoder.GetCharCount(byteBuf, 0, 0, flush: true);
            if (tail > 0)
            {
                EnsureCapacity(ref charBuf, charLen + tail);
                charLen += decoder.GetChars(byteBuf, 0, 0, charBuf, charLen, flush: true);
            }

            char[] result = charBuf;
            charBuf = null;
            return new PooledCharBuffer(result, charLen);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(byteBuf);
            if (charBuf is not null)
                ArrayPool<char>.Shared.Return(charBuf);
        }
    }

    private static void EnsureCapacity(ref char[] buffer, int required)
    {
        if (required <= buffer.Length)
            return;

        int nextSize = Math.Max(required, buffer.Length * 2);
        char[] bigger = ArrayPool<char>.Shared.Rent(nextSize);
        buffer.AsSpan().CopyTo(bigger);
        ArrayPool<char>.Shared.Return(buffer);
        buffer = bigger;
    }

    private static async Task WriteInputAsync(Stream input, string content)
    {
        // Signal EOF without a byte-order mark or a continuation queued on the waiting editor thread.
        await using (input.ConfigureAwait(false))
        {
            if (content.Length == 0)
                return;

            const int MaxCharsPerChunk = 2048;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(Utf8NoBom.GetMaxByteCount(MaxCharsPerChunk + 1));
            try
            {
                Encoder encoder = Utf8NoBom.GetEncoder();
                int offset = 0;
                while (offset < content.Length)
                {
                    int chunk = Math.Min(MaxCharsPerChunk, content.Length - offset);
                    bool flush = offset + chunk == content.Length;
                    int written = encoder.GetBytes(content.AsSpan(offset, chunk), buffer, flush);
                    if (written > 0)
                        await input.WriteAsync(buffer.AsMemory(0, written)).ConfigureAwait(false);
                    offset += chunk;
                }
                await input.FlushAsync().ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private static void Kill(Process git)
    {
        try { git.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private readonly struct PooledCharBuffer(char[]? buffer, int length) : IDisposable
    {
        public ReadOnlySpan<char> Span => buffer is null ? ReadOnlySpan<char>.Empty : buffer.AsSpan(0, length);

        public void Dispose()
        {
            if (buffer is not null)
                ArrayPool<char>.Shared.Return(buffer);
        }
    }
}
