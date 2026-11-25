using System.Globalization;
using System.Numerics;
using System.Text;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// Link Chat for working on code together (docs/hex-link-plan.md, 5.3, "Link Chat for code"): a selection shared as a code
// card, a "look here" ping, file:line references in any message made links, @device mentions, and the chat kept over a
// restart. The wire is HexLinkTalk's optional "ref" field, HexLinkTalkRef.
internal sealed partial class DawnScene
{
    private const string LINK_TALK_HISTORY = "talk-history.json";

    private readonly object linkTalkSaveGate = new();

    /* Keeping the chat */

    // The last messages of the chat read back from the Link data folder when Link starts
    private void LinkTalkRestore()
    {
        linkTalkMessages.Clear();
        if (linkData is { Length: > 0 } folder)
            linkTalkMessages.AddRange(HexLinkTalkHistory.Load(Path.Combine(folder, LINK_TALK_HISTORY)));
        if (linkTalkRows is not { } rows)
            return;
        linkTalkEmpty!.Visible = linkTalkMessages.Count == 0;
        rows.ItemCount = linkTalkMessages.Count;
        rows.ItemsChanged();
        rows.ScrollToEnd();
    }

    // The chat written out in the background, a message at a time (the file is small, the last 500)
    private void LinkTalkSave()
    {
        // A burst of messages is one write: the first starts a short wait and what's there when it ends is written once
        if (linkData is not { Length: > 0 } || linkTalkSaveQueued)
            return;
        linkTalkSaveQueued = true;
        _ = Task.Delay(LINK_TALK_SAVE_DELAY_MS).ContinueWith(_ => PostLink(() => LinkTalkFlush(background: true)), TaskScheduler.Default);
    }

    private const int LINK_TALK_SAVE_DELAY_MS = 400;
    private bool linkTalkSaveQueued;

