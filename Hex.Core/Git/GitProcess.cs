using System.Diagnostics;
using System.Text;

namespace Hex.Core.Git;

/// <summary>What git said, its exit code, what it wrote out and what it wrote to its errors.</summary>
internal readonly record struct GitResult(int Exit, string Output, string Error)
{
    public bool Ok => Exit == 0;

    /// <summary>The first line of what went wrong, for a card or the status bar.</summary>
    public string Why => (Error.Length > 0 ? Error : Output).Trim().Split('\n')[0].Trim();
}

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
            return enabled ? installed ??= Task.Run(() => Run(Environment.CurrentDirectory, ["--version"]).Ok) : null;
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
    private static GitResult Disabled => new(-1, string.Empty, DisabledReason);

    /// <summary>
    /// Runs git with some arguments in a folder, what's handed in going to its input, and waits for it. Never throws, a
    /// git that isn't there or never finishes comes back as an exit of -1 with the reason.
    /// </summary>
    public static GitResult Run(string folder, IReadOnlyList<string> arguments, string? input = null, TimeSpan? patience = null, bool unattended = false,
        IReadOnlyDictionary<string, string>? environment = null, CancellationToken cancel = default, Stream? standardOutput = null)
    {
        cancel.ThrowIfCancellationRequested();
        if (!enabled)
            return Disabled;
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        // Paths as they are rather than octal escapes, and English, so what's said can be read the same everywhere
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("core.quotepath=false");

        // The GitHub account to sign in as, so a credential helper holding more than one (Git Credential Manager with a
        // work and a personal account) uses it rather than asking which every time. A username in the remote's URL or a
        // repository's own credential.username is more particular and still wins
        if (Account is { Length: > 0 } account)
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add($"credential.https://github.com.username={account}");
        }

        // Nobody asked for this one (a fetch in the background), so nothing pops up to ask who to sign in as. A helper
        // that can't sign in on its own gives up and the fetch fails quietly until somebody fetches by hand
        if (unattended)
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("credential.interactive=false");
            start.Environment["GCM_INTERACTIVE"] = "never";
        }

        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);

        // A status every few seconds mustn't take the index lock out from under a commit typed in the terminal, and git
        // mustn't stop to ask for a password on a terminal nobody can see
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
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
                return enabled ? new GitResult(-1, string.Empty, "git didn't start") : Disabled;

            using var cancellation = cancel.UnsafeRegister(static state => Kill((Process)state!), git);
            // Closed here rather than left to the process's own Dispose, which leaves its pipes to the finalizer. An idle
            // Hex makes so little garbage that the collector hardly comes round, and a status every few seconds had
            // hundreds of pipe handles open within minutes
            using StreamReader outputReader = git.StandardOutput, errorReader = git.StandardError;
            Task<string> output = standardOutput is null ? outputReader.ReadToEndAsync() : CopyOutput(outputReader.BaseStream, standardOutput);
            Task<string> error = errorReader.ReadToEndAsync();
            // Git can emit a result while it is still consuming stdin, as check-ignore --stdin does.
            Task writing = input is not null ? WriteInput(git.StandardInput.BaseStream, input) : Task.CompletedTask;
            if (!git.WaitForExit(patience ?? Patience))
            {
                Kill(git);
                cancel.ThrowIfCancellationRequested();

                // The reads end once git has gone, before their pipes are closed under them
                try { Task.WaitAll([output, error, writing], TimeSpan.FromSeconds(1)); } catch (AggregateException) { }
                return new GitResult(-1, string.Empty, $"git {string.Join(' ', arguments)} took longer than {(patience ?? Patience).TotalSeconds:0} seconds");
            }

            cancel.ThrowIfCancellationRequested();
            writing.GetAwaiter().GetResult();
            return new GitResult(git.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            cancel.ThrowIfCancellationRequested();
            return new GitResult(-1, string.Empty, e.Message);
        }
    }

    internal static Process? StartEnabled(ProcessStartInfo start)
    {
        lock (integrationGate)
            return enabled ? Process.Start(start) : null;
    }

    private static async Task<string> CopyOutput(Stream from, Stream to)
    {
        await from.CopyToAsync(to).ConfigureAwait(false);
        return string.Empty;
    }

    private static async Task WriteInput(Stream input, string content)
    {
        // Signal EOF without a byte-order mark or a continuation queued on the waiting editor thread.
        var writer = new StreamWriter(input, new UTF8Encoding(false));
        await using (writer.ConfigureAwait(false))
            await writer.WriteAsync(content).ConfigureAwait(false);
    }

    private static void Kill(Process git)
    {
        try { git.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}
