using System.Diagnostics;
using System.Text;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private readonly Dictionary<Guid, HexLinkDraftStatus> privateDeviceStatus = [];
    private readonly HashSet<Guid> privateDeviceJobs = [];
    private readonly Dictionary<Guid, Process> privateDeviceEditors = [];

    private void DrawLinkPrivateWorktree(StackPanel holder, string folder, HexLinkFolder? provisioned = null)
    {
        if (linkServices is not { } services ||
            (provisioned ??
             services.Workspace.Folders.FirstOrDefault(item => PlatformPaths.Comparer.Equals(item.Name, folder))) is not { } shared)
            return;

        if (LinkPrivateStoragePath is not { } storage)
        {
            holder.Add(LinkLine(LINK_PRIVATE_STORAGE_UNAVAILABLE));
            return;
        }
        string path = Path.GetFullPath(shared.Path);
        if (PlatformPaths.Comparison != StringComparison.Ordinal)
            path = path.ToUpperInvariant();
        var id = new Guid(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("device\n" + path)).AsSpan(0, 16));
        string saved = Path.Combine(storage, "drafts", id.ToString("N"), "state.json");
        if (!File.Exists(saved))
        {
            holder.Add(LinkButton("Start private worktree", "git-branch", () => RunPrivateDeviceAction(id, async stop =>
            {
                HexLinkPrivateWorktree draft = await LinkStartPrivateWorktree(services, shared, id, stop);
                linkPrivateDrafts[id] = draft;
                await RefreshPrivateDeviceStatus(id, draft, stop);
            }))).Enabled = !privateDeviceJobs.Contains(id);
            return;
        }

        var buttons = holder.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 8, Fill = UIFill.Horizontal });
        string state = privateDeviceStatus.TryGetValue(id, out HexLinkDraftStatus? status)
            ? $"{status.Files} unpublished files ({status.Bytes:N0} bytes)"
            : "Private worktree";
        holder.Add(LinkLine(state));
        bool busy = privateDeviceJobs.Contains(id);
        bool ready = LinkPrivatePublishRoute(shared) is not null;
        buttons.Add(LinkButton("Open private editor", "folder-opened", () => RunPrivateDeviceAction(id, async stop =>
        {
            HexLinkPrivateWorktree draft = await LinkOpenStoredDraft(id, stop);
            await OnEditorForLink(() => OpenPrivateDeviceEditor(id, draft.Root)).WaitAsync(stop);
        }))).Enabled = !busy;
        buttons.Add(LinkButton("Refresh status", "refresh", () => RunPrivateDeviceAction(id, async stop =>
            await RefreshPrivateDeviceStatus(id, await LinkOpenStoredDraft(id, stop), stop)))).Enabled = !busy;

        bool hold = File.Exists(Path.Combine(Path.GetDirectoryName(saved)!, "hold-incoming"));
        buttons.Add(LinkButton(hold ? "Follow incoming files" : "Hold incoming files", "sync", () => RunPrivateDeviceAction(id, async stop =>
        {
            HexLinkPrivateWorktree draft = await LinkOpenStoredDraft(id, stop);
            draft.HoldIncoming = !hold;
            if (hold)
            {
                var tree = new HexLinkTree(shared, () => services.Excludes);
                await draft.Follow(await Task.Run(tree.Manifest, stop), stop);
            }
            await RefreshPrivateDeviceStatus(id, draft, stop);
        }))).Enabled = !busy;

        var publication = holder.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 8, Fill = UIFill.Horizontal });
        publication.Add(LinkButton("Publish", "cloud-upload", () => RunPrivateDeviceAction(id, stop => PublishDeviceDraft(id, false, stop))))
            .Enabled = !busy && ready;
        publication.Add(LinkButton("Publish anyway…", "warning", () =>
            UIDialog.Show(chrome, "Publish anyway", "Publish these private files without a passing build? The authority records the override.",
                new DialogChoice("Publish anyway", () => RunPrivateDeviceAction(id, stop => PublishDeviceDraft(id, true, stop)), Risky: true),
                new DialogChoice("Cancel", () => { }))))
            .Enabled = !busy && ready;
        publication.Add(LinkButton("Discard…", "trash", () =>
            UIDialog.Show(chrome, "Discard private worktree", "Discard all unpublished files in this private worktree?",
                new DialogChoice("Discard", () => RunPrivateDeviceAction(id, async stop =>
                {
                    if (await OnEditorForLink(() => privateDeviceEditors.TryGetValue(id, out Process? child) && !child.HasExited).WaitAsync(stop))
                        throw new IOException("Close the private editor window before discarding this worktree.");
                    McpToolResult result = await LinkDraftAction(id, "discard", false, null, stop);
                    PostLink(() =>
                    {
                        if (!result.IsError)
                            privateDeviceStatus.Remove(id);
                        Say(result.AllText(), error: result.IsError);
                    });
                }), Risky: true), new DialogChoice("Cancel", () => { }))))
            .Enabled = !busy;
        if (ready)
            holder.Add(LinkLine("The first publish runs a full build. Later publishes reuse the build checkout."));
        else
            holder.Add(LinkLine("Connect to this folder’s authority to publish."));
    }

    private async Task RefreshPrivateDeviceStatus(Guid id, HexLinkPrivateWorktree draft, CancellationToken cancel)
    {
        HexLinkDraftStatus status = await draft.Status(cancel);
        PostLink(() => privateDeviceStatus[id] = status);
    }

    private async Task PublishDeviceDraft(Guid id, bool bypass, CancellationToken stop)
    {
        McpToolResult result = await LinkDraftAction(id, "publish", bypass, null, stop);
        PostLink(() => Say(result.AllText(), error: result.IsError));
        await RefreshPrivateDeviceStatus(id, await LinkOpenStoredDraft(id, stop), stop);
    }

    private void RunPrivateDeviceAction(Guid id, Func<CancellationToken, Task> action)
    {
        if (!privateDeviceJobs.Add(id))
            return;
        linkDirty = true;
        CancellationToken cancel = linkCancel?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            try { await action(cancel); }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
            catch (Exception error) when (error is HexLinkException or IOException or UnauthorizedAccessException
                or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                PostLink(() => Say(error.Message, error: true));
            }
            finally
            {
                PostLink(() =>
                {
                    privateDeviceJobs.Remove(id);
                    linkDirty = true;
                    LinkMoved();
                });
            }
        });
    }

    private bool OpenPrivateDeviceEditor(Guid id, string root)
    {
        if (privateDeviceEditors.TryGetValue(id, out Process? old))
        {
            if (!old.HasExited)
                return true;
            old.Dispose();
            privateDeviceEditors.Remove(id);
        }
        string exe = System.Environment.ProcessPath ?? throw new IOException("The Hex executable is unavailable.");
        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        if (LinkTestRunByHost() is { } managed)
            start.ArgumentList.Add(managed);
        start.ArgumentList.Add(root);
        start.Environment["HEX_HOME"] = Path.Combine(LinkPrivateStorage, "editors", id.ToString("N"));
        start.Environment["HEX_REMOTE_PIPE"] = "hex-private-" + id.ToString("N");
        start.Environment.Remove("HEX_PERSISTENT");
        privateDeviceEditors[id] = Process.Start(start) ?? throw new IOException("The private editor did not start.");
        return true;
    }
}
