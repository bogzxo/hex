using System.Security.Cryptography;
using System.Text;

namespace Horizon.Hex;

/// <summary>
/// What isn't saved, kept in a folder of Hex's own so a crash or a power cut doesn't take it (VS Code's hot exit, cut down).
/// A backup is a file a document, its first line saying which file it's of (or which untitled layout) and the rest its
/// text. Written on a thread of the pool, never the editor's. Nothing is ever written to the real file, Syncthing and a
/// game watching its files make auto save a bad idea.
/// </summary>
internal sealed class HexBackups
{
    private const string HEADER = "// hex backup of ";
    private const string UNTITLED = "untitled:";

    private readonly string folder;

    // What was last written for a document, by the name of its backup, so the same text isn't written twice
    private readonly Dictionary<string, string> written = new(StringComparer.OrdinalIgnoreCase);

    // The writes and deletes one after the other, a delete never overtaking the write before it
    private Task chain = Task.CompletedTask;

    public HexBackups(string folder) => this.folder = folder;

    /// <summary>A backup found when Hex starts, the file it's of (null for an untitled layout) and its text.</summary>
    public readonly record struct Found(string? Path, string Name, string Text, string File);

    /// <summary>The name a document's backup goes by, its path hashed, or the untitled layout's name.</summary>
    public static string KeyOf(string? path, string name)
    {
        if (path is null)
            return "untitled-" + string.Concat(name.Where(char.IsLetterOrDigit));

        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(path).ToLowerInvariant()));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    /// <summary>A document's text kept, off the editor's thread, unless it's what was kept last time.</summary>
    public void Keep(string? path, string name, string text)
    {
        string key = KeyOf(path, name);
        if (written.TryGetValue(key, out string? last) && last == text)
            return;

        written[key] = text;
        string file = System.IO.Path.Combine(folder, key + ".hor");
        string header = HEADER + (path is null ? UNTITLED + name : System.IO.Path.GetFullPath(path)) + "\n";
        Then(() =>
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(file, header + text);
        });
    }

    /// <summary>A document's backup taken away, it was saved or its changes were thrown away.</summary>
    public void Drop(string? path, string name)
    {
        string key = KeyOf(path, name);
        if (!written.Remove(key) && !File.Exists(System.IO.Path.Combine(folder, key + ".hor")))
            return;

        string file = System.IO.Path.Combine(folder, key + ".hor");
        Then(() => File.Delete(file));
    }

    // A bit of work on the pool after the last one. One that goes wrong (a backup that can't be written) is a backup
    // missing, nothing to tell anybody in the middle of typing
    private void Then(Action work) => chain = chain.ContinueWith(_ =>
    {
        try { work(); }
        catch (Exception) { }
    }, TaskScheduler.Default);

    /// <summary>Whether a document has a backup kept this run.</summary>
    public bool Has(string? path, string name) => written.ContainsKey(KeyOf(path, name));

    /// <summary>Every backup there is, on the editor's thread as Hex starts. The ones that can't be read are left out.</summary>
    public List<Found> All()
    {
        var found = new List<Found>();
        if (!Directory.Exists(folder))
            return found;

        foreach (string file in Directory.EnumerateFiles(folder, "*.hor"))
        {
            try
            {
                string all = File.ReadAllText(file);
                int end = all.IndexOf('\n');
                if (end < 0 || !all.StartsWith(HEADER, StringComparison.Ordinal))
                    continue;

                string of = all[HEADER.Length..end];
                string text = all[(end + 1)..];
                found.Add(of.StartsWith(UNTITLED, StringComparison.Ordinal)
                    ? new Found(null, of[UNTITLED.Length..], text, file)
                    : new Found(of, System.IO.Path.GetFileNameWithoutExtension(of), text, file));
            }
            catch (Exception)
            {
            }
        }

        return found;
    }

    /// <summary>Every backup gone, Hex was closed and told what to do with what wasn't saved. On the editor's thread, it's quitting.</summary>
    public void DropAll()
    {
        written.Clear();
        try
        {
            chain.Wait(1000);
            if (Directory.Exists(folder))
            {
                foreach (string file in Directory.EnumerateFiles(folder, "*.hor"))
                    File.Delete(file);
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>A backup taken away once it has been brought back, the document keeps a new one from there.</summary>
    public void Remove(Found found)
    {
        written.Remove(KeyOf(found.Path, found.Name));
        try { File.Delete(found.File); }
        catch (Exception) { }
    }
}
