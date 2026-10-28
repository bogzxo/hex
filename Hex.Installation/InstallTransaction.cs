using System.IO.Compression;
using System.Text.Json.Nodes;

namespace Hex.Installation;

/// <summary>Stages complete replacements beside the destination, then swaps directories with rollback.</summary>
public static class InstallTransaction
{
    public static async Task Install(ReleaseAsset release, string root, Action<InstallProgress>? progress, CancellationToken cancel, InstallChoices? choices = null)
    {
        root = ValidateRoot(root);
        Directory.CreateDirectory(Path.GetDirectoryName(root)!);
        using var lease = Lock(root);
        string zip = Path.Combine(Path.GetTempPath(), $"hex-{Guid.NewGuid():N}.zip");
        string stage = root + ".stage-" + Guid.NewGuid().ToString("N");
        try
        {
            await new ReleaseClient().Download(release, zip, progress, cancel);
            progress?.Invoke(new(InstallText.Get("status.verifying"), .74f));
            Prepare(zip, stage, root, release.Version, cancel, choices);
            cancel.ThrowIfCancellationRequested();
            InstallerHandoff.EnsureHexStopped(root);
            progress?.Invoke(new(InstallText.Get("status.registering"), .9f));
            Commit(stage, root, () => PlatformRegistration.Register(root, release.Version), () => RestoreRegistration(root));
            progress?.Invoke(new(InstallText.Get("status.ready"), 1));
        }
        finally
        {
            if (File.Exists(zip)) File.Delete(zip);
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
        }
    }

