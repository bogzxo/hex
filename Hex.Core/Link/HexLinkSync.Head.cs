using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Hex.Core;

internal sealed partial class HexLinkSync
{
    private sealed record HeadRepository(string Prefix, string? Head);
    private HeadRepository[] headRepositories = [];

    private void CompareHeads(HexLinkManifest mine, HexLinkManifest theirs)
    {
        string? Same(string? here, string? there) =>
            here is not null && here.Equals(there, StringComparison.OrdinalIgnoreCase) ? here : null;
        headRepositories = [.. RepositoryPaths(mine, theirs).OrderByDescending(path => path.Length)
            .Select(path => new HeadRepository(path.Length == 0 ? path : path + "/", Same(HeadAt(mine, path), HeadAt(theirs, path))))];
    }

    // A shared checked-out commit is an initial base only for paths tracked by that exact repository.
    private async Task<bool> ReconcileFromHead(string wire, string full, HexLinkTreeFile? mine, HexLinkTreeFile? theirs,
        byte[]? localBytes, CancellationToken stop)
    {
        if (!remoteCertified)
            return false;
        string relative = wire[(folder.Name.Length + 1)..];
        HeadRepository? repo = headRepositories.FirstOrDefault(scope => relative.StartsWith(scope.Prefix, StringComparison.Ordinal));
        if (repo is null)
            return false;
        string? head = followBase.GetValueOrDefault(repo.Prefix.TrimEnd('/')) ?? repo.Head;
        if (head is null)
            return false;
        byte[]? baseline = await ReadHeadBlob(Path.Combine(tree.Root, repo.Prefix), head, relative[repo.Prefix.Length..], stop);
        if (baseline is null)
            return false;

        string hash = HexLinkFiles.Hash(baseline);
        string? text = TextOf(baseline).Norm;
        bool localClean = mine is not null && (mine.Hash == hash || (text is not null && mine.Text == text));
        bool remoteClean = theirs is not null && (theirs.Hash == hash || (text is not null && theirs.Text == text));
        if (localClean && remoteClean)
        {
            lock (gate)
                SetBase(wire, new HexLinkSyncAgreed(mine!.Hash!, theirs!.Hash!, text, EolIn(mine.Eol), EolIn(theirs.Eol)));
            return true;
        }

        if (localClean)
        {
            await Pull(wire, full, mine!.Hash, theirs, stop);
            return true;
        }
        if (remoteClean)
        {
            await Push(wire, full, null, localBytes, mine?.Hash, theirs!.Hash, stop);
            return true;
        }

        if (localBytes is null || theirs?.Hash is not { } expected)
        {
            await Conflict(wire, full, mine?.Hash, theirs?.Hash, stop);
            return true;
        }
        if (pullOnly)
            return false;
        HexLinkFile remoteFile = await Fetch(wire, stop);
        if (remoteFile.Hash != expected)
        {
            Again(wire);
            return true;
        }

        HexLinkMergeResult? merge = await HexLinkMerge.TryTextAsync(baseline, localBytes, remoteFile.Bytes, stop);
        if (merge is null)
            return false;
        if (!merge.Clean)
        {
            await HoldEditorConflict(wire, full, merge.Bytes, "overlapping changes from the shared commit", stop);
            return true;
        }

        byte[] hereBytes = merge.Bytes;
        byte[] thereBytes = Converted(hereBytes, TextOf(hereBytes), TextOf(remoteFile.Bytes).Eol);
        string hereHash = HexLinkFiles.Hash(hereBytes), thereHash = HexLinkFiles.Hash(thereBytes);
        if (hereHash != mine!.Hash)
        {
            try
            {
                await ApplyLocal(full, wire, mine.Hash, hereBytes, stop, null);
            }
            catch (HexLinkException error) when (EditorDirty(error))
            {
                await HoldEditorConflict(wire, full, remoteFile.Bytes, "unsaved changes in the editor", stop);
                return true;
            }
            Interlocked.Increment(ref received);
        }
        if (thereHash != expected)
        {
            try
            {
                await Asked(() => peer.Write(wire, expected, thereBytes, stop), stop);
            }
            catch (HexLinkException error) when (EditorDirty(error))
            {
                await HoldEditorConflict(wire, full, remoteFile.Bytes, "unsaved changes on the other device", stop);
                return true;
            }
            Interlocked.Increment(ref sent);
        }
        lock (gate)
            SetBase(wire, AgreedOf(hereBytes, thereBytes));
        Notify();
        return true;
    }

    internal static async Task<byte[]?> ReadHeadBlob(string repository, string head, string path, CancellationToken cancel,
        long maximum = HexLinkFiles.MOST_READ)
    {
        // Batch mode distinguishes an untracked path from an actual Git failure, and returns raw bytes.
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = repository, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("cat-file");
        start.ArgumentList.Add("--batch");
        start.Environment["LC_ALL"] = "C";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        CancellationToken stop = timeout.Token;
        using Process git = Process.Start(start) ?? throw new IOException("Git did not start.");
        Task<string> errors = git.StandardError.ReadToEndAsync(stop);
        try
        {
            await using (Stream input = git.StandardInput.BaseStream)
                await input.WriteAsync(Encoding.UTF8.GetBytes($"{head}:{path}\n"), stop);

            Stream output = git.StandardOutput.BaseStream;
            var header = new StringBuilder();
            var one = new byte[1];
            while (await output.ReadAsync(one, stop) == 1 && one[0] != '\n')
            {
                if (header.Length >= HexLinkFiles.MOST_PATH + 128)
                    throw new IOException("Git sent an invalid blob header.");
                header.Append((char)one[0]);
            }
            string description = header.ToString();
            if (description.EndsWith(" missing", StringComparison.Ordinal))
            {
                await git.WaitForExitAsync(stop);
                if (git.ExitCode != 0)
                    throw new IOException(await errors);
                return null;
            }

            string[] parts = description.Split(' ');
            if (parts.Length != 3 || !HexLinkGit.IsCommitId(parts[0]) ||
                !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int size))
                throw new IOException($"Git could not read {path} from the shared commit.");
            if (parts[1] != "blob")
                return null;
            if (size > maximum)
                throw new HexLinkException(HexLinkErrors.TOO_BIG, $"{path} is too large to compare with the shared commit.");

            byte[] blob = new byte[size];
            await output.ReadExactlyAsync(blob, stop);
            await output.ReadExactlyAsync(one, stop);
            await git.WaitForExitAsync(stop);
            if (git.ExitCode != 0 || one[0] != '\n')
                throw new IOException($"Git could not read {path} from the shared commit: {await errors}");
            return blob;
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new IOException("Git timed out reading the shared commit.");
        }
        finally
        {
            if (!git.HasExited)
            {
                try { git.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            }
        }
    }
}
