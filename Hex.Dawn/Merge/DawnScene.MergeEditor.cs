using System.Numerics;
using Hex.Core.Git;
using Hex.Core.Merge;

namespace Hex.Dawn;

internal sealed partial class HexDocument
{
    internal MergeEditor? Merge { get; set; }
}

internal sealed partial class DawnScene
{
    private Panel? shownMergePanel;

    private void BuildMergeEditor()
    {
        Add("Next conflict", "Ctrl+F7", () => document.Merge?.Go(1));
        Add("Previous conflict", "Ctrl+Shift+F7", () => document.Merge?.Go(-1));
        Add("Accept mine", "Ctrl+Alt+1", () => document.Merge?.AcceptCurrent(MergeChoice.Mine));
        Add("Accept theirs", "Ctrl+Alt+2", () => document.Merge?.AcceptCurrent(MergeChoice.Theirs));
        Add("Accept both", "Ctrl+Alt+3", () => document.Merge?.AcceptCurrent(MergeChoice.BothMineFirst));
        Add("Complete merge", "Ctrl+Shift+Enter", () => CompleteMergeEditor(document));

        void Add(string name, string shortcut, Action action)
        {
            var command = commands.Add("Git/conflicts", name, action, shortcut, "diff", UIKeyUse.BoundWhileTyping);
            command.IsEnabled = command.IsVisible = () => document.Merge is { Busy: false, Session.IsBinary: false };
        }
    }

    private MergeEditor OpenMergeEditor(MergeSession session)
    {
        if (Split)
            Unsplit();
        var tab = new HexDocument($"Merge: {session.Title}", stage.CreateModule())
        {
            IsText = true,
            FallbackDirectory = Path.GetDirectoryName(session.Path) ?? browseDirectory,
        };
        tab.Module.Interactive = false;
        PrepareEditor(tab);
        tab.Editor.Highlighter = CodeLanguages.ForFile(session.Path);
        tab.Editor.CheckSyntax = false;
        tab.Editor.WordWrap = false;
        tab.Editor.StickyScroll = false;
        tab.Editor.Minimap = false;
        tab.Editor.OnChanged = () => tabsDirty = true;
        var merge = new MergeEditor(session, tab.Editor, () => CompleteMergeEditor(tab), () => CancelMergeEditor(tab),
            side => CompleteMergeEditor(tab, side), () => WarningColor,
            () => compositor.Skin?.Palette.Success ?? WarningColor);
        tab.Merge = merge;
        tab.SavedCode = tab.Editor.Text;
        documents.Add(tab);
        Show(tab);
        if (!session.IsBinary)
            languages.AttachMergeResult(tab, session.Path);
        tabsDirty = true;
        return merge;
    }

    private void CompleteMergeEditor(HexDocument tab, MergeSide? side = null)
    {
        if (tab.Merge is not { Busy: false } merge || (side is null && !merge.CanComplete))
            return;
        MergeSession session = merge.Session;
        byte[]? result = side is null ? merge.ResultBytes() : null;
        merge.SetBusy(true);

        _ = Task.Run(async () =>
        {
            try
            {
                if (side is { } chosen && session.SideChoices is { } choices)
                    await choices.Complete(chosen);
                else if (side is { } binary && session.Model.BinarySides is { } files)
                    await session.Complete(binary == MergeSide.Mine ? files.Mine : files.Theirs);
                else if (result is not null)
                    await session.Complete(result);
                PostLink(() => CloseMergeTab(tab));
            }
            catch (Exception error)
            {
                PostLink(() =>
                {
                    merge.SetBusy(false);
                    Say(error.Message, error: true);
                });
            }
        });
    }

    private void CancelMergeEditor(HexDocument tab)
    {
        if (tab.Merge is not { Busy: false } merge)
            return;
        if (!merge.Changed)
            Cancel();
        else
            UIDialog.Show(chrome, "Cancel merge?", "Your resolutions in this merge editor will be discarded.",
                new DialogChoice("Discard resolutions", Cancel, Risky: true),
                new DialogChoice("Continue merging"));

        void Cancel()
        {
            merge.Session.Cancel();
            CloseMergeTab(tab);
        }
    }