    public static string ValidateRoot(string root)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (root.Contains('\n') || root.Contains('\r') || root.Contains('"')) throw new IOException(InstallText.Get("errors.destination"));
        InstallPaths.NoLinks(root);
        if (root == Path.GetPathRoot(root) || root.Equals(InstallPaths.Home, StringComparison.OrdinalIgnoreCase))
            throw new IOException(InstallText.Get("errors.dedicated_folder"));
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any() && !File.Exists(Path.Combine(root, InstallPaths.Manifest)))
            throw new IOException(InstallText.Get("errors.portable_folder"));
        return root;
    }

    private static FileStream Lock(string root) => new(root + ".install-lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);

    public static (string Version, List<string> Files) ReadManifest(string root)
    {
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(root, InstallPaths.Manifest))) ?? throw new InvalidDataException(InstallText.Get("errors.invalid_record"));
        if (json["product"]?.GetValue<string>() != "bogzxo.Hex" || json["schema"]?.GetValue<int>() != 1)
            throw new InvalidDataException(InstallText.Get("errors.wrong_record"));
        var files = (json["files"] as JsonArray ?? throw new InvalidDataException(InstallText.Get("errors.missing_file_list")))
            .Select(n => n!.GetValue<string>()).ToList();
        foreach (string file in files) InstallPaths.NoLinks(InstallPaths.Under(root, file));
        return (json["version"]!.GetValue<string>(), files);
    }

    public static InstallChoices ReadChoices(string root)
    {
        if (!File.Exists(Path.Combine(root, InstallPaths.Manifest))) return new();
        _ = ReadManifest(root);
        return InstallChoices.FromJson(JsonNode.Parse(File.ReadAllText(Path.Combine(root, InstallPaths.Manifest)))?["integration"]);
    }

    public static void Prepare(string zip, string stage, string root, string version, CancellationToken cancel = default, InstallChoices? choices = null)
    {
        choices ??= ReadChoices(root);
        if (Directory.Exists(stage)) throw new IOException(InstallText.Get("errors.stage_exists"));
        var previous = File.Exists(Path.Combine(root, InstallPaths.Manifest)) ? ReadManifest(root).Files : [];
        if (Directory.Exists(root)) InstallPaths.CopyTree(root, stage);
        else Directory.CreateDirectory(stage);
        foreach (string owned in previous)
        {
            string path = InstallPaths.Under(stage, owned);
            if (File.Exists(path)) File.Delete(path);
        }

        var files = new List<string>();
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long unpacked = 0;
        using var archive = ZipFile.OpenRead(zip);
        foreach (var entry in archive.Entries)
        {
            cancel.ThrowIfCancellationRequested();
            string name = entry.FullName;
            if (!name.StartsWith("Hex/", StringComparison.Ordinal)) throw new InvalidDataException(InstallText.Get("errors.package_root"));
            name = name[4..];
            if (name.Length == 0) continue;
            string path = InstallPaths.Under(stage, name.TrimEnd('/'));
            int kind = (entry.ExternalAttributes >> 16) & 0xf000;
            if (kind is not (0 or 0x8000 or 0x4000)) throw new InvalidDataException(InstallText.Get("errors.package_links"));
            if (entry.Name.Length == 0) { Directory.CreateDirectory(path); continue; }
            if (!unique.Add(name) || name.Equals(InstallPaths.Manifest, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(InstallText.Get("errors.package_names"));
            unpacked += entry.Length;
            if (unpacked > 4L * 1024 * 1024 * 1024 || files.Count >= 100000) throw new InvalidDataException(InstallText.Get("errors.package_size"));
            if (File.Exists(path)) throw new IOException(InstallText.Get("errors.personal_file", ("file", name)));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path);
            if (!OperatingSystem.IsWindows())
            {
                var mode = (UnixFileMode)((entry.ExternalAttributes >> 16) & 0x1ff);
                File.SetUnixFileMode(path, mode == 0 ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead : mode);
            }
            files.Add(name);
        }
        if (!files.Contains(InstallPaths.Executable) || !files.Contains("installer/" + InstallPaths.InstallerExecutable))
            throw new InvalidDataException(InstallText.Get("errors.package_installer"));
        if (!OperatingSystem.IsWindows())
            foreach (string executable in new[] { InstallPaths.Executable, "installer/" + InstallPaths.InstallerExecutable })
            {
                string path = InstallPaths.Under(stage, executable);
                File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }
        var record = new JsonObject
        {
            ["product"] = "bogzxo.Hex", ["schema"] = 1, ["version"] = version,
            ["integration"] = choices.ToJson(),
            ["files"] = new JsonArray(files.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray())
        };
        File.WriteAllText(Path.Combine(stage, InstallPaths.Manifest), record.ToJsonString());
    }

    public static void Commit(string stage, string root, Action register, Action? rollbackRegistration = null)
    {
        InstallPaths.NoLinks(root);
        string backup = root + ".previous-" + Guid.NewGuid().ToString("N");
        bool hadPrevious = Directory.Exists(root), moved = false;
        if (hadPrevious) Directory.Move(root, backup);
        try
        {
            Directory.Move(stage, root);
            moved = true;
            register();
        }
        catch
        {
            if (moved) Directory.Move(root, stage);
            if (hadPrevious) Directory.Move(backup, root);
            // Restore registrations as well, including a first install that failed part-way through registration.
            try
            {
                rollbackRegistration?.Invoke();
            }
            catch { /* Preserve the original error; payload rollback has already completed. */ }
            throw;
        }
        // A cleanup failure must never roll back a successfully registered installation.
        if (hadPrevious)
            try { Directory.Delete(backup, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static void RestoreRegistration(string root)
    {
        if (File.Exists(Path.Combine(root, InstallPaths.Manifest))) PlatformRegistration.Register(root, ReadManifest(root).Version);
        else PlatformRegistration.Unregister(root);
    }

    public static void Uninstall(string root) => Uninstall(root, () => PlatformRegistration.Unregister(root), () => RestoreRegistration(root));

    internal static void Uninstall(string root, Action unregister, Action restoreRegistration)
    {
        root = ValidateRoot(root);
        using var lease = Lock(root);
        var manifest = ReadManifest(root);
        InstallerHandoff.EnsureHexStopped(root);
        string stage = root + ".stage-" + Guid.NewGuid().ToString("N");
        try
        {
            InstallPaths.CopyTree(root, stage);
            foreach (string file in manifest.Files)
            {
                string path = InstallPaths.Under(stage, file);
                if (File.Exists(path)) File.Delete(path);
            }
            File.Delete(Path.Combine(stage, InstallPaths.Manifest));
            Commit(stage, root, unregister, restoreRegistration);
            // Keep settings, backups, extensions and every file the package did not own.
            foreach (string folder in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(p => p.Length))
                if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
            if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
            if (OperatingSystem.IsMacOS() && Path.GetFileName(root) == "MacOS")
            {
                string contents = Path.GetDirectoryName(root)!;
                string bundle = Path.GetDirectoryName(contents)!;
                if (Path.GetFileName(contents) == "Contents" && bundle.EndsWith(".app", StringComparison.Ordinal))
                {
                    if (!Directory.EnumerateFileSystemEntries(contents).Any()) Directory.Delete(contents);
                    if (!Directory.EnumerateFileSystemEntries(bundle).Any()) Directory.Delete(bundle);
                }
            }
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }
}