    /// <summary>Writes the chat now if a write is waiting (on the editor's thread); background writes on a worker, the rest (Link stopping, exit) before returning.</summary>
    private void LinkTalkFlush(bool background = false)
    {
        if (!linkTalkSaveQueued)
            return;
        linkTalkSaveQueued = false;
        if (linkData is not { Length: > 0 } folder)
            return;
        int skip = Math.Max(0, linkTalkMessages.Count - HexLinkTalkHistory.MOST);
        HexLinkTalk[] snapshot = linkTalkMessages.GetRange(skip, linkTalkMessages.Count - skip).ToArray();
        string path = Path.Combine(folder, LINK_TALK_HISTORY);
        void Write()
        {
            lock (linkTalkSaveGate)
            {
                try
                {
                    HexLinkTalkHistory.Save(path, snapshot);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        if (background)
            _ = Task.Run(Write);
        else
            Write();
    }

    /* What a message of the chat tells this device */

    // Called with each message heard: a look-here for this device and a mention of it are told on a card, chat open or not
    private void LinkTalkNotice(HexLinkTalk message)
    {
        if (LinkDevice is not { } me || message.Sender.Id == me.Id)
            return;
        string who = LinkNameOf(message.Sender.Id, message.Sender.Name);
        if (message.Ref is { Kind: HexLinkTalkRef.LOOK } look && look.To == me.Id)
        {
            UINotification? card = null;
            string where = $"{look.Path}:{look.Lines}";
            card = toasts.Show(Said("link.talk.look_notice", ("device", who), ("where", where)), "eye", null, 0.0f,
                (Said("link.talk.look_go"), () =>
                {
                    card?.Close();
                    LinkOpenReferenceSaying(message.Sender.Id, look.Path, look.Line, look.ToLine);
                }),
                (Said("link.talk.look_dismiss"), () => card?.Close()));
            return;
        }

        // Asked to finish with a file: the question on a card, whose Done lets it go
        if (message.Ref is { Kind: HexLinkTalkRef.RELEASE } ask && string.Equals(ask.To, me.Id, StringComparison.OrdinalIgnoreCase))
        {
            UINotification? asked = null;
            asked = toasts.Show(ask.Fallback(), "comment-discussion", null, 0.0f,
                (Said("link.mark_ask_done"), () =>
                {
                    asked?.Close();
                    if (LinkLocalOf(ask.Path) is { } local)
                        LinkReleaseOwn(local);
                }));
            return;
        }

        if (message.Model is null && HexLinkTalkText.Mentions(message.Text, [me.Name]).Count > 0 && linkTalkPanel?.IsShownInHierarchy != true)
        {
            string text = message.Text.Length > 160 ? message.Text[..160] + "…" : message.Text;
            toasts.Show(Said("link.talk.mention_notice", ("device", who), ("text", text)), "comment-discussion", null, 8.0f,
                (Said("link.talk.mention_open"), ToggleLinkTalk));
        }
    }

    /* Sharing */

    // The selection here (or its line) as lines of the file, 1-based
    private bool LinkTalkSelectionHere(out string wire, out int line, out int toLine, out string snippet)
    {
        wire = string.Empty;
        line = toLine = 1;
        snippet = string.Empty;
        if (document is not { IsText: true, Path: { } path, DiffOf: null } || LinkWireOf(path) is not { } where)
            return false;
        var text = document.Editor.Document;
        UITextPosition from = text.HasSelection ? text.SelectionStart : text.Caret, to = text.HasSelection ? text.SelectionEnd : text.Caret;
        // A selection ending at the start of a line doesn't take that line
        int last = to.Line > from.Line && to.Column == 0 ? to.Line - 1 : to.Line;
        wire = where;
        line = from.Line + 1;
        toLine = last + 1;
        var lines = new StringBuilder();
        for (int at = from.Line; at <= last && at < from.Line + HexLinkTalkRef.MOST_LINES + 1; at++)
            lines.Append(text.LineAt(at)).Append('\n');
        snippet = HexLinkTalkRef.CapSnippet(lines.ToString());
        return true;
    }

    /// <summary>Shares the selection (or the caret's line) in Link Chat as a code card, asking for a note first.</summary>
    internal void LinkShareSelection()
    {
        if (link is null || LinkPeers.Count == 0)
        {
            Say(Said("link.talk.no_devices"), error: true);
            return;
        }

        if (!LinkTalkSelectionHere(out string wire, out int line, out int toLine, out string snippet))
        {
            Say(Said("link.talk.share_nowhere"), error: true);
            return;
        }

        string where = $"{wire}:{(toLine > line ? $"{line}-{toLine}" : line.ToString(CultureInfo.InvariantCulture))}";
        AskText(Said("link.talk.share_title"), Said("link.talk.share_text", ("where", where)), string.Empty, Said("link.talk.share_button"),
            note => LinkTalkSend(new HexLinkTalkRef(HexLinkTalkRef.CODE, wire, line, toLine, snippet, note.Trim() is { Length: > 0 and <= HexLinkTalkRef.MOST_NOTE } trimmed && HexLinkTalk.Valid(trimmed) ? trimmed : null)),
            box => box.Placeholder = Said("link.talk.share_note"));
    }

    /// <summary>Asks a device to look at what the caret (or the selection) is on, a ping in the chat that its notice offers to go to.</summary>
    internal void LinkLookHere(HexLinkPeer peer)
    {
        if (!LinkTalkSelectionHere(out string wire, out int line, out int toLine, out _))
        {
            Say(Said("link.talk.share_nowhere"), error: true);
            return;
        }

        var ping = new HexLinkTalkRef(HexLinkTalkRef.LOOK, wire, line, toLine, To: peer.Device.Id);
        if (LinkTalkSend(ping))
            Say(Said("link.talk.look_sent", ("device", peer.Device.Name), ("where", $"{wire}:{ping.Lines}")));
    }

    // A reference said in the chat, what stopped it said otherwise; true when it went
    private bool LinkTalkSend(HexLinkTalkRef reference)
    {
        if (link is not { } current)
            return false;
        HexLinkTalkResult result = current.SayTalk(reference);
        switch (result)
        {
            case HexLinkTalkResult.Sent:
                if (reference.Kind == HexLinkTalkRef.CODE && linkTalkPanel?.IsShownInHierarchy != true)
                    ToggleLinkTalk();
                return true;
            case HexLinkTalkResult.Invalid:
                Say(Said("link.talk.invalid", ("count", HexLinkTalk.MOST_TEXT.ToString(CultureInfo.InvariantCulture))), error: true);
                break;
            case HexLinkTalkResult.TooFast:
                Say(Said("link.talk.slow"), error: true);
                break;
            case HexLinkTalkResult.NoView:
                Say(Said("link.talk.no_view"), error: true);
                break;
            default:
                Say(Said("link.talk.full"), error: true);
                break;
        }

        return false;
    }

    // The editor's right click menu and the palette's share commands
    private void AddLinkTalkMenu(Menu menu)
    {
        var share = menu.Add(Said("link.talk.share_menu"), LinkShareSelection, "Ctrl+K L", "comment-discussion");
        share.IsVisible = () => LinkPeers.Count > 0 && document.IsText;
        foreach (HexLinkPeer peer in LinkPeers.Where(peer => HexLinkAllowed.Has(peer.Granted, HexLinkRights.View)))
        {
            HexLinkPeer asked = peer;
            menu.Add(Said("link.talk.look_menu", ("device", peer.Device.Name)), () => LinkLookHere(asked), string.Empty, "eye").IsVisible = () => document.IsText;
        }
    }

    private void AddLinkTalkShareCommands(List<(string Text, Action Picked)> commands)
    {
        commands.Add((Said("link.talk.share_command"), LinkShareSelection));
        foreach (HexLinkPeer peer in LinkPeers.Where(peer => HexLinkAllowed.Has(peer.Granted, HexLinkRights.View)))
        {
            HexLinkPeer asked = peer;
            commands.Add((Said("link.talk.look_command", ("device", peer.Device.Name)), () => LinkLookHere(asked)));
        }
    }

    /* Opening a reference */

    private void LinkOpenReferenceSaying(string deviceId, string path, int line, int toLine)
    {
        // Opening the file from a card leaves the chat where it is: it is what the person is reading the card in
        bool chatOpen = linkTalkPanel?.IsShownInHierarchy == true;
        _ = Task.Run(async () =>
        {
            string? why = await LinkOpenReference(deviceId, path, line, toLine);
            PostLink(() =>
            {
                if (chatOpen && linkTalkPanel?.IsShownInHierarchy != true && LinkPeers.Count > 0)
                {
                    if (!right.Shown)
                        ToggleProperties();
                    linkTalkOpen = linkTalkExplicit = true;
                }
                if (why is not null)
                    Say(why, error: true);
            });
        });
    }

    /// <summary>
    /// A file a message points at opened here at its lines: the local file when the folder is synced with the device (or the
    /// reference is this device's own), otherwise the device's file over the link as a remote tab. A path with no workspace
    /// folder in front is looked for in the folders of whoever wrote it first, then in this device's. Null when it's open,
    /// why when it isn't. From any thread.
    /// </summary>
    internal async Task<string?> LinkOpenReference(string deviceId, string path, int line, int toLine)
    {
        string wire = path.Replace('\\', '/').TrimStart('/');
        if (wire.Length == 0 || wire.Contains("../", StringComparison.Ordinal) || wire.StartsWith("..", StringComparison.Ordinal))
            return Said("link.talk.not_found", ("path", path));

        bool mine = deviceId == LinkDevice?.Id;
        IReadOnlyList<HexLinkFolder> here = linkServices?.Workspace.Folders ?? [];
        HexLinkPeer? peer = mine ? null : link?.PeerOf(deviceId);
        IReadOnlyList<HexLinkFolder> there = peer?.Workspace.Folders ?? [];
        string first = wire.IndexOf('/') is > 0 and var slash ? wire[..slash] : wire;
        string? why = null;

        // The wire paths it could be, a folder's name in front or not
        List<string> candidates = [];
        IReadOnlyList<HexLinkFolder> owner = mine ? here : there;
        if (owner.Any(folder => PlatformPaths.Comparer.Equals(folder.Name, first)))
            candidates.Add(wire);
        else
            candidates.AddRange(owner.Select(folder => folder.Name + "/" + wire));

        foreach (string candidate in candidates)
        {
            // A folder synced here is opened as this device's own file, not as the other device's over the link
            if (peer is not null && LinkLocalOf(candidate) is { } synced && File.Exists(synced))
            {
                string? opened = await OnEditorForLink(() => Open(synced) ? null : Said("link.remote_not_opened", ("path", candidate)));
                if (opened is null)
                    return await LinkTalkLanded(line, toLine);
                why = opened;
            }
            else if (peer is not null)
            {
                why = await LinkOpenRemote(deviceId, candidate);
                if (why is null)
                    return await LinkTalkLanded(line, toLine);
            }
            else if (LinkLocalOf(candidate) is { } local && File.Exists(local))
            {
                string? opened = await OnEditorForLink(() => Open(local) ? null : Said("link.remote_not_opened", ("path", candidate)));
                if (opened is null)
                    return await LinkTalkLanded(line, toLine);
                why = opened;
            }
        }

        // Not in the writer's folders, a file of this device's workspace of the same path
        foreach (HexLinkFolder folder in here)
        {
            string candidate = PlatformPaths.Comparer.Equals(folder.Name, first) ? wire : folder.Name + "/" + wire;
            if (LinkLocalOf(candidate) is { } local && File.Exists(local))
            {
                string? opened = await OnEditorForLink(() => Open(local) ? null : Said("link.remote_not_opened", ("path", candidate)));
                return opened ?? await LinkTalkLanded(line, toLine);
            }
        }

        return why ?? Said("link.talk.not_found", ("path", path));
    }

    // The caret put on the lines (selected when there are several), on the editor's thread
    private Task<string?> LinkTalkLanded(int line, int toLine) => OnEditorForLink<string?>(() =>
    {
        if (!document.IsText)
            return null;
        var text = document.Editor.Document;
        UITextPosition from = text.Clamp(new UITextPosition(Math.Max(0, line - 1), 0));
        document.Editor.GoTo(from.Line, 0);
        if (toLine > line)
            text.Select(from, text.Clamp(new UITextPosition(toLine - 1, 100_000)));
        document.Editor.Focus();
        return null;
    });

    /* Drawing */

    // The device names a message may mention: those linked, this one and the model
    private List<string> LinkTalkMentionable() =>
        [.. LinkPeers.Select(peer => peer.Device.Name), .. LinkDevice is { } me ? [me.Name] : (string[])[], "ai"];

    // A message's words as lines, each with its file references made links and the names it mentions picked out
    private void LinkTalkLines(StackPanel row, HexLinkTalk message)
    {
        List<string> names = LinkTalkMentionable();
        // One label for the whole text, unless it has file references (which are markdown links, and a markdown label folds line ends)
        if (HexLinkTalkText.References(message.Text).Count == 0)
        {
            row.Add(LinkTalkLine(message.Text, message.Sender.Id, names));
            return;
        }

        foreach (string line in message.Text.Split('\n'))
            row.Add(LinkTalkLine(line.TrimEnd('\r'), message.Sender.Id, names));
    }

    private Label LinkTalkLine(string line, string sender, List<string> names)
    {
        List<HexLinkTalkText.Reference> references = HexLinkTalkText.References(line);
        List<(int Start, int Length)> mentions = [.. HexLinkTalkText.MentionSpans(line, names)
            .Where(span => !references.Any(reference => span.Start < reference.Start + reference.Length && reference.Start < span.Start + span.Length))];
        bool markdown = references.Count > 0;
        string Literal(string text) => (markdown ? Horizon.UI.Drawing.UIRichText.MarkdownLiteral(text) : LinkTalkLiteral(text)).Replace("$(", "$​(", StringComparison.Ordinal);

        // References and mentions in the order they're in the line
        var parts = new List<(int Start, int Length, string Shown)>();
        for (int i = 0; i < references.Count; i++)
            parts.Add((references[i].Start, references[i].Length, $"[{Literal(line.Substring(references[i].Start, references[i].Length))}](ref{i.ToString(CultureInfo.InvariantCulture)})"));
        string me = LinkDevice?.Name ?? string.Empty;
        foreach ((int start, int length) in mentions)
        {
            string name = line.Substring(start + 1, length - 1);
            string colour = HexColour(name.Equals(me, StringComparison.OrdinalIgnoreCase) || name.Equals("ai", StringComparison.OrdinalIgnoreCase)
                ? LinkChatReadableInk(LinkDeviceColour(LinkDevice?.Id ?? sender))
                : LinkChatReadableInk(LinkDeviceColour(LinkPeers.FirstOrDefault(peer => peer.Device.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Device.Id ?? sender)));
            parts.Add((start, length, $"[b][color:{colour}]{Literal(line.Substring(start, length))}[/color][/b]"));
        }

        parts.Sort((a, b) => a.Start.CompareTo(b.Start));
        var built = new StringBuilder();
        int at = 0;
        foreach ((int start, int length, string shown) in parts)
        {
            built.Append(Literal(line[at..start])).Append(shown);
            at = start + length;
        }

        built.Append(Literal(line[at..]));
        var label = new Label
        {
            Text = built.ToString(), Align = Origin.TopLeft, Wrap = true, Fill = UIFill.Horizontal, TextScale = 0.145f, Markdown = markdown
        };
        if (markdown)
            label.OnLink = target =>
            {
                if (target.StartsWith("ref", StringComparison.Ordinal) && int.TryParse(target[3..], NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index < references.Count)
                    LinkOpenReferenceSaying(sender, references[index].Path, references[index].Line, references[index].ToLine);
            };
        return label;
    }

    // The body of a message with a reference: a code card for a shared selection, the ping's words and a Go button for a look-here
    private void LinkTalkReference(StackPanel row, HexLinkTalk message, HexLinkTalkRef reference)
    {
        string where = $"{reference.Path}:{reference.Lines}";
        string sender = message.Sender.Id;
        Button Open(string label) => new()
        {
            Style = "button_flat", Animated = false, Label = label, LabelScale = 0.12f, Align = Origin.Left, Size = new Vector2(0, 20),
            Fill = UIFill.Horizontal, Padding = new UIEdges(2, 0, 2, 0), Tooltip = Said("link.talk.open_tip", ("where", where)),
            OnPressed = () => LinkOpenReferenceSaying(sender, reference.Path, reference.Line, reference.ToLine)
        };

        if (reference.Kind == HexLinkTalkRef.RELEASE)
        {
            // A request is its words, nothing to open
            row.Add(new Label { Text = LinkTalkLiteral(reference.Fallback()), Align = Origin.TopLeft, Wrap = true, Fill = UIFill.Horizontal, TextScale = 0.145f });
            return;
        }

        Vector4 band = Skin is { } skin ? skin.BorderColor with { W = 0.35f } : new Vector4(0.5f, 0.5f, 0.5f, 0.2f);
        if (reference.Kind == HexLinkTalkRef.LOOK)
        {
            string target = reference.To is { } to
                ? string.Equals(to, LinkDevice?.Id, StringComparison.OrdinalIgnoreCase) ? Said("link.talk.look_you") : LinkNameOf(to)
                : string.Empty;
            row.Add(new Label
            {
                Text = $"$(eye) {LinkTalkLiteral(Said("link.talk.look_row", ("target", target), ("where", where)))}", Align = Origin.TopLeft, Wrap = true,
                Fill = UIFill.Horizontal, TextScale = 0.145f
            });
            row.Add(Open($"$(go-to-file) {Said("link.talk.look_go")}"));
            return;
        }

        if (reference.Note is { Length: > 0 } note)
            row.Add(new Label { Text = LinkTalkLiteral(note), Align = Origin.TopLeft, Wrap = true, Fill = UIFill.Horizontal, TextScale = 0.145f });

        // In the colours Hex's own code blocks and fields have, not a grey band
        var card = new StackPanel
        {
            Fill = UIFill.Horizontal, Spacing = 2, Padding = new UIEdges(8, 5, 8, 5), Radius = 4,
            Color = Skin is { } fieldSkin ? fieldSkin.FieldColor with { W = 1 } : band, Outline = Skin?.BorderColor ?? Vector4.Zero
        };
        card.Add(new Label
        {
            Text = $"$(file-code) [b]{LinkTalkLiteral(where)}[/b]", Align = Origin.TopLeft, Wrap = true, Fill = UIFill.Horizontal, TextScale = 0.125f
        });
        if (reference.Snippet is { Length: > 0 } snippet)
        {
            string language = Path.GetExtension(reference.Path).TrimStart('.');
            card.Add(new Label
            {
                // A fence the snippet can't end early, coloured as its file's language would be
                Text = $"```{language}\n{snippet.Replace("```", "``​`", StringComparison.Ordinal).Replace("$(", "$​(", StringComparison.Ordinal)}\n```",
                Markdown = true, CodeLanguage = language.Length > 0 ? language : null, Align = Origin.TopLeft, Wrap = true, Fill = UIFill.Horizontal, TextScale = 0.12f
            });
        }

        card.Add(Open($"$(go-to-file) {Said("link.talk.open_code")}"));
        row.Add(card);
    }

    // The completion of a device's name after an @ in the chat's input
    private UICompletionRequest? LinkTalkComplete(CodeEditor editor)
    {
        UITextPosition caret = editor.Document.Caret;
        string line = editor.Document.LineAt(caret.Line);
        if (HexLinkTalkText.Completing(line, caret.Column) is not { } typing)
            return null;
        // Never this device's own name: it is the one typing
        string? own = LinkDevice?.Name;
        List<UICompletion> items = [.. LinkTalkMentionable().Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => own is null || !name.Equals(own, StringComparison.OrdinalIgnoreCase))
            .Select(name => new UICompletion(name, name == "ai" ? Said("link.talk.complete_ai") : Said("link.talk.complete_device"), name + " "))];
        return new UICompletionRequest(typing.Start + 1, items);
    }
}
