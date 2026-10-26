using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

using Horizon.UI.Components;

namespace Hex.Core;

/// <summary>A Hex with its MCP server on, as its file in the settings folder has it.</summary>
/// <param name="Pid">Its process.</param>
/// <param name="Pipe">The name of the pipe it listens on.</param>
/// <param name="Token">What a client says first to be let in, see McpGate.</param>
/// <param name="Folders">The workspace's folders, for a client started in one of them to find the Hex that has it open.</param>
internal sealed record McpListing(int Pid, string Pipe, string Token, string[] Folders);

/// <summary>
/// How <c>Hex --mcp</c> finds the Hex it's for. Every Hex with the server on keeps a file in the settings folder's mcp
/// folder, named by its process, with its pipe, its token and the folders it has open, written again as they change and
/// taken away when it stops. The file is the user's to read, like the rest of the settings folder, which is the point of
/// the token, nobody who can't read it gets in. One left by a Hex that crashed is taken away by whoever finds it.
/// </summary>
internal static class McpDiscovery
{
    public const string FOLDER = "mcp";

    /// <summary>Where the files are.</summary>
    public static string Folder => Path.Combine(TerminalFiles.UserFolder, FOLDER);

    /// <summary>The pipe a Hex listens on, the user's name in it so two people on a machine don't meet.</summary>
    public static string PipeOf(int pid) => $"hex-mcp-{Environment.UserName.ToLowerInvariant()}-{pid}";

    /// <summary>A token nobody can guess, 32 random bytes as hex.</summary>
    public static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    private static string FileOf(int pid) => Path.Combine(Folder, $"{pid}.json");

    /// <summary>Writes a Hex's file, over what was there, a whole file at a time so a reader never gets half of it.</summary>
    public static void Write(McpListing listing)
    {
        Directory.CreateDirectory(Folder);
        var written = new JsonObject
        {
            ["pid"] = listing.Pid,
            ["pipe"] = listing.Pipe,
            ["token"] = listing.Token,
            ["folders"] = new JsonArray([.. listing.Folders.Select(folder => (JsonNode)folder)])
        };

        string path = FileOf(listing.Pid), next = path + ".new";
        File.WriteAllText(next, written.ToJsonString());
        File.Move(next, path, overwrite: true);
    }

    /// <summary>Takes a Hex's file away, when its server stops.</summary>
    public static void Remove(int pid)
    {
        try
        {
            File.Delete(FileOf(pid));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Every Hex that has the server on, the files of ones that aren't running any more taken away.</summary>
    public static List<McpListing> All()
    {
        var found = new List<McpListing>();
        if (!Directory.Exists(Folder))
            return found;

        foreach (string file in Directory.EnumerateFiles(Folder, "*.json"))
        {
            McpListing? listing = Read(file);
            if (listing is null)
                continue;

            if (Running(listing.Pid))
                found.Add(listing);
            else
                Remove(listing.Pid);
        }

        return found;
    }

    /// <summary>The file of one Hex, null while it hasn't written one (it's starting) or it's gone.</summary>
    public static McpListing? Of(int pid) => File.Exists(FileOf(pid)) ? Read(FileOf(pid)) : null;

    /// <summary>
    /// The Hex for a folder, the one with that folder (or one it's inside) open, the nearest when there's more than one.
    /// Null when none has it open.
    /// </summary>
    public static McpListing? For(string folder, IEnumerable<McpListing> listings)
    {
        string wanted = Path.GetFullPath(folder).TrimEnd('\\', '/');
        McpListing? best = null;
        int bestLength = -1;
        foreach (McpListing listing in listings)
        {
            foreach (string open in listing.Folders)
            {
                string root = Path.GetFullPath(open).TrimEnd('\\', '/');
                if ((PlatformPaths.Same(root, wanted) || PlatformPaths.IsUnder(wanted, root)) && root.Length > bestLength)
                {
                    best = listing;
                    bestLength = root.Length;
                }
            }
        }

        return best;
    }

    private static McpListing? Read(string file)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject read
                || read["pid"] is not JsonValue pid || !pid.TryGetValue(out int process)
                || read["pipe"]?.GetValue<string>() is not { } pipe || read["token"]?.GetValue<string>() is not { } token)
                return null;

            string[] folders = read["folders"] is JsonArray listed ? [.. listed.Select(folder => folder?.GetValue<string>()).OfType<string>()] : [];
            return new McpListing(process, pipe, token, folders);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Whether a process is still going.</summary>
    public static bool Running(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
