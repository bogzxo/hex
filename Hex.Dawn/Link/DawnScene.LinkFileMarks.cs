using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// Who holds or edits each file, in the Explorer: a dot in the device's colour (laid with the presence dots, see
// LinkUpdateExplorerPresence), a tooltip that says what and for how long, and a menu to release a file of this device's or to
// ask another to
internal sealed partial class DawnScene
{
    // The marks' lines last added to a row's tooltip, so a later Decorate cuts them off before making them again
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<UITreeItem, string> linkMarkTips = new();

    /// <summary>
    /// The copy another device's version is kept in beside a file in conflict (name.theirs-device.ext) coloured as a conflict is
    /// and badged, the way git's rows are, so it isn't mistaken for a file of the project.
    /// </summary>
    private void LinkConflictCopyMark(UITreeItem row, string path)
    {
        if (!Path.GetFileName(path).Contains(".theirs-", StringComparison.Ordinal))
            return;
        row.Color = WarningColor;
        row.Badge = "C";
        row.BadgeColor = WarningColor;
        row.Tooltip = path + "\n" + Said("link.conflict_copy_tip");
    }

    /// <summary>The claims and holds of a file, this device's first.</summary>
    internal IReadOnlyList<HexLinkFileMark> LinkFileMarksOf(string? path)
    {
        if (path is null || linkServices is null)
            return [];
        string local = LinkDevice?.Id ?? string.Empty;
        var holds = new List<(string DeviceId, string DeviceName, TimeSpan? Age)>();
        if (LinkHeldFile(path) is { } held)
            holds.Add((local, LinkDevice?.Name ?? string.Empty, DateTimeOffset.UtcNow - held.HeldAt));
        if (LinkRemoteHolderOf(path) is { } holder)
            holds.Add((holder.DeviceId, holder.Name, null));
        return HexLinkFileMark.Of(LinkClaimsAt(path), System.Environment.TickCount64, local, holds);
    }

    private static string LinkMarkWho(HexLinkFileMark mark) =>
        mark.Own ? Said("link.mark_this_device") : OneLine(mark.DeviceName, 40);

    /// <summary>What a mark says, "Held by Beta" or "Being edited by Beta (bogdan)", with how long when that is known.</summary>
    internal static string LinkMarkTip(HexLinkFileMark mark)
    {
        string who = LinkMarkWho(mark);
        string person = mark.Person.Length > 0 ? OneLine(mark.ChatId is { } chat ? $"#{chat} {mark.Person}" : mark.Person, 40) : string.Empty;
        string said = mark.Kind switch
        {
            HexLinkMarkKind.Held => Said("link.mark_held", ("device", who)),
            HexLinkMarkKind.Planned => Said("link.mark_planned", ("device", who), ("person", person)),
            HexLinkMarkKind.Publishing => Said("link.mark_publishing", ("device", who)),
            _ => Said("link.mark_editing", ("device", who), ("person", person))
        };
        return mark.Age is { } age ? Said("link.mark_age", ("what", said), ("age", HexLinkFileMark.AgeText(age))) : said;
    }

    // The marks' lines on a row's tooltip, the ones it had cut off first
    private void LinkMarkTooltip(UITreeItem row, string path)
    {
        if (linkMarkTips.TryGetValue(row, out string? previous))
        {
            linkMarkTips.Remove(row);
            if (row.Tooltip.EndsWith(previous, StringComparison.Ordinal))
                row.Tooltip = row.Tooltip[..Math.Max(0, row.Tooltip.Length - previous.Length - 1)];
        }

        IReadOnlyList<HexLinkFileMark> marks = LinkFileMarksOf(path);
        if (marks.Count == 0)
            return;
        // Two devices editing at once is one line, not one each; and one line says what the dots are
        var lines = new List<string>();
        HexLinkFileMark? mine = marks.FirstOrDefault(mark => mark.Own && mark.Kind == HexLinkMarkKind.Editing);
        HexLinkFileMark? theirs = marks.FirstOrDefault(mark => !mark.Own && mark.Kind == HexLinkMarkKind.Editing);
        if (mine is not null && theirs is not null)
            lines.Add(Said("link.mark_together", ("device", OneLine(theirs.DeviceName, 40))));
        foreach (HexLinkFileMark mark in marks)
        {
            if (mine is not null && theirs is not null && mark.Kind == HexLinkMarkKind.Editing)
                continue;
            lines.Add(LinkMarkTip(mark));
        }

        lines.Add(Said("link.mark_legend"));
        string added = string.Join("\n", lines);
        linkMarkTips.Add(row, added);
        row.Tooltip = (row.Tooltip.Length > 0 ? row.Tooltip : path) + "\n" + added;
    }

    /// <summary>Every file that has a mark, by its path here, with the devices' colours; what the Explorer's dots are laid from.</summary>
    private void LinkFileMarkColours(Dictionary<string, List<System.Numerics.Vector4>> into, System.Text.StringBuilder key)
    {
        if (linkServices is not { } services)
            return;
        void Put(string? path, string deviceId)
        {
            if (path is null)
                return;
            System.Numerics.Vector4 colour = LinkDeviceColour(deviceId);
            if (!into.TryGetValue(path, out List<System.Numerics.Vector4>? dots))
                into[path] = dots = [];
            if (dots.Contains(colour))
                return;
            dots.Add(colour);
            key.Append(path).Append('|').Append(colour).Append(';');
        }

        if (link?.Claims is { } exchange)
        {
            foreach (HexLinkClaim claim in exchange.List(System.Environment.TickCount64).Where(claim => !claim.Prefix))
                Put(LinkLocalOf(claim.Wire), claim.DeviceId);
        }

        string own = LinkDevice?.Id ?? string.Empty;
        foreach (HexLinkFolder folder in services.Workspace.Folders)
        {
            foreach (HexLinkHold held in services.Holds(folder).List())
            {
                try { Put(HexLinkFiles.Resolve([folder], held.Path), own); }
                catch (HexLinkException) { }
            }
        }

        foreach (var (path, holder) in linkRemoteHolds)
            Put(path, holder.DeviceId);
    }

