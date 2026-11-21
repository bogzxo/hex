using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    /// <summary>
    /// What a link needs of the editor (IHexLinkServices), over this scene. The links call it from their own threads:
    /// what's read there is a value the editor swaps in whole (the workspace, the fleet's extras, the hub's snapshot),
    /// and what has to touch the editor (a chat read, a question answered, a file written into an open tab) is posted to
    /// the editor's thread and handed back when it's run.
    /// </summary>
    private sealed class LinkServices(DawnScene hex) : IHexLinkServices
    {
        private volatile HexLinkWorkspace workspace = HexLinkWorkspace.Empty;
        private volatile IReadOnlyDictionary<int, HexLinkChatExtra> extras = new Dictionary<int, HexLinkChatExtra>();

        public HexLinkDevice Device => hex.linkIdentity?.Device ?? new HexLinkDevice(string.Empty, hex.LinkNameWanted);

        public HexLinkWorkspace Workspace
        {
            get => workspace;
            set => workspace = value;
        }

        public HexHubSnapshot Fleet => hex.hub.Snapshot;

        public IReadOnlyDictionary<int, HexLinkChatExtra> FleetExtras
        {
            get => extras;
            set => extras = value;
        }

        public Task<JsonObject?> Chat(int id, Func<HexAgentChat, JsonObject?> read) =>
            hex.OnEditorForLink(() => hex.ChatById(id) is { } chat ? read(chat) : null);

        // The tools as a client that isn't a chat's sees them; HexLinkAllowed keeps to the few a device may call
        public IMcpTools Tools => hex.McpTools;

        // As the question card's button does it
        public Task<bool> Answer(int chat, string answer) => hex.OnEditorForLink(() =>
            hex.ChatById(chat) is { } asked && asked.FirstPending(HexAgentEntryKind.Question) is { } question && asked.Reply(question, answer));

        // As hex_write does it: an open tab with changes nobody saved yet is changed in the editor, as one step to undo;
        // otherwise the disk, beside it and moved over it, which a tab of it takes in as it would any change on the disk
        public async Task WriteFile(string fullPath, byte[] content)
        {
            bool inEditor = await hex.OnEditorForLink(() =>
            {
                if (hex.Context.Documents.Find(fullPath) is not { Modified: true } open || LinkText(content) is not { } text)
                    return false;
                hex.Context.Documents.Rewrite(open, text.Replace("\r\n", "\n"));
                return true;
            });
            if (!inEditor)
                await Task.Run(() => HexLinkFiles.WriteAtomic(fullPath, content));
        }

        public Task DeleteFile(string fullPath) => Task.Run(() => File.Delete(fullPath));

        public void Presence(HexLinkDevice device, HexLinkPresence? presence) => hex.PostLink(() =>
        {
            hex.linkPresence[device.Id] = new LinkPresenceSeen(device, presence, DateTime.UtcNow);
            hex.linkDirty = true;
        });

        // Kept as a file for now, said in a notice, until the Link window has a card to apply it from
        public Task<string> PatchOffered(HexLinkDevice device, string title, string diff)
        {
            string folder = Path.Combine(hex.linkData, "patches");
            string safe = new([.. device.Name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-')]);
            string file = Path.Combine(folder, $"{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{(safe.Length > 0 ? safe : "device")}.diff");
            Directory.CreateDirectory(folder);
            File.WriteAllText(file, diff, new UTF8Encoding(false));
            hex.PostLink(() => hex.Say(Said("link.patch_kept", ("device", device.Name), ("title", title), ("file", file))));
            return Task.FromResult("kept");
        }

        public void Audit(string what, HexLinkDevice who, string detail) => hex.linkAudit?.Write(what, who.Id, detail);
    }

    // Bytes as text when they're UTF-8 (a byte order mark let go of), null when they aren't, which goes to the disk as they are
    private static string? LinkText(byte[] content)
    {
        try
        {
            int skip = content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF ? 3 : 0;
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(content, skip, content.Length - skip);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }
}
