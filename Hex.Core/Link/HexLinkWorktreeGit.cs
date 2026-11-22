using System.Diagnostics;
using System.Text;
using Hex.Core.Git;

namespace Hex.Core;

internal sealed record HexLinkWorktreeFile(string Path, bool Executable = false);

/// <summary>Objects, indexes and refs owned by Link, separate from the shared repository.</summary>
internal sealed class HexLinkWorktreeGit
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private const string Published = "refs/hexlink/published";
    private readonly string repository;

    private HexLinkWorktreeGit(string repository) => this.repository = repository;

    internal string DirectoryPath => repository;

    public static async Task<HexLinkWorktreeGit> Open(string path, CancellationToken cancel)
    {
        if (!GitProcess.Enabled)
            throw new IOException(GitProcess.DisabledReason);
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(full);
        var git = new HexLinkWorktreeGit(full);
        if (!File.Exists(Path.Combine(full, "HEAD")))
            await git.Need(["init", "--bare", "--quiet", "--object-format=sha1", "."], cancel);
        return git;
    }

    public async Task<string> Capture(string source, IReadOnlyList<HexLinkWorktreeFile> files, CancellationToken cancel)
    {
        string root = Path.GetFullPath(source);
        var folder = new HexLinkFolder("work", root);
        var paths = new HashSet<string>(PlatformPaths.Comparer);
        var sources = new StringBuilder();
        foreach (HexLinkWorktreeFile file in files)
        {
            if (!paths.Add(file.Path) || file.Path.Any(char.IsControl))
                throw new IOException($"Invalid or duplicated snapshot path: {file.Path}");
            string full = HexLinkFiles.Resolve([folder], "work/" + file.Path);
            string literal = Path.GetFullPath(Path.Combine(root, file.Path));
            if (!PlatformPaths.Same(literal, full) || (File.GetAttributes(literal) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new IOException($"Snapshot path {file.Path} is not a regular file.");
            sources.Append(full).Append('\n');
        }

        // One git fast-import writes every file into a single pack with the tree and commit, rather than hash-object
        // writing a loose object a file and four more processes making the tree: 1,677 files (37 MB) took 10-12 s as
        // loose objects and 1.6-2 s as one pack, the same tree. Light compression, no deltas: the pack is local and
        // short-lived, the time is in zlib
        string capture = $"refs/hexlink/capture/{Guid.NewGuid():N}";
        string[] resolved = [.. sources.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)];
        GitOutput imported = await Run(["-c", "core.compression=1", "fast-import", "--quiet", "--depth=0"], cancel,
            stream: input => WriteImport(input, resolved, files, capture, cancel));
        if (imported.Exit != 0)
            throw new IOException($"Git fast-import failed: {imported.Error}");

        string commit = CheckedObject(await Need(["rev-parse", "--verify", capture + "^{commit}"], cancel));
        await Need(["update-ref", "--stdin"], cancel, $"start\nupdate refs/hexlink/snapshots/{commit} {commit}\ndelete {capture} {commit}\nprepare\ncommit\n");
        return commit;
    }

    /// <summary>The fast-import stream of a snapshot: each file's bytes as read now, then one commit of them all.</summary>
    private static async Task WriteImport(Stream input, string[] full, IReadOnlyList<HexLinkWorktreeFile> files, string capture, CancellationToken cancel)
    {
        await using var output = new BufferedStream(input, 1 << 16);
        await output.WriteAsync(Utf8.GetBytes("feature done\n"), cancel);
        for (int i = 0; i < files.Count; i++)
        {
            byte[] data = await File.ReadAllBytesAsync(full[i], cancel);
            await output.WriteAsync(Utf8.GetBytes($"blob\nmark :{i + 1}\ndata {data.Length}\n"), cancel);
            await output.WriteAsync(data, cancel);
            output.WriteByte((byte)'\n');
        }

        var commit = new StringBuilder();
        commit.Append($"commit {capture}\ncommitter Hex Link <hex-link@localhost> {DateTimeOffset.UtcNow.ToUnixTimeSeconds()} +0000\n");
        byte[] message = Utf8.GetBytes("Hex Link snapshot");
        commit.Append($"data {message.Length}\nHex Link snapshot\n");
        for (int i = 0; i < files.Count; i++)
            commit.Append(files[i].Executable ? "M 100755 :" : "M 100644 :").Append(i + 1).Append(' ').Append(Quoted(files[i].Path)).Append('\n');
        commit.Append("\ndone\n");
        await output.WriteAsync(Utf8.GetBytes(commit.ToString()), cancel);
        await output.FlushAsync(cancel);
    }

    /// <summary>A path as fast-import reads a quoted one, the backslash and the quote escaped (control characters are refused before).</summary>
    private static string Quoted(string path) => "\"" + path.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    internal bool NeedsSnapshotMaintenance()
    {
        string refs = Path.Combine(repository, "refs", "hexlink", "snapshots");
        if (Directory.Exists(refs) && Directory.EnumerateFiles(refs).Take(32).Count() == 32)
            return true;
        var packs = new DirectoryInfo(Path.Combine(repository, "objects", "pack"));
        if (!packs.Exists)
            return false;
        FileInfo[] files = [.. packs.EnumerateFiles("*.pack")];
        return files.Length >= 16 || (files.Length > 1 && files.Sum(file => file.Length) >= 64L * 1024 * 1024);
    }

    internal async Task PruneSnapshots(IReadOnlySet<string> keep, CancellationToken cancel)
    {
        string refs = await Need(["for-each-ref", "--format=%(refname) %(objectname)",
            "refs/hexlink/snapshots/", "refs/hexlink/capture/"], cancel);
        var commands = new StringBuilder();
        foreach (string row in refs.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pair = row.Trim().Split(' ');
            if (pair.Length != 2)
                throw new IOException("Git returned an invalid private snapshot reference.");
            if (!keep.Contains(CheckedObject(pair[1])))
                commands.Append("delete ").Append(pair[0]).Append(' ').Append(pair[1]).Append('\n');
        }
        if (commands.Length != 0)
            await Need(["update-ref", "--stdin"], cancel, commands.ToString());
        await Need(["gc", "--quiet", "--prune=now"], cancel);
    }

    public Task<string> Checkout(string commit, string path, CancellationToken cancel) =>
        Need(["worktree", "add", "--detach", "--quiet", Path.GetFullPath(path), CheckedObject(commit)], cancel);

    public Task<string> Remove(string path, CancellationToken cancel) =>
        Need(["worktree", "remove", "--force", Path.GetFullPath(path)], cancel);

    public async Task<byte[]> ReadFile(string commit, string path, CancellationToken cancel)
    {
        GitOutput result = await Run(["show", $"{CheckedObject(commit)}:{path}"], cancel);
        return result.Exit == 0 ? result.Bytes : throw new IOException(result.Error);
    }

    public Task<string?> Tip(CancellationToken cancel) => ReadRef(Published, cancel);
    public Task<string?> Receipt(Guid id, CancellationToken cancel) => ReadRef(ReceiptRef(id), cancel);

    private async Task<string?> ReadRef(string name, CancellationToken cancel)
    {
        GitOutput result = await Run(["rev-parse", "--verify", "--quiet", name], cancel);
        return result.Exit switch
        {
            0 => CheckedObject(result.Text),
            1 => null,
            _ => throw new IOException(result.Error)
        };
    }

    public async Task CommitPublish(Guid id, string commit, string? expected, CancellationToken cancel)
    {
        string value = CheckedObject(commit), before = expected is null ? new string('0', 40) : CheckedObject(expected);
        string input = $"start\nupdate {Published} {value} {before}\ncreate {ReceiptRef(id)} {value}\nprepare\ncommit\n";
        GitOutput result = await Run(["update-ref", "--stdin"], cancel, input);
        if (result.Exit != 0)
            throw new HexLinkException(HexLinkErrors.CONFLICT, $"The shared publication changed before commit. {result.Error}");
    }

    private static string ReceiptRef(Guid id) => $"refs/hexlink/receipts/{id:N}";

    private static string CheckedObject(string value) => HexLinkGit.IsCommitId(value)
        ? value : throw new IOException("Git returned an invalid object name.");

    private async Task<string> Need(string[] args, CancellationToken cancel, string? input = null, string? index = null)
    {
        GitOutput result = await Run(args, cancel, input, index);
        return result.Exit == 0
            ? result.Text
            : throw new IOException($"Git {args[0]} failed: {result.Error}");
    }

    private sealed record GitOutput(int Exit, byte[] Bytes, string Error)
    {
        public string Text => Utf8.GetString(Bytes).Trim();
    }

    private async Task<GitOutput> Run(string[] args, CancellationToken cancel, string? input = null, string? index = null,
        Func<Stream, Task>? stream = null)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = repository,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input is not null || stream is not null,
            StandardErrorEncoding = Utf8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.Environment.Remove("GIT_DIR");
        start.Environment.Remove("GIT_WORK_TREE");
        start.Environment.Remove("GIT_COMMON_DIR");
        start.Environment.Remove("GIT_OBJECT_DIRECTORY");
        start.Environment.Remove("GIT_ALTERNATE_OBJECT_DIRECTORIES");
        start.Environment.Remove("GIT_INDEX_FILE");
        start.Environment.Remove("GIT_NAMESPACE");
        start.Environment.Remove("GIT_CONFIG_COUNT");
        start.Environment.Remove("GIT_CONFIG_PARAMETERS");
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        start.Environment["GIT_AUTHOR_NAME"] = start.Environment["GIT_COMMITTER_NAME"] = "Hex Link";
        start.Environment["GIT_AUTHOR_EMAIL"] = start.Environment["GIT_COMMITTER_EMAIL"] = "hex-link@localhost";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (index is not null)
            start.Environment["GIT_INDEX_FILE"] = Path.GetFileName(index);
        if (args[0] != "init")
            start.ArgumentList.Add("--git-dir=.");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("core.autocrlf=false");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("core.longpaths=true");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("gc.auto=0");
        foreach (string argument in args)
            start.ArgumentList.Add(argument);

        cancel.ThrowIfCancellationRequested();
        using Process process = GitProcess.StartEnabled(start)
            ?? throw new IOException(GitProcess.Enabled ? "Git did not start." : GitProcess.DisabledReason);
        using var bytes = new MemoryStream();
        Task output = process.StandardOutput.BaseStream.CopyToAsync(bytes, cancel);
        Task<string> error = process.StandardError.ReadToEndAsync(cancel);
        try
        {
            if (input is not null)
            {
                await using Stream inputStream = process.StandardInput.BaseStream;
                await inputStream.WriteAsync(Utf8.GetBytes(input), cancel);
            }
            else if (stream is not null)
            {
                await using Stream inputStream = process.StandardInput.BaseStream;
                await stream(inputStream);
            }

            await Task.WhenAll(process.WaitForExitAsync(cancel), output, error);
            return new GitOutput(process.ExitCode, bytes.ToArray(), (await error).Trim());
        }
        catch
        {
            try
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception stopping) when (stopping is InvalidOperationException or TimeoutException) { }
            throw;
        }
    }
}