    /// <summary>
    /// What Link can do about a file, edit it together, keep changes to oneself, share them, ask another device to finish, under
    /// one "Link" submenu so the menu of a file keeps its own rows together and these four don't sit unheaded in it.
    /// </summary>
    private void AddLinkMenu(Menu menu, string? path, bool includeClaims = true, bool includeHold = true)
    {
        if (path is null || Directory.Exists(path))
            return;
        var link = new Menu(Said("link.menu_title"));
        if (includeClaims)
            AddLinkClaimMenu(link, path);
        if (includeHold)
            AddLinkHoldMenu(link, path);
        AddLinkMarkMenu(link, path);
        if (link.Items.Count == 0)
            return;
        menu.AddSeparator();
        menu.Add(new MenuItem { Label = link.Title, Icon = "link", Submenu = link });
    }

    /// <summary>"Share my changes" for what this device holds or has claimed of a file, "Ask Beta to finish editing" for what another does.</summary>
    private void AddLinkMarkMenu(Menu menu, string? path)
    {
        if (path is null || Directory.Exists(path))
            return;
        IReadOnlyList<HexLinkFileMark> marks = LinkFileMarksOf(path);
        if (marks.Count == 0)
            return;
        menu.AddSeparator();
        HexLinkFileMark? own = marks.FirstOrDefault(mark => mark.Own);
        if (own is not null)
            menu.Add(Said(own.Kind == HexLinkMarkKind.Held ? "link.mark_release" : "link.mark_release_claim"), () => LinkReleaseOwn(path), icon: "cloud-upload");
        foreach (HexLinkFileMark other in marks.Where(mark => !mark.Own).DistinctBy(mark => mark.DeviceId))
            menu.Add(Said("link.mark_ask_release", ("device", OneLine(other.DeviceName, 40))), () => LinkAskToRelease(path, other), icon: "comment-discussion");
    }

    /// <summary>Lets go of what this device holds or has explicitly claimed of a file: its hold goes through Submit, its claims come off the board.</summary>
    private void LinkReleaseOwn(string path)
    {
        bool released = false;
        if (link?.Claims is { } exchange && ClaimPathOf(exchange.Folders, path, false) is { } shared)
        {
            foreach (var entry in linkExplicitClaims.Keys.Where(entry => entry.Scope == shared.Scope && entry.Key.Path == shared.Relative && !entry.Key.Prefix).ToArray())
                released |= linkExplicitClaims.Remove(entry);
            linkClaimWait = 0;
        }

        if (LinkHoldTarget(path) is { } target && target.Store.Get(target.Wire) is not null)
        {
            PreviewLinkSubmit(target.Folder, [target.Wire]);
            return;
        }

        // The editing claim of an open, edited file: let go of here, the unsaved edits staying local until they are saved
        if (!released && Context.Documents.Find(path) is { IsText: true, Modified: true } edited && documents.Contains(edited))
        {
            linkEditingReleased[path] = LinkLiveOf(edited) is { } sharedEdit ? sharedEdit.LocalEditVersion : edited.Editor.Document.Version;
            linkClaimWait = 0;
            Say(Said("link.mark_released_editing"));
            return;
        }

        if (!released)
            Say(Said("link.mark_release_none"));
        else
            Say(Said("link.mark_released"));
    }

    /// <summary>Asks the device in Link Chat, naming the file, to let it go.</summary>
    internal void LinkAskToRelease(string path, HexLinkFileMark mark)
    {
        if (link is not { } current)
            return;
        string file = linkServices?.Workspace.Folders.Where(folder => PlatformPaths.IsUnder(path, folder.Path))
            .MaxBy(folder => folder.Path.Length) is { } folder ? folder.Name + "/" + HexExcludes.Relative(folder.Path, path) : Path.GetFileName(path);
        // Held on their side its edits aren't reaching us, which is worth saying; otherwise the plain question, which the
        // other device answers on a card with a Done button (see LinkTalkNotice)
        string text = mark.Kind == HexLinkMarkKind.Held
            ? Said("link.mark_ask_text_held", ("device", OneLine(mark.DeviceName, 40)), ("file", file))
            : Said("link.mark_ask_notice", ("device", OneLine(mark.DeviceName, 40)), ("file", Path.GetFileName(path)));
        var request = new HexLinkTalkRef(HexLinkTalkRef.RELEASE, file, 1, 1, Note: text, To: mark.DeviceId);
        switch (current.SayTalk(request))
        {
            case HexLinkTalkResult.Sent:
                Notify(Said("link.mark_asked", ("device", OneLine(mark.DeviceName, 40))));
                break;
            case HexLinkTalkResult.TooFast:
                Say(Said("link.talk.slow"), error: true);
                break;
            case HexLinkTalkResult.NoView:
                Say(Said("link.talk.no_view"), error: true);
                break;
            default:
                Say(Said("link.mark_ask_failed"), error: true);
                break;
        }
    }
}
