using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Hex.Core;

internal interface IHexLinkGitFollowing
{
    string GitFollowData { get; }
    bool GitFollows(HexLinkFolder folder, HexLinkDevice peer);
}

internal sealed class HexLinkGitFollowState(string data, HexLinkFolder folder, string device)
{
    private string FileAt(string path)
    {
        string identity = Path.GetFullPath(folder.Path) + "\n" + folder.Name + "\n" + path
            + "\n" + folder.FolderAuthorityId + "\n" + folder.FolderAuthorityEpoch + "\n" + device;
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(data, "git-follow", key + ".json");
    }

    internal HexLinkHeadTip? Agreed(string path)
    {
        try { return HexLinkHeadTip.Of(JsonNode.Parse(File.ReadAllText(FileAt(path))) as JsonObject); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (System.Text.Json.JsonException error) { throw new IOException("The agreed Git tip record is damaged.", error); }
    }

    internal void Agree(string path, HexLinkHeadTip tip)
    {
        string file = FileAt(path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        HexLinkFiles.WriteAtomic(file, Encoding.UTF8.GetBytes(tip.ToJson().ToJsonString()));
    }
}
