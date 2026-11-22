using Hex.Core.Git;
using Hex.Core.Merge;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private readonly Lock linkHoldServicesGate = new();
    private readonly Dictionary<HexLinkHoldStore, HexLinkHoldService> linkHoldServices = new();
    private DateTimeOffset linkHoldCheckAt;

    private void LinkHoldPaletteCommands(List<(string Text, Action Picked)> entries)
    {
        if (LinkHoldTarget(document.Path) is { } item)
            Add("Hold changes to this file", () => StartLinkHold(() => HoldLinkPaths(item.Folder, [item.Wire])));
        Add("Hold changes to all open files", HoldOpenLinkFiles);
        Add("Hold all uncommitted changes", HoldUncommittedLinkFiles);
        Add("Submit held changes…", () => WithLinkHeldFolder(folder => PreviewLinkSubmit(folder, null)));
        Add("Update held files from others", () => WithLinkHeldFolder(folder => PreviewLinkUpdate(folder, null)));
        Add("Discard held changes…", () => WithLinkHeldFolder(folder => ConfirmLinkDiscard(folder, null)));
        Add("Show held files", () => WithLinkHeldFolder(ShowLinkHeld));

        void Add(string title, Action picked) => entries.Add(("Hex Link: " + title, picked));
    }

    private (HexLinkFolder Folder, HexLinkHoldStore Store, string Wire)? LinkHoldTarget(string? full)
    {
        if (full is null || linkServices is not { } services)
            return null;
        if (LinkHoldFolderOf(services, full) is not { } folder)
            return null;
        HexLinkHoldStore store = services.Holds(folder);
        return store.WireFromFullPath(full) is { } wire ? (folder, store, wire) : null;
    }

    private HexLinkHoldService LinkHoldServiceFor(HexLinkFolder folder)
    {
        LinkServices services = linkServices ?? throw HexLinkException.NotFound("Hex Link is off.");
        HexLinkHoldStore store = services.Holds(folder);
        lock (linkHoldServicesGate)
        {
            if (linkHoldServices.TryGetValue(store, out HexLinkHoldService? kept))
                return kept;
            var service = new HexLinkHoldService(folder, store)
            {
                AgreedBasis = async (wire, cancel) =>
                {
                    HexLinkSync[] syncs;
                    lock (linkSyncGate)
                        syncs = [.. linkSyncs.Values.Where(each => PlatformPaths.Comparer.Equals(each.Folder, folder.Name))
                            .Select(each => each.Sync).OfType<HexLinkSync>()];
                    foreach (HexLinkSync sync in syncs)
                        if (ReferenceEquals(sync.Holds, store) && await sync.HoldBasis(wire, cancel) is { } basis)
                            return basis;
                    return null;
                },
                GatePolicy = () => LinkPrivateWorktreeGateForScope(services, folder) ?? folder.WorktreeGate,
                Prepare = async (paths, cancel) => await OnEditorForLink(() =>
                {
                    if (LinkHoldUnsaved(folder, paths) is [{ Path: { } path }, ..])
                        throw new IOException($"Save {Path.GetFileName(path)} before changing its hold.");
                    return true;
                }).WaitAsync(cancel),
                Apply = LinkCompareAndReplace,
                ApplyStream = LinkCompareAndReplaceStream,
            };
            linkHoldServices.Add(store, service);
            return service;
        }
    }

    private static HexLinkFolder? LinkHoldFolderOf(LinkServices services, string full) => services.Workspace.Folders
        .Where(folder => PlatformPaths.IsUnder(full, folder.Path) && !PlatformPaths.Same(full, folder.Path))
        .MaxBy(folder => folder.Path.Length);

    private HexLinkHold? LinkHeldFile(string? path)
    {
        if (path is null || linkServices is not { } services || LinkHoldFolderOf(services, path) is not { } folder ||
            services.Holds(folder) is not { Count: > 0 } store || store.WireFromFullPath(path) is not { } wire)
            return null;
        return store.Get(wire);
    }

    private void HoldOpenLinkFiles()
    {
        var targets = documents.Where(tab => tab is { Merge: null, DiffOf: null })
            .Select(tab => LinkHoldTarget(tab.Path)).OfType<(HexLinkFolder Folder, HexLinkHoldStore Store, string Wire)>()
            .GroupBy(target => target.Folder).Select(group => (Folder: group.Key, Paths: group.Select(target => target.Wire).ToArray())).ToArray();
        if (targets.Length == 0)
        {
            Say("Open a file in a shared folder first.");
            return;
        }
        StartLinkHold(async () =>
        {
            foreach (var target in targets)
                await HoldLinkPaths(target.Folder, target.Paths);
        });
    }

    private void HoldUncommittedLinkFiles()
    {
        HexLinkWorkspace? workspace = linkServices?.Workspace;
        HexLinkFolder? folder = LinkHoldTarget(document.Path)?.Folder ?? workspace?.Folders.FirstOrDefault();
        if (folder is null || workspace is null)
        {
            Say("Open a shared folder first.");
            return;
        }

        StartLinkHold(async () =>
        {
            HexLinkHoldStore store = linkServices!.Holds(folder);
            var paths = new HashSet<string>(PlatformPaths.Comparer);
            var repos = workspace.Repositories.Where(repo => PlatformPaths.IsUnder(repo.Path, folder.Path))
                .Select(repo => GitRepository.Find(repo.Path)).OfType<GitRepository>().DistinctBy(repo => repo.Root, PlatformPaths.Comparer).ToList();
            if (GitRepository.Find(folder.Path) is { } containing && !repos.Any(repo => PlatformPaths.Same(repo.Root, containing.Root)))
                repos.Add(containing);
            foreach (GitRepository repo in repos)
            {
                if (repo.Status() is not { } status)
                    continue;
                foreach (var (path, state) in status.Files)
                {
                    if (state.Change is GitChange.None or GitChange.Ignored || state.IsRepository)
                        continue;
                    if (store.WireFromFullPath(path) is { } wire)
                        paths.Add(wire);
                    if (state.From is { } old && store.WireFromFullPath(old) is { } previous)
                        paths.Add(previous);
                }
            }
            await HoldLinkPaths(folder, [.. paths]);
        });
    }

    private async Task HoldLinkPaths(HexLinkFolder folder, IReadOnlyList<string> paths)
    {
        HexLinkHoldService service = LinkHoldServiceFor(folder);
        foreach (string wire in paths.Distinct(PlatformPaths.Comparer))
            await LinkHoldDocument(service, wire, CancellationToken.None);
        PostLink(() => Say(paths.Count > 0 ? $"Held {HexText.Count("file", paths.Count)}." : "No changes to hold."));
    }

    private void StartLinkHold(Func<Task> action) => _ = Task.Run(async () =>
    {
        try { await action(); }
        catch (Exception error) { PostLink(() => Notify(error.Message, error: true)); }
    });

    private void WithLinkHeldFolder(Action<HexLinkFolder> picked)
    {
        if (linkServices is not { } services)
        {
            Say("Hex Link is off.");
            return;
        }
        var folder = LinkHoldTarget(document.Path)?.Folder;
        if (folder is null || services.Holds(folder).Count == 0)
            folder = services.Workspace.Folders.FirstOrDefault(each => services.Holds(each).Count > 0);
        if (folder is null)
            Say("There are no held files.");
        else
            picked(folder);
    }

    private void AddLinkHoldMenu(Menu menu, string? path)
    {
        if (LinkHoldTarget(path) is not { } target || Directory.Exists(path))
            return;
        menu.AddSeparator();
        if (target.Store.Get(target.Wire) is { } held)
        {
            menu.Add("Submit held changes", () => PreviewLinkSubmit(target.Folder, [target.Wire]), icon: "cloud-upload");
            menu.Add("Update from others", () => PreviewLinkUpdate(target.Folder, [target.Wire]), icon: "cloud-download")
                .IsEnabled = () => held.HasIncoming;
            menu.Add("Discard held changes", () => ConfirmLinkDiscard(target.Folder, [target.Wire]), icon: "discard", destructive: true);
        }
        else
            menu.Add("Hold changes", () => StartLinkHold(() => HoldLinkPaths(target.Folder, [target.Wire])), icon: "pin");
    }

    private void PreviewLinkSubmit(HexLinkFolder folder, IReadOnlyList<string>? paths) =>
        SaveLinkHeldFirst(folder, paths, () => StartLinkHold(async () =>
        {
            HexLinkHoldService service = LinkHoldServiceFor(folder);
            var plan = await service.Plan(paths);
            HexLinkWorktreeGatePolicy? gate = linkServices is { } services ?
                LinkPrivateWorktreeGateForScope(services, folder) ?? folder.WorktreeGate : null;
            PostLink(() =>
            {
                if (plan.Count == 0) { Say("There are no held files."); return; }
                ToggleButton? check = null;
                var choices = new List<DialogChoice> { new("Submit held changes", () => Submit(check?.State == true)) };
                if (gate is not null)
                    choices.Add(new("Submit anyway", () => Submit(check: false), Risky: true));
                choices.Add(new("Cancel"));
                UIDialog preview = UIDialog.Show(chrome, "Submit held changes", "", [.. choices]);
                ShowLinkHoldRows(preview, plan.Select(item => $"{item.Path} · {LinkHoldOutcome(item.Outcome)}").ToArray());
                if (gate is not null)
                {
                    check = preview.Body.Add(new ToggleButton("Run pre-publish check", true)
                    {
                        Tooltip = gate.Options.Describe(),
                    });
                    preview.Body.Add(LinkLine(gate.Options.Describe()));
                }
            });

            void Submit(bool check) => StartLinkHold(async () =>
            {
                HexLinkHoldOperation operation = await service.Submit(paths, check: check);
                PostLink(() =>
                {
                    if (operation.Gate is { Passed: false } failure)
                        UIDialog.Show(chrome, "Submit check failed", failure.Output,
                            new DialogChoice("Submit anyway", () => Submit(check: false), Risky: true), new DialogChoice("Cancel"));
                    else
                        ShowLinkHoldMerges(operation, "Submitted");
                });
            });
        }));

    private void PreviewLinkUpdate(HexLinkFolder folder, IReadOnlyList<string>? paths) =>
        SaveLinkHeldFirst(folder, paths, () => StartLinkHold(async () =>
        {
            HexLinkHoldOperation updated = await LinkHoldServiceFor(folder).Update(paths);
            PostLink(() => ShowLinkHoldMerges(updated, "Updated"));
        }));

    private void ConfirmLinkDiscard(HexLinkFolder folder, IReadOnlyList<string>? paths)
    {
        int count = paths?.Count ?? linkServices?.Holds(folder).Count ?? 0;
        if (count == 0)
            return;
        UIDialog.Show(chrome, "Discard held changes", $"Restore the shared versions of {HexText.Count("file", count)}? Hex keeps a local copy of the discarded files for seven days.",
            new DialogChoice("Discard held changes", () => SaveLinkHeldFirst(folder, paths, () => StartLinkHold(async () =>
            {
                HexLinkHoldOperation discarded = await LinkHoldServiceFor(folder).Discard(paths);
                string saved = discarded.DiscardedDirectory is { } dir ? $" Copies saved in {dir}." : "";
                PostLink(() => Say($"Discarded changes to {HexText.Count("file", discarded.Paths.Count)}.{saved}"));
            })), Risky: true), new DialogChoice("Cancel"));
    }

    private IReadOnlyList<HexDocument> LinkHoldUnsaved(HexLinkFolder folder, IReadOnlyList<string> paths)
    {
        var full = new HashSet<string>(paths.Select(path => HexLinkFiles.Resolve([folder], path)), PlatformPaths.Comparer);
        return [.. documents.Where(tab => tab.Path is { } path && full.Contains(path) && HasChanges(tab))];
    }

    private void SaveLinkHeldFirst(HexLinkFolder folder, IReadOnlyList<string>? paths, Action next)
    {
        var dirty = LinkHoldUnsaved(folder, paths ?? [.. LinkHoldServiceFor(folder).List().Select(held => held.Path)]);
        if (dirty.Count == 0) { next(); return; }
        UIDialog.Show(chrome, "Save held files",
            "Save your changes before continuing?\n\n" + string.Join("\n", dirty.Select(tab => tab.Path)),
            new DialogChoice("Save and continue", () =>
            {
                HexDocument previous = document;
                foreach (HexDocument tab in dirty)
                {
                    if (!documents.Contains(tab) || !HasChanges(tab) || tab.Path is not { } path)
                        continue;
                    Show(tab);
                    SaveTo(path);
                    if (HasChanges(tab))
                        return;
                }
                if (documents.Contains(previous))
                    Show(previous);
                next();
            }), new DialogChoice("Cancel"));
    }

    private void ShowLinkHoldMerges(HexLinkHoldOperation operation, string done)
    {
        MergeSession[] sessions = [.. operation.NeedsMerge.Select(file => file.Session).OfType<MergeSession>()];
        if (sessions.Length == 0)
        {
            Say(operation.Paths.Count == 0 ? "There are no incoming changes." : $"{done} {HexText.Count("file", operation.Paths.Count)}.");
            return;
        }
        ShowNext(0);

        void ShowNext(int index)
        {
            if (index == sessions.Length)
            {
                Say($"{done} held changes.");
                return;
            }
            MergeSession current = sessions[index];
            Say($"{index} of {sessions.Length} merged");
            OpenMergeEditor(current with
            {
                Complete = bytes => Finish(() => current.Complete(bytes)),
                SideChoices = current.SideChoices is { } sides ? sides with { Complete = side => Finish(() => sides.Complete(side)) } : null,
                Cancel = () =>
                {
                    foreach (MergeSession session in sessions.Skip(index))
                        session.Cancel();
                    Say("Merge canceled. Unsubmitted changes are still held.");
                },
            });

            async Task Finish(Func<Task> complete)
            {
                try { await complete(); }
                catch (MergeCheckFailed error)
                {
                    throw new MergeCheckFailed(error.Message, () => Finish(error.OverrideCheck));
                }
                PostLink(() => ShowNext(index + 1));
            }
        }
    }

    private void ShowLinkHeld(HexLinkFolder folder)
    {
        IReadOnlyList<HexLinkHold> held = LinkHoldServiceFor(folder).List();
        if (held.Count == 0)
        {
            UIDialog.Show(chrome, "Held", "There are no held files.", new DialogChoice("Close"));
            return;
        }
        UIDialog dialog = UIDialog.Show(chrome, "Held", "", new DialogChoice("Submit all", () => PreviewLinkSubmit(folder, null)),
            new DialogChoice("Update all", () => PreviewLinkUpdate(folder, null)), new DialogChoice("Close"));
        ShowLinkHoldRows(dialog, held.Select(entry =>
            $"{entry.Path} · Held since {entry.HeldAt.ToLocalTime():g} · Others changed it: {(entry.HasIncoming ? "yes" : "no")}").ToArray());
    }

    private static void ShowLinkHoldRows(UIDialog dialog, IReadOnlyList<string> rows)
    {
        var scroll = dialog.Body.Add(new ScrollPanel { Size = new System.Numerics.Vector2(460, Math.Clamp(rows.Count * 44, 54, 300)) });
        var list = scroll.Add(new StackPanel { Fill = UIFill.Horizontal, Spacing = 6 });
        foreach (string item in rows)
        {
            Label line = list.Add(LinkLine(item));
            line.Dim = false;
            line.Tooltip = item;
        }
    }

    private static string LinkHoldOutcome(HexLinkHoldOutcome outcome) => outcome switch
    {
        HexLinkHoldOutcome.SendMine => "Will send",
        HexLinkHoldOutcome.TakeTheirs => "Will take incoming changes",
        HexLinkHoldOutcome.MergedClean => "Will merge cleanly",
        HexLinkHoldOutcome.DeleteConflict => "Delete conflict",
        HexLinkHoldOutcome.Binary => "Choose a file version",
        _ => "Needs merge editor",
    };
}
