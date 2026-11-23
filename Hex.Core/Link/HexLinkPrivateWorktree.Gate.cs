namespace Hex.Core;

internal sealed partial class HexLinkPrivateWorktree
{
    private sealed class GateCandidateChanged(IEnumerable<string> paths)
        : IOException("Publish inputs changed: " + string.Join(", ", paths.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) + ".");

    private async Task<HexLinkWorktreeGateResult> Gate(HexLinkDraftRoute route, HexLinkManifest snapshot, HexLinkDraftPlan plan,
        Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<HexLinkWorktreeGateOptions>>> gateFor,
        CancellationToken cancel)
    {
        await operation.WaitAsync(cancel);
        try
        {
            if (plan.Conflicts.Count != 0)
                throw new InvalidOperationException("Settle private worktree conflicts before building.");
            if (!repositoryLayout.SequenceEqual(HexLinkWorktreeScope.Layout(snapshot.NestedHeads), PlatformPaths.Comparer))
                throw new IOException("The shared repository layout changed after this worktree was opened.");

            foreach (Repository repo in repositories)
            {
                string path = Path.Combine(GateRoot, repo.Path);
                if (!File.Exists(Path.Combine(path, ".git")))
                    await repo.Git.Checkout(repo.Commit, path, cancel);
            }

            var gate = shared with { Path = GateRoot };
            var gateTree = new HexLinkTree(gate, excludes, streamLarge: true);
            var authority = await Versions(shared, snapshot, cancel, IsMaterialized, skipUnavailable: true);
            var expected = new Dictionary<string, FileVersion>(authority, StringComparer.Ordinal);
            var edited = plan.Edits.ToDictionary(edit => edit.Path, StringComparer.Ordinal);
            var current = await GateVersions(gateTree, cancel);
            foreach (HexLinkPublishEdit edit in plan.Edits)
            {
                string to = HexLinkFiles.Resolve([gate], edit.Path);
                if (edit.Bytes is { } bytes)
                {
                    expected[edit.Path] = new FileVersion(HexLinkFiles.Hash(bytes), bytes.LongLength, HexLinkSync.TextAndEol(bytes).Text);
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    await File.WriteAllBytesAsync(to, bytes, cancel);
                }
                else
                {
                    expected.Remove(edit.Path);
                    File.Delete(to);
                }
            }

            // Stage the local working files before resolving the build so added projects and imports are visible.
            // Authority equality is checked below only where these files are inputs to the selected gate.
            foreach (string gone in current.Keys.Where(path => !expected.ContainsKey(path) && !edited.ContainsKey(path)))
            {
                try { File.Delete(HexLinkFiles.Resolve([gate], gone)); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // Scoped verification will report this stale path if the build can read it.
                }
            }
            foreach (var (wire, version) in authority)
            {
                cancel.ThrowIfCancellationRequested();
                if (edited.ContainsKey(wire) || version.SameContent(current.GetValueOrDefault(wire)))
                    continue;
                string to = HexLinkFiles.Resolve([gate], wire);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    File.Copy(HexLinkFiles.Resolve([shared], wire), to, overwrite: true);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // A missing or locked replica file is repaired from the authority if the gate needs it.
                }
            }

            var options = await gateFor([.. edited.Keys], cancel);
            var includes = HexLinkWorktreeGateInputs.For(gate, options);
            bool Input(string wire) => edited.ContainsKey(wire) || includes(Relative(gate, wire));
            for (int pass = 0; pass < 2; pass++)
            {
                RequireAvailable(snapshot, Input);
                var staged = await GateVersions(gateTree, cancel, Input);
                string[] repairs = Differences(expected, staged, Input);
                if (repairs.Length == 0)
                    break;
                foreach (string wire in repairs)
                {
                    if (edited.ContainsKey(wire) || !expected.TryGetValue(wire, out var version))
                        continue;
                    byte[] bytes = await ReadGateAuthority(wire, version, route.ReadAuthority, cancel);
                    string to = HexLinkFiles.Resolve([gate], wire);
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    await File.WriteAllBytesAsync(to, bytes, cancel);
                }
                // A refreshed project or import can bring more paths into the build's scope.
                includes = HexLinkWorktreeGateInputs.For(gate, options);
            }
            RequireAvailable(snapshot, Input);
            await VerifyGate(gateTree, expected, Input, cancel);

            HexLinkWorktreeGateResult result = await HexLinkWorktreeGate.Run(GateRoot, Path.Combine(data, "gate.log"), options, cancel);
            if (result.Passed)
            {
                await VerifyGate(gateTree, expected, Input, cancel);
                HexLinkManifest latest = await route.Snapshot(cancel);
                RequireAvailable(latest, Input);
                var fresh = await Versions(shared, latest, cancel, wire => Input(wire) && IsMaterialized(wire));
                string[] changed = Differences(authority, fresh, Input);
                if (changed.Length != 0)
                    throw new GateCandidateChanged(changed);
            }
            return result;
        }
        finally
        {
            operation.Release();
        }
    }

    private async Task<byte[]> ReadGateAuthority(string wire, FileVersion version,
        Func<string, CancellationToken, Task<byte[]>>? readAuthority, CancellationToken cancel)
    {
        RequirePublishSize(wire, version.Size);
        try
        {
            byte[] content = readAuthority is null
                ? await HexLinkFiles.ReadSharedAsync(HexLinkFiles.Resolve([shared], wire), cancel)
                : await readAuthority(wire, cancel);
            if (HexLinkFiles.Hash(content) == version.Hash)
                return content;
        }
        catch (Exception error) when (error is IOException ||
            error is HexLinkException { Code: HexLinkErrors.NOT_FOUND or HexLinkErrors.CONFLICT })
        {
            // The snapshot raced a write or rename. Let the bounded publish loop take another snapshot.
        }
        throw new GateCandidateChanged([wire]);
    }

    private static string[] Differences(Dictionary<string, FileVersion> expected, Dictionary<string, FileVersion> scanned,
        Func<string, bool> include) =>
        [.. expected.Keys.Union(scanned.Keys, StringComparer.Ordinal).Where(path => include(path) &&
            (expected.TryGetValue(path, out var version) ? !version.SameContent(scanned.GetValueOrDefault(path)) : scanned.ContainsKey(path)))];

    private async Task<Dictionary<string, FileVersion>> GateVersions(HexLinkTree tree, CancellationToken cancel, Func<string, bool>? include = null)
    {
        HexLinkManifest snapshot = await Task.Run(() => include is null ? tree.Manifest(verify: true) :
            tree.PrivateManifest(_ => path => include(tree.Folder.Name + "/" + path), cancel, verify: true), cancel);
        if (include is not null)
            RequireAvailable(snapshot, include);
        return await Versions(tree.Folder, snapshot, cancel, include, skipUnavailable: include is null);
    }

    private static void RequireAvailable(HexLinkManifest snapshot, Func<string, bool> include)
    {
        string[] unavailable = [.. snapshot.Files.Where(file => file.Unavailable && include(file.Path)).Select(file => file.Path)];
        if (unavailable.Length != 0)
            throw new GateCandidateChanged(unavailable);
    }

    private async Task VerifyGate(HexLinkTree tree, Dictionary<string, FileVersion> expected, Func<string, bool> include, CancellationToken cancel)
    {
        var scanned = await GateVersions(tree, cancel, include);
        string[] changed = Differences(expected, scanned, include);
        if (changed.Length != 0)
            throw new GateCandidateChanged(changed);
    }
}
