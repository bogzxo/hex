using System.Globalization;

namespace Hex.Dawn;

/// <summary>A file Hex was started on, where the caret goes in it, 1 for the first line and column, 0 for wherever.</summary>
internal readonly record struct HexLaunchFile(string Path, int Line, int Column);

/// <summary>
/// What a command line names besides its options, the way code and every other editor takes them. Any argument that is
/// neither an option nor an option's value is a file or a folder, from wherever Hex was started, file:12 and file:12:3
/// put the caret there (a drive's colon is no line), and --goto (or -g) is one more of them, the way code -g has it.
/// </summary>
internal static class HexLaunch
{
    // The options that take the one argument after them
    private static readonly string[] withValue = ["--open", "--workspace", "--size", "--scale", "--view", "--line", "--folder", "--goto", "-g", "--cwd"];

    // The options that take every argument after them up to the next option
    private static readonly string[] withValues = ["--check", "--selftest", "--console"];

    /// <summary>
    /// The files a command line names, in its order, and the first folder named on its own. --open and --goto are among
    /// the files where they are, --line is the line of --open.
    /// </summary>
    public static (List<HexLaunchFile> Files, string? Folder) Parse(string[] args, string current)
    {
        var files = new List<HexLaunchFile>();
        string? folder = null;
        int lineAsked = int.TryParse(ValueAfter(args, "--line"), NumberStyles.None, CultureInfo.InvariantCulture, out int line) ? line : 0;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg is "--open" or "--goto" or "-g")
            {
                if (i + 1 >= args.Length)
                    continue;

                i++;
                HexLaunchFile named = arg == "--open" ? new HexLaunchFile(FullPath(args[i], current), lineAsked, 0) : ParseFile(args[i], current);
                files.Add(named);
                continue;
            }

            if (withValue.Contains(arg))
            {
                i++;
                continue;
            }

            if (withValues.Contains(arg))
            {
                while (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    i++;
                continue;
            }

            if (arg.StartsWith('-') || arg.Length == 0)
                continue;

            string full = FullPath(arg, current);
            if (Directory.Exists(full))
                folder ??= full;
            else
                files.Add(ParseFile(arg, current));
        }

        return (files, folder);
    }

    /// <summary>
    /// A file and where in it, a.cs, a.cs:12 or C:\a\b.cs:12:3, made whole from the folder Hex was started in. A file
    /// that is there by the whole of what was said is that file, colons and all.
    /// </summary>
    public static HexLaunchFile ParseFile(string text, string current)
    {
        string path = text.Trim('"');
        if (File.Exists(FullPath(path, current)))
            return new HexLaunchFile(FullPath(path, current), 0, 0);

        int line = 0, column = 0;
        if (TakeNumber(ref path, out int last))
        {
            if (TakeNumber(ref path, out int first))
            {
                line = first;
                column = last;
            }
            else
            {
                line = last;
            }
        }

        return new HexLaunchFile(FullPath(path, current), line, column);
    }

    // Helper method to take a :number off the end, never the colon of a drive (C:5 is a file called 5 on C)
    private static bool TakeNumber(ref string path, out int number)
    {
        number = 0;
        int colon = path.LastIndexOf(':');
        if (colon < 2 || colon == path.Length - 1)
            return false;

        if (!int.TryParse(path.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out number))
            return false;

        path = path[..colon];
        return true;
    }

    // Helper method to make a path whole from a folder, one that's whole already left as it is
    private static string FullPath(string path, string current)
    {
        try
        {
            return Path.GetFullPath(Path.Combine(current, path.Trim('"')));
        }
        catch (Exception)
        {
            return path;
        }
    }

    private static string? ValueAfter(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
