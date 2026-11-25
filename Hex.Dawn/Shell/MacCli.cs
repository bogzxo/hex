using System.Diagnostics;

namespace Hex.Dawn;

/// <summary>
/// The <c>hex</c> command of a Mac: a link on the PATH to the program, so <c>hex file.cs</c> or <c>hex .</c> in a terminal
/// opens it in the running Hex (the single instance pipe takes the files). In a folder of the person's own when their PATH has
/// one (<c>~/.local/bin</c>), otherwise <c>/usr/local/bin</c>, which wants an administrator's word, asked for by the system.
/// </summary>
internal static class MacCli
{
    internal const string NAME = "hex";

    /// <summary>Where the link goes and whether making it wants an administrator.</summary>
    internal readonly record struct Place(string Folder, string Link, bool NeedsAdministrator);

    /// <summary>
    /// The place for the link: <c>~/.local/bin</c> when it is on the PATH (the folder is made if needed, nobody asked for
    /// sudo), <c>/usr/local/bin</c> otherwise. Pure, the PATH and the home folder are given.
    /// </summary>
    internal static Place Choose(string home, string? path)
    {
        string own = Join(home, ".local", "bin");
        foreach (string part in (path ?? string.Empty).Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            string folder = part.StartsWith('~') ? home + part[1..] : part;
            if (string.Equals(folder.Length > 1 ? folder.TrimEnd('/') : folder, own, StringComparison.Ordinal))
                return new Place(own, Join(own, NAME), false);
        }

        return new Place("/usr/local/bin", "/usr/local/bin/" + NAME, true);
    }

    /// <summary>What the link points at: the app's Contents/MacOS/Hex inside a bundle, the running program otherwise.</summary>
    internal static string? Target(string? bundleRoot, string? processPath) =>
        bundleRoot is not null ? Join(bundleRoot, "Contents", "MacOS", "Hex") : processPath;

    /// <summary>Joins macOS path parts with '/' whatever the host OS is (Path.Combine would use '\' on Windows).</summary>
    private static string Join(string first, params string[] rest)
    {
        string result = first;
        foreach (string part in rest)
            result = result.EndsWith('/') ? result + part : result + "/" + part;
        return result;
    }

    /// <summary>The shell line that makes the link as an administrator, with what's quoted for the shell and then for AppleScript.</summary>
    internal static string AdministratorScript(string target, string link, string folder)
    {
        string line = $"mkdir -p {ShellQuote(folder)} && ln -sf {ShellQuote(target)} {ShellQuote(link)}";
        return $"do shell script \"{line.Replace("\\", "\\\\").Replace("\"", "\\\"")}\" with administrator privileges";
    }

    internal static string ShellQuote(string text) => "'" + text.Replace("'", "'\\''") + "'";

    /// <summary>
    /// Makes the link in a folder of the person's own. Replaces a link there (it may point at an older Hex), never a file that
    /// isn't one. Returns null when done, otherwise what's wrong.
    /// </summary>
    internal static string? Link(string target, string link)
    {
        try
        {
            string? folder = Path.GetDirectoryName(link);
            if (folder is not null)
                Directory.CreateDirectory(folder);

            FileSystemInfo existing = new FileInfo(link);
            if (existing.Exists || existing.LinkTarget is not null || Directory.Exists(link))
            {
                if (existing.LinkTarget is null)
                    return "exists";
                File.Delete(link);
            }

            File.CreateSymbolicLink(link, target);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }
    }

    /// <summary>Asks the system for an administrator's word and makes the link in /usr/local/bin. Null when done, "cancelled" when they said no, else why not.</summary>
    internal static string? LinkAsAdministrator(string target, Place place)
    {
        try
        {
            var start = new ProcessStartInfo("/usr/bin/osascript") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
            start.ArgumentList.Add("-e");
            start.ArgumentList.Add(AdministratorScript(target, place.Link, place.Folder));
            using Process? process = Process.Start(start);
            if (process is null)
                return "osascript didn't start";

            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode == 0)
                return null;

            // -128 is "User canceled."
            return error.Contains("-128", StringComparison.Ordinal) ? "cancelled" : error.Trim();
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception)
        {
            return e.Message;
        }
    }
}
