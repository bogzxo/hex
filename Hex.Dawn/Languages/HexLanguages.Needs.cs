using Hex.Core;

namespace Hex.Dawn;

// A server that won't run without a project somewhere in its folder (needs in language_servers.hor) and stops where
// there's none isn't broken. csharp-ls 0.28 throws the moment it starts on a lone .cs, it was started again, stopped
// again, and the red card saying so sat over the bottom corner of the code until somebody closed it (a press on the
// minimap's bottom landed on the card). It's said in the status bar instead, and a project made there starts it again
// the way a changed one does (reload).
internal sealed partial class HexLanguages
{
    // How far down a server's folder its project is looked for, csharp-ls looks all the way but a folder with no project
    // anywhere near its top is no project's
    private const int NEEDS_DEPTH = 6;

    /// <summary>Whether a server has what it needs in the folder it runs in, true for one that needs nothing.</summary>
    private static bool HasWhatItNeeds(HexServerEntry entry, string root)
    {
        if (entry.Needs is not { Length: > 0 } needs)
            return true;

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = NEEDS_DEPTH };
        try
        {
            return needs.Any(pattern => Directory.EnumerateFiles(root, pattern, options).Any());
        }
        catch (Exception)
        {
            // A folder that can't be looked through can't be said to have nothing, the stop is the server's own
            return true;
        }
    }

    /// <summary>A server that stopped for want of what it needs, said quietly and left until a project is made. False for any other stop.</summary>
    private bool StoppedForWant(LanguageServer server, HexServerEntry entry, string shown)
    {
        if (HasWhatItNeeds(entry, server.RootDirectory))
            return false;

        Console.WriteLine($"[Hex] {entry.Name} needs {string.Join(" or ", entry.Needs!)} in {server.RootDirectory}, it starts again once there is one.");
        context.Notices.Say(context.Strings.Said("languages.server_needs", ("server", shown)));
        return true;
    }
}