    private void CloseMergeTab(HexDocument tab)
    {
        if (!documents.Contains(tab))
            return;
        languages.DetachMergeResult(tab);
        HexDocument previous = document;
        if (document != tab)
            Show(tab);
        CloseNow();
        if (previous != tab && documents.Contains(previous))
            Show(previous);
    }

    private void ShowMergePanel()
    {
        if (shownMergePanel is not null)
        {
            codeScroll.Remove(shownMergePanel);
            shownMergePanel = null;
        }
        if (document.Merge is { } merge)
        {
            shownMergePanel = codeScroll.Add(merge.Root);
            merge.Fit(codeScroll.Size);
        }
    }

    private void FitMergeEditor()
    {
        if (document.Merge is not { } merge)
            return;
        merge.Fit(codeScroll.Size);
        merge.FollowScroll();
    }

    private void OpenMarkedMergeEditor()
    {
        if (document.Path is { } path)
            OpenGitMergeEditor(null, path, document.Editor.Text);
    }

    private void OpenGitMergeEditor(GitRepository? repository, string path, string? markedText = null)
    {
        if (documents.FirstOrDefault(tab => tab.Merge is { } active &&
            PlatformPaths.Comparer.Equals(active.Session.Path, path)) is { } existing)
        {
            Show(existing);
            return;
        }

        HexDocument? original = Context.Documents.Find(path);
        int version = original?.Editor.Document.Version ?? -1;
        if (original is not null && HasChanges(original))
        {
            Say("Save the file before opening the merge editor.", error: true);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                repository ??= GitRepository.Find(path);
                MergeSession? session = repository is { } repo ? await GitUnmerged.Open(repo, path, markedText) :
                    markedText is { } text ? MarkedFileSession(text) : null;
                PostLink(() =>
                {
                    if (session is null)
                        Say("The file has no merge conflicts.");
                    else if (!documents.Any(tab => tab.Merge is { } merge &&
                        PlatformPaths.Comparer.Equals(merge.Session.Path, path)))
                        OpenMergeEditor(session with
                        {
                            Complete = bytes => CompleteGitEdit(() => session.Complete(bytes), bytes),
                            SideChoices = session.SideChoices is { } choices ? choices with
                            {
                                Complete = side => CompleteGitEdit(() => choices.Complete(side), null)
                            } : null,
                        });
                });
            }
            catch (Exception error)
            {
                PostLink(() => Say(error.Message, error: true));
            }
        });

        async Task CompleteGitEdit(Func<Task> write, byte[]? bytes)
        {
            LinkTabStamp stamp = await OnEditorForLink(() =>
            {
                LinkTabStamp current = LinkTabBeforeWrite(path);
                if (current.Dirty || current.Document is { } open &&
                    (open != original || open.Editor.Document.Version != version))
                    throw new IOException("The open file changed. Open the merge editor again.");

                return current;
            });
            await write();
            if (bytes is null && File.Exists(path))
                bytes = await File.ReadAllBytesAsync(path);
            await OnEditorForLink(() =>
            {
                if (bytes is not null && HexLinkFiles.Decode(bytes) is { } decoded)
                    LinkTakeIntoTab(path, decoded.Text, stamp);
                git.Refresh(path);
                return true;
            });
        }

        MergeSession? MarkedFileSession(string text)
        {
            byte[] before = File.ReadAllBytes(path);
            if (HexLinkFiles.Decode(before) is not { } disk)
                return null;
            MergeModel model = MergeModel.FromMarkers(text, disk.Encoding);
            return model.ConflictCount == 0 ? null : new(Path.GetFileName(path), "Mine", "Theirs", "Base", model,
                bytes => HexLinkFiles.CompareAndReplace(path, Path.GetFileName(path), HexLinkFiles.Hash(before), bytes,
                    CancellationToken.None), static () => { }, path);
        }
    }
}
