using System.Text.Json.Nodes;

namespace Hex.Core;

internal sealed partial class HexLinkTree
{
    private const int MOST_PROVISION_ISSUES = 512;

    internal HexLinkManifest ProvisionManifest() => Manifest(verify: false, reuse: false, provision: true);

    private string? ProvisionFileProblem(string relative)
    {
        if (!Plain(relative) || Folder.Name.Length + 1 + relative.Length > HexLinkFiles.MOST_PATH)
            return "The file name cannot be used by Link.";
        if (SyncArtifact(relative) || Hidden(relative) || RepositoryUnavailable(relative))
            return null;
        string full = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            return (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0 ? "Symbolic links are left in the source checkout." : null;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (HexLinkProvision.DiskFull(e))
                throw;
            return "The source could not inspect this file. " + e.Message;
        }
    }

    private static JsonArray ProvisionIssuesJson(IReadOnlyList<HexLinkProvisionSkip> issues) =>
        new([.. issues.Take(MOST_PROVISION_ISSUES).Select(issue => (JsonNode)new JsonObject
        {
            ["path"] = issue.Path.Length <= HexLinkFiles.MOST_PATH ? issue.Path : issue.Path[..HexLinkFiles.MOST_PATH],
            ["reason"] = issue.Reason.Length <= 1024 ? issue.Reason : issue.Reason[..1024]
        })]);

    private static IReadOnlyList<HexLinkProvisionSkip>? ProvisionIssuesOf(JsonObject manifest)
    {
        if (manifest["provision_issues"] is not JsonArray { Count: <= MOST_PROVISION_ISSUES } issues)
            return null;
        return [.. issues.OfType<JsonObject>().Select(entry => new HexLinkProvisionSkip(
            HexLinkData.Need(entry, "path", HexLinkFiles.MOST_PATH + 1), HexLinkData.Need(entry, "reason", 1025), File: true))];
    }
}
