using System.Text;
using System.Text.Json.Nodes;
using Hex.Core.Git;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestLinkCrossWorktree(HexPointerScript test, Func<HexLinkPeer?> paired, string? dial, string work)
    {
        const string shared = "worktree-shared.txt", conflict = "worktree-conflict.txt";
        const string hostOnly = "worktree-host.txt", guestOnly = "worktree-guest.txt";
        const string ready = "worktree-ready.flag", dirty = "worktree-dirty.flag";
        const string offline = "worktree-offline.flag", resolved = "worktree-resolved.flag";
        const string checkedPullOnly = "worktree-pull-only.flag", confirmed = "worktree-confirmed.flag";
        const string baseline = "first line\nbase \u00e9\n", guestSave = "first line\nguest save \U0001f680\n";
        const string guestDraft = "first line\nunsaved guest \U0001f9ea\n", hostSave = "first line\nhost save \u6f22\n";
        const string hostEdit = "surviving host edit \u00df\n", hostOffline = "host offline \u6f22\n";
        const string guestOffline = "guest offline e\u0301\n", hostPull = "host pull-only update \U0001f680\n";
        const string denied = "guest write must stay local\n";
        string localEol = OperatingSystem.IsWindows() ? "\r\n" : "\n";
        string guestEol = localEol;
        string hostEol = localEol;

        string Full(string name) => Path.Combine(work, name);
        static string Wire(string name) => LINK_CROSS_FOLDER + "/" + name;
        static byte[] Bytes(string text, string lineEnd) => Encoding.UTF8.GetBytes(text.ReplaceLineEndings(lineEnd));
        bool Exact(string name, string text)
        {
            try
            {
                return HexLinkFiles.ReadShared(Full(name)).AsSpan().SequenceEqual(Bytes(text, localEol));
            }
            catch (IOException)
            {
                // The watcher can see the transfer before the destination can be opened.
                return false;
            }
        }
        void Write(string name, string text) => File.WriteAllBytes(Full(name), Bytes(text, localEol));
        void Wait(string label, Func<bool> readyCheck)
        {
            test.WaitFor(readyCheck, 45);
            test.Check("link-cross-worktree: " + label, readyCheck);
        }

        test.Run(() =>
        {
            BuildLinkCrossWorktree(work);
            foreach (string name in new[] { shared, conflict, hostOnly, guestOnly })
                Write(name, baseline);
            if (dial is null)
                File.WriteAllText(Full(ready), "ready");
        });

        if (dial is null)
        {
            HexLinkPeer? previous = null;
            Task<HexLinkFile>? pullOnlyChecked = null;
            Wait("guest editor save arrived with the host's exact line endings", () =>
                File.Exists(Full(dirty)) && Exact(shared, guestSave));
            test.Run(() => Write(shared, hostSave));
            Wait("guest reached the disconnect checkpoint", () => File.Exists(Full(offline)));
            test.Run(() =>
            {
                previous = paired();
                if (previous is not null) LinkDisconnect(previous.Device.Id);
                Write(conflict, hostEdit);
                Write(hostOnly, hostOffline);
            });
            Wait("reconnected guest delivered its offline file without changing the conflict", () =>
                previous is { IsClosed: true } && Exact(guestOnly, guestOffline) && Exact(conflict, hostEdit));
            Wait("guest explicitly resolved the conflict", () => File.Exists(Full(resolved)));
            test.Run(() =>
            {
                if (previous is not null) LinkSetRights(previous.Device.Id, HexLinkRights.View);
                Write(hostOnly, hostPull);
                if (LinkPeers.FirstOrDefault() is { } current)
                    pullOnlyChecked = ReadMarker(current, checkedPullOnly);
            });
            Wait("the denied write stays off the host disk until Edit is regranted", () =>
                pullOnlyChecked is { IsCompletedSuccessfully: true } &&
                Exact(conflict, hostEdit) && Exact(shared, hostSave) && Exact(guestOnly, guestOffline));
            test.Run(() =>
            {
                if (previous is not null)
                    LinkSetRights(previous.Device.Id, HexLinkRights.View | HexLinkRights.Edit);
            });
            Wait("regrant delivers the held guest edit without another save", () =>
                Exact(guestOnly, denied) && File.Exists(Full(confirmed)));
            test.WaitFor(() => LinkPeers.Count == 0, 10);
            return;
        }

        HexLinkPeer? first = null;
        Task<HexLinkFile>? hostReady = null;
        Task<string?>? starting = null, resolution = null;
        Task? saved = null, offlineNotice = null, resolutionNotice = null, deniedWrite = null;
        Task<HexLinkPeer>? reconnect = null;
        Task<HexLinkFile>? remoteConflict = null, regrantedFile = null;
        Task<bool>? conflictReport = null, pullOnlyReport = null;
        HexDocument? tab = null;
        Exception? rejection = null;
        string? Device() => first?.Device.Id;
        HexLinkSync? Sync() => Device() is { } id ? LinkSyncOf(id, LINK_CROSS_FOLDER) : null;

        test.Run(() =>
        {
            first = paired();
            if (first is null) return;
            hostEol = first.Device.Os == "windows" ? "\r\n" : "\n";
            hostReady = ReadMarker(first, ready);
        });
        Wait("listener announced its prepared repository", () => hostReady is { IsCompletedSuccessfully: true });
        test.Run(() =>
        {
            if (Device() is { } id)
                starting = LinkSyncStart(id, LINK_CROSS_FOLDER);
        });
        Wait("same commit with CRLF/LF checks out without rewriting either side", () =>
            starting is { IsCompletedSuccessfully: true, Result: null } &&
            Sync() is { State: HexLinkSyncState.Idle, Sent: 0, Received: 0 } && Exact(shared, baseline));
        test.Run(() =>
        {
            Open(Full(shared));
            tab = document;
            Rewrite(tab, guestSave);
            Save();
            if (Sync() is { } active && first is not null)
                saved = FlushAndSignal(active, first, dirty);
            Rewrite(tab, guestDraft);
        });
        Wait("flush returns after host received the exact saved bytes", () => saved is { IsCompletedSuccessfully: true });
        Wait("incoming host write uses the guest's exact line endings", () =>
            tab?.Editor.Text == hostSave && Exact(shared, hostSave));
        test.Run(() => tab?.Editor.Document.Undo());
        test.Check("link-cross-worktree: one Undo recovers the unsaved draft without rewriting disk", () =>
            tab?.Editor.Text == guestDraft && Exact(shared, hostSave));
        test.Run(() =>
        {
            if (first is null) return;
            linkHeldOff.Add(first.Device.Id);
            offlineNotice = first.Write(Wire(offline), null, "ready"u8.ToArray());
        });
        Wait("link closes after the host sees the offline checkpoint", () => first is { IsClosed: true });
        test.Run(() =>
        {
            File.Delete(Full(conflict));
            Write(guestOnly, guestOffline);
            if (Device() is { } id && TryLinkAddress(dial, HexLinkTransport.DEFAULT_PORT, out string host, out int port))
                reconnect = LinkConnect(host, port, id);
        });
        Wait("remembered sync resumes after trusted reconnect and delivers offline edits", () =>
            reconnect is { IsCompletedSuccessfully: true } && Sync() is { State: HexLinkSyncState.Idle } &&
            Exact(hostOnly, hostOffline) && Exact(guestOnly, guestOffline) &&
            LinkConflicts.Any(item => item.Path == Wire(conflict) && item.Kind == "deleted here, changed there"));
        test.Run(() =>
        {
            if (reconnect is { IsCompletedSuccessfully: true })
                remoteConflict = reconnect.Result.Read(Wire(conflict));
        });
        Wait("delete-versus-edit conflict leaves both sides in their original state", () =>
            !File.Exists(Full(conflict)) && remoteConflict is { IsCompletedSuccessfully: true } &&
            remoteConflict.Result.Bytes.AsSpan().SequenceEqual(Bytes(hostEdit, hostEol)));
        test.Run(() => conflictReport = BothActionsReport("Some files have not synced."));
        Wait("sync start and flush report unresolved files", () =>
            conflictReport is { IsCompletedSuccessfully: true, Result: true });
        test.Run(() =>
        {
            if (Device() is { } id)
                resolution = LinkResolve(id, Wire(conflict), HexLinkSyncKeep.Theirs);
        });
        Wait("explicitly taking theirs restores exact bytes and clears the conflict", () =>
            resolution is { IsCompletedSuccessfully: true, Result: null } && Exact(conflict, hostEdit) &&
            LinkConflicts.All(item => item.Path != Wire(conflict)));
        test.Run(() =>
        {
            if (reconnect is { IsCompletedSuccessfully: true })
                resolutionNotice = reconnect.Result.Write(Wire(resolved), null, "ready"u8.ToArray());
        });
        Wait("View-only sync still pulls the host file", () =>
            reconnect is { IsCompletedSuccessfully: true, Result.Granted: HexLinkRights.View } &&
            Sync()?.PullOnly == true && Exact(hostOnly, hostPull));
        test.Run(() =>
        {
            Write(guestOnly, denied);
            if (reconnect is { IsCompletedSuccessfully: true })
                deniedWrite = RefusedWrite(reconnect.Result);
        });
        Wait("View-only write is refused and the local draft stays on disk", () =>
            deniedWrite is { IsCompletedSuccessfully: true } &&
            rejection is HexLinkException { Code: HexLinkErrors.NOT_ALLOWED } && Exact(guestOnly, denied));
        test.Run(() => pullOnlyReport = BothActionsReport("Pull-only: changes saved here stay here."));
        Wait("sync start and flush report local changes stay here in pull-only mode", () =>
            pullOnlyReport is { IsCompletedSuccessfully: true, Result: true });
        test.Run(() => File.WriteAllText(Full(checkedPullOnly), "checked"));
        Wait("regrant returns the live sync to read-write", () =>
            reconnect is { IsCompletedSuccessfully: true, Result.Granted: HexLinkRights.View | HexLinkRights.Edit }
            && Sync() is { State: HexLinkSyncState.Idle, PullOnly: false });
        test.Run(() =>
        {
            if (reconnect is { IsCompletedSuccessfully: true })
                regrantedFile = reconnect.Result.Read(Wire(guestOnly));
        });
        Wait("previously held edit reaches the host with exact bytes", () =>
            regrantedFile is { IsCompletedSuccessfully: true } &&
            regrantedFile.Result.Bytes.AsSpan().SequenceEqual(Bytes(denied, hostEol)));
        test.Run(() =>
        {
            if (reconnect is { IsCompletedSuccessfully: true })
                resolutionNotice = reconnect.Result.Write(Wire(confirmed), null, "ready"u8.ToArray());
        });
        Wait("host received the final acknowledgement", () => resolutionNotice is { IsCompletedSuccessfully: true });

        async Task<bool> BothActionsReport(string expected)
        {
            if (Device() is not { } id) return false;
            foreach (string action in new[] { "start", "flush" })
            {
                var result = await McpTools.Call("hex_link_sync", new JsonObject
                {
                    ["device"] = id, ["folder"] = LINK_CROSS_FOLDER, ["action"] = action
                }, CancellationToken.None).ConfigureAwait(false);
                string said = result.AllText();
                Console.WriteLine($"[Hex self-test] link-cross-worktree: {action}: {said}");
                if (result.IsError || !said.Contains(expected, StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        async Task<HexLinkFile> ReadMarker(HexLinkPeer peer, string marker)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            while (true)
            {
                try { return await peer.Read(Wire(marker), stop.Token).ConfigureAwait(false); }
                catch (HexLinkException error) when (error.Code == HexLinkErrors.NOT_FOUND)
                { await Task.Delay(250, stop.Token).ConfigureAwait(false); }
            }
        }

        async Task FlushAndSignal(HexLinkSync sync, HexLinkPeer peer, string marker)
        {
            await sync.Flush().ConfigureAwait(false);
            var received = await peer.Read(Wire(shared)).ConfigureAwait(false);
            if (!received.Bytes.AsSpan().SequenceEqual(Bytes(guestSave, hostEol)) || !Exact(shared, guestSave))
                throw new IOException("The saved bytes did not survive the flush.");
            await peer.Write(Wire(marker), null, "ready"u8.ToArray()).ConfigureAwait(false);
        }

        async Task RefusedWrite(HexLinkPeer peer)
        {
            try { await peer.Write(Wire(guestOnly), null, Bytes(denied, guestEol)).ConfigureAwait(false); }
            catch (HexLinkException error) { rejection = error; }
        }
    }

    private static void BuildLinkCrossWorktree(string work)
    {
        var time = new Dictionary<string, string>
        {
            ["GIT_AUTHOR_DATE"] = "2026-01-01T00:00:00+00:00",
            ["GIT_COMMITTER_DATE"] = "2026-01-01T00:00:00+00:00"
        };
        Git("init", "-q", "-b", "main", "--object-format=sha1");
        Git("config", "core.autocrlf", "false");
        File.WriteAllText(Path.Combine(work, ".gitignore"), "*.flag\n");
        File.WriteAllText(Path.Combine(work, ".gitattributes"), "*.txt text\n");
        foreach (string name in new[] { "worktree-shared.txt", "worktree-conflict.txt", "worktree-host.txt", "worktree-guest.txt" })
            File.WriteAllText(Path.Combine(work, name), "first line\nbase \u00e9\n");
        Git("add", "--", ".gitignore", ".gitattributes", "worktree-shared.txt", "worktree-conflict.txt", "worktree-host.txt", "worktree-guest.txt");
        Git("-c", "user.name=Hex Link QA", "-c", "user.email=link-qa@example.invalid", "-c", "commit.gpgSign=false",
            "-c", "core.hooksPath=.git/no-hooks", "commit", "-qm", "Paired sync fixture");

        void Git(params string[] arguments)
        {
            if (GitProcess.Run(work, arguments, environment: time) is { Ok: false } failure)
                throw new IOException($"Git {arguments[0]} failed in the paired sync fixture: {failure.Why}");
        }
    }
}
