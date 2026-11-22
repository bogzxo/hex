using System.Diagnostics;
using System.Text;

namespace Hex.Core;

internal sealed record HexLinkWorktreeFile(string Path, bool Executable = false);

/// <summary>Objects, indexes and refs owned by Link, separate from the shared repository.</summary>
internal sealed class HexLinkWorktreeGit
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private const string Published = "refs/hexlink/published";
    private readonly string repository;

    private HexLinkWorktreeGit(string repository) => this.repository = repository;

    public static async Task<HexLinkWorktreeGit> Open(string path, CancellationToken cancel)
    {
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

        string[] blobs = files.Count == 0 ? [] :
            (await Need(["hash-object", "-w", "--no-filters", "--stdin-paths"], cancel, sources.ToString())).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (blobs.Length != files.Count || blobs.Any(blob => !HexLinkGit.IsCommitId(blob)))
            throw new IOException("Git did not return all the snapshot blobs.");

        string index = Path.Combine(repository, $"hex-index-{Guid.NewGuid():N}");
        try
        {
            await Need(["read-tree", "--empty"], cancel, index: index);
            if (files.Count > 0)
            {
                var entries = new StringBuilder();
                for (int i = 0; i < files.Count; i++)
                    entries.Append(files[i].Executable ? "100755 " : "100644 ").Append(blobs[i]).Append('\t').Append(files[i].Path).Append('\0');
                await Need(["update-index", "-z", "--index-info"], cancel, entries.ToString(), index);
            }

            string tree = await Need(["write-tree"], cancel, index: index);
            string commit = await Need(["commit-tree", tree, "-m", "Hex Link snapshot"], cancel);
            await Need(["update-ref", "refs/hexlink/snapshots/" + commit, commit], cancel);
            return commit;
        }
        finally
        {
            File.Delete(index);
        }
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

    private async Task<GitOutput> Run(string[] args, CancellationToken cancel, string? input = null, string? index = null)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = repository,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input is not null,
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
        foreach (string argument in args)
            start.ArgumentList.Add(argument);

        cancel.ThrowIfCancellationRequested();
        using Process process = Process.Start(start) ?? throw new IOException("Git did not start.");
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
