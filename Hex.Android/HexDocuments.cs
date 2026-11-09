using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

using Android.App;
using Android.Content;
using Android.Database;
using Android.Provider;

using Hex.Core;

using Horizon.Logging;

using AndroidUri = Android.Net.Uri;

namespace Hex.Android;

/// <summary>
/// Documents from outside the app, through the system's picker (the Storage Access Framework), so Hex never asks for
/// every file on the tablet. Dawn works on paths, a picked document is a content:// address, so what's picked is copied
/// into files/hex/picked and that copy is what's opened. Saving the copy writes it back through the picker's grant, the
/// address is remembered with a persisted grant so it still works after a restart. A folder is copied the same way, as
/// far as <see cref="FOLDER_FILES"/> files go, and a file saved inside it (an existing one or a new one) is written back.
/// <para>
/// What it can't do it says in the log and to Dawn (a file deleted or renamed in the copy stays in the original, the
/// original changed by another app isn't seen until it's picked again).
/// </para>
/// </summary>
internal static class HexDocuments
{
    private const int PICK_FILE = 0x4E01, PICK_FOLDER = 0x4E02;

    // How much of a picked folder is copied in, past that it's left out with a line in the log
    private const int FOLDER_FILES = 4000;
    private const long FOLDER_BYTES = 256L * 1024 * 1024;

    // Where every copy came from, kept as lines of path, tab, document address, tab, tree address (empty for a file)
    private const string MAP = "picked.tsv";

    private static HexActivity? activity;
    private static Action<string?>? waiting;
    private static FileSystemWatcher? watcher;
    private static string root = string.Empty;

    // Copied path to (document, tree), tree empty for a single document. Read from the watcher's thread too
    private static readonly ConcurrentDictionary<string, (string Document, string Tree)> origins = new(StringComparer.Ordinal);

    // The copies a write back is due for, written a moment after the last change so a save in parts is one write
    private static readonly ConcurrentDictionary<string, DateTime> due = new(StringComparer.Ordinal);
    private static Timer? flusher;

    /// <summary>Hands Dawn the pickers, once the activity is up.</summary>
    public static void Attach(HexActivity from)
    {
        activity = from;
        if (HexAndroidFiles.Current is not { } files) return;
        if (root.Length == 0)
        {
            root = files.Picked;
            Load();
            Watch();
        }

        HexHost.PickFile = answer => Pick(PICK_FILE, answer);
        HexHost.PickFolder = answer => Pick(PICK_FOLDER, answer);
    }

    public static void Detach(HexActivity from)
    {
        if (activity == from) activity = null;
    }

