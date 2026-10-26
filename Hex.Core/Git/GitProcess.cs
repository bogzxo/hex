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

    // Asked once, in the background, the first time anybody wants to know
    private static readonly Lazy<Task<bool>> installed = new(() => Task.Run(() => Run(Environment.CurrentDirectory, ["--version"]).Ok));

    /// <summary>Whether there's a git to run at all, asked once. Waits for the answer, so not for the editor's thread, see <see cref="InstalledNow"/>.</summary>
    public static bool Installed => installed.Value.GetAwaiter().GetResult();

    /// <summary>The GitHub account every git is run as (the git_account setting), empty for whatever git works out itself.</summary>
    public static string Account
    {
        get => account;
        set => account = value?.Trim() ?? string.Empty;
    }

    private static volatile string account = string.Empty;

    /// <summary>Whether there's a git, null while that's still being asked (which this starts). Never waits.</summary>
    public static bool? InstalledNow => installed.Value.IsCompletedSuccessfully ? installed.Value.Result : null;

    /// <summary>
    /// Runs git with some arguments in a folder, what's handed in going to its input, and waits for it. Never throws, a
    /// git that isn't there or never finishes comes back as an exit of -1 with the reason.
    /// </summary>
    public static GitResult Run(string folder, IReadOnlyList<string> arguments, string? input = null, TimeSpan? patience = null, bool unattended = false)
    {
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

        try
        {
            using var git = Process.Start(start);
            if (git is null)
                return new GitResult(-1, string.Empty, "git didn't start");

            if (input is not null)
            {
                // Without a BOM, git reads the first line of a blame's contents as having three bytes of nonsense in front
                using (var writer = new StreamWriter(git.StandardInput.BaseStream, new UTF8Encoding(false)))
                    writer.Write(input);
            }

            Task<string> output = git.StandardOutput.ReadToEndAsync();
            Task<string> error = git.StandardError.ReadToEndAsync();
            if (!git.WaitForExit(patience ?? Patience))
            {
                try { git.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return new GitResult(-1, string.Empty, $"git {string.Join(' ', arguments)} took longer than {(patience ?? Patience).TotalSeconds:0} seconds");
            }

            return new GitResult(git.ExitCode, output.Result, error.Result);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new GitResult(-1, string.Empty, e.Message);
        }
    }
}
