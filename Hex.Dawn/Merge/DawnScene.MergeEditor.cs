using System.Numerics;
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
        HexDocument original = document;
        string before = original.Editor.Text;
        MergeModel model = MergeModel.FromMarkers(before);
        if (model.ConflictCount == 0 || original.Path is not { } path)
            return;
        OpenMergeEditor(new(Path.GetFileName(path), "Mine", "Theirs", "Base", model, bytes =>
            OnEditorForLink(() =>
            {
                if (!documents.Contains(original) || original.Editor.Text != before)
                    throw new IOException("The original file changed. Open the merge editor again.");
                original.Editor.Document.Replace(HexLinkFiles.Decode(bytes)?.Text ?? "");
                return true;
            }), static () => { }, path));
    }
}