    // Starts the system's picker on the main thread, the answer comes to Answered
    private static void Pick(int request, Action<string?> answer)
    {
        if (activity is not { } shown)
        {
            answer(null);
            return;
        }

        shown.RunOnUiThread(() =>
        {
            waiting?.Invoke(null);
            waiting = answer;
            Intent intent = request == PICK_FILE
                ? new Intent(Intent.ActionOpenDocument).AddCategory(Intent.CategoryOpenable).SetType("*/*")
                : new Intent(Intent.ActionOpenDocumentTree);
            intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission | ActivityFlags.GrantPersistableUriPermission);
            try
            {
                shown.StartActivityForResult(intent, request);
            }
            catch (ActivityNotFoundException e)
            {
                Log.Warning($"[Hex] There's no document picker on this device. {e.Message}");
                Answer(null);
            }
        });
    }

    /// <summary>The picker came back. Main thread, the copying goes off it.</summary>
    public static void Answered(HexActivity from, int request, Result result, Intent? data)
    {
        if (request is not (PICK_FILE or PICK_FOLDER)) return;
        if (result != Result.Ok || data?.Data is not { } picked)
        {
            Answer(null);
            return;
        }

        ContentResolver resolver = from.ContentResolver!;
        ActivityFlags granted = data.Flags & (ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
        try
        {
            resolver.TakePersistableUriPermission(picked, granted);
        }
        catch (Java.Lang.SecurityException e)
        {
            Log.Warning($"[Hex] The picker's grant for {picked} couldn't be kept, it lasts until Hex stops. {e.Message}");
        }

        bool writable = (granted & ActivityFlags.GrantWriteUriPermission) != 0;
        _ = Task.Run(() =>
        {
            string? copied = null;
            try
            {
                copied = request == PICK_FILE ? CopyDocument(resolver, picked) : CopyTree(resolver, picked);
                if (!writable) Log.Warning($"[Hex] {picked} was picked read only, saving the copy won't change the original.");
            }
            catch (Exception e)
            {
                Log.Error($"[Hex] Couldn't copy {picked} in. {e}");
            }

            Answer(copied);
        });
    }

    private static void Answer(string? path)
    {
        Action<string?>? answer = Interlocked.Exchange(ref waiting, null);
        answer?.Invoke(path);
    }

    /* Copying in */

    private static string CopyDocument(ContentResolver resolver, AndroidUri document)
    {
        string name = Safe(DisplayName(resolver, document) ?? "document");
        string folder = Path.Combine(root, Short(document.ToString()!));
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name);
        ReadInto(resolver, document, path);
        Remember(path, document.ToString()!, string.Empty);
        Save();
        Log.Info($"[Hex] Picked {document} copied to {path}.");
        return path;
    }

    private static string CopyTree(ContentResolver resolver, AndroidUri tree)
    {
        string treeId = DocumentsContract.GetTreeDocumentId(tree)!;
        AndroidUri top = DocumentsContract.BuildDocumentUriUsingTree(tree, treeId)!;
        string name = Safe(DisplayName(resolver, top) ?? "folder");
        string folder = Path.Combine(root, Short(tree.ToString()!), name);
        Directory.CreateDirectory(folder);
        Remember(folder, top.ToString()!, tree.ToString()!);

        int files = 0;
        long bytes = 0;
        bool cut = false;
        var pending = new Stack<(string Id, string Into)>();
        pending.Push((treeId, folder));
        while (pending.Count > 0)
        {
            (string id, string into) = pending.Pop();
            AndroidUri children = DocumentsContract.BuildChildDocumentsUriUsingTree(tree, id)!;
            using ICursor? cursor = resolver.Query(children, [DocumentsContract.Document.ColumnDocumentId, DocumentsContract.Document.ColumnDisplayName,
                DocumentsContract.Document.ColumnMimeType, DocumentsContract.Document.ColumnSize], null, null, null);
            if (cursor is null) continue;
            while (cursor.MoveToNext())
            {
                string childId = cursor.GetString(0)!;
                string childName = Safe(cursor.GetString(1) ?? childId);
                string mime = cursor.GetString(2) ?? string.Empty;
                long size = cursor.IsNull(3) ? 0 : cursor.GetLong(3);
                AndroidUri child = DocumentsContract.BuildDocumentUriUsingTree(tree, childId)!;
                string path = Path.Combine(into, childName);

                if (mime == DocumentsContract.Document.MimeTypeDir)
                {
                    Directory.CreateDirectory(path);
                    Remember(path, child.ToString()!, tree.ToString()!);
                    pending.Push((childId, path));
                    continue;
                }

                if (files >= FOLDER_FILES || bytes + size > FOLDER_BYTES)
                {
                    cut = true;
                    continue;
                }

                ReadInto(resolver, child, path);
                Remember(path, child.ToString()!, tree.ToString()!);
                files++;
                bytes += size;
            }
        }

        Save();
        Log.Info($"[Hex] Picked folder {tree} copied to {folder}, {files} files, {bytes / 1024} KB.");
        if (cut) Log.Warning($"[Hex] {tree} has more than {FOLDER_FILES} files or {FOLDER_BYTES / (1024 * 1024)} MB, the rest wasn't copied in.");
        return folder;
    }

    private static void ReadInto(ContentResolver resolver, AndroidUri document, string path)
    {
        using Stream from = resolver.OpenInputStream(document) ?? throw new IOException($"{document} can't be read.");
        string temporary = path + ".picking";
        using (FileStream to = File.Create(temporary))
            from.CopyTo(to);
        File.Move(temporary, path, overwrite: true);
    }

    /* Writing back */

    private static void Watch()
    {
        watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
        watcher.Changed += (_, e) => Changed(e.FullPath);
        watcher.Created += (_, e) => Changed(e.FullPath);
        watcher.Renamed += (_, e) => Changed(e.FullPath);
        watcher.EnableRaisingEvents = true;
        flusher = new Timer(_ => Flush(), null, 500, 500);
    }

    private static void Changed(string path)
    {
        if (path.EndsWith(".picking", StringComparison.Ordinal) || Directory.Exists(path)) return;
        due[path] = DateTime.UtcNow;
    }

    // Everything changed more than a quarter second ago written back
    private static void Flush()
    {
        foreach ((string path, DateTime when) in due)
        {
            if (DateTime.UtcNow - when < TimeSpan.FromMilliseconds(250)) continue;
            if (!due.TryRemove(new KeyValuePair<string, DateTime>(path, when))) continue;
            try
            {
                WriteBack(path);
            }
            catch (Exception e)
            {
                Log.Error($"[Hex] Couldn't write {path} back to where it was picked from. {e.Message}");
                HexHost.Report?.Invoke($"{Path.GetFileName(path)} couldn't be written back to the device, it's saved in Hex only. {e.Message}");
            }
        }
    }

    private static void WriteBack(string path)
    {
        if (!File.Exists(path) || activity?.ApplicationContext?.ContentResolver is not { } resolver) return;

        if (!origins.TryGetValue(path, out var origin))
        {
            // A new file in a picked folder is made in the original folder, one anywhere else stays Hex's
            if (Path.GetDirectoryName(path) is not { } parent || !origins.TryGetValue(parent, out var folder) || folder.Tree.Length == 0) return;
            AndroidUri made = DocumentsContract.CreateDocument(resolver, AndroidUri.Parse(folder.Document)!, MimeOf(path), Path.GetFileName(path))
                ?? throw new IOException("the folder wouldn't take a new file");
            origin = (made.ToString()!, folder.Tree);
            Remember(path, origin.Document, origin.Tree);
            Save();
        }

        using (Stream to = resolver.OpenOutputStream(AndroidUri.Parse(origin.Document)!, "wt") ?? throw new IOException("it can't be written"))
        using (FileStream from = File.OpenRead(path))
            from.CopyTo(to);
        Log.Info($"[Hex] {path} written back to {origin.Document}.");
    }

    /* What came from where */

    private static void Remember(string path, string document, string tree) => origins[Path.GetFullPath(path)] = (document, tree);

    private static void Load()
    {
        string map = Path.Combine(root, MAP);
        if (!File.Exists(map)) return;
        foreach (string line in File.ReadAllLines(map))
        {
            string[] parts = line.Split('\t');
            if (parts.Length == 3) origins[parts[0]] = (parts[1], parts[2]);
        }
    }

    private static void Save()
    {
        string map = Path.Combine(root, MAP);
        var text = new StringBuilder();
        foreach ((string path, (string document, string tree)) in origins)
            text.Append(path).Append('\t').Append(document).Append('\t').Append(tree).Append('\n');
        File.WriteAllText(map + ".new", text.ToString());
        File.Move(map + ".new", map, overwrite: true);
    }

    private static string? DisplayName(ContentResolver resolver, AndroidUri document)
    {
        using ICursor? cursor = resolver.Query(document, [IOpenableColumns.DisplayName], null, null, null);
        return cursor is not null && cursor.MoveToFirst() ? cursor.GetString(0) : null;
    }

    private static string MimeOf(string path) =>
        global::Android.Webkit.MimeTypeMap.Singleton?.GetMimeTypeFromExtension(Path.GetExtension(path).TrimStart('.').ToLowerInvariant()) ?? "application/octet-stream";

    // A name that's a file name here, nothing that climbs out or hides from aapt and friends
    private static string Safe(string name)
    {
        foreach (char bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');
        name = name.Trim().TrimStart('.');
        return name.Length == 0 ? "document" : name;
    }

    private static string Short(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 6).ToLowerInvariant();
}
