using System.Collections.Concurrent;

using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;
using Horizon.UI;

namespace Hex.Dawn;

// Files an extension keeps an eye on, opens for whoever is using it, and text it copies.
//
//   hex.watch(path)          on_file_changed(hex, path) once a file has changed on the disk and been left alone a moment
//                            (Aseprite saves a file in a few goes). A picture or an .ase that changes is read again next time
//   hex.open(file, path)     the file on screen, at the value of a path if one is given
//   hex.copy(text)           on the clipboard, and hex.copied() what was last copied through Hex
internal sealed partial class HexScene
{
    // How long a watched file has to be left alone before it counts as changed
    private const float WATCH_QUIET = 0.25f;

    private readonly Dictionary<string, (FileSystemWatcher Watcher, HashSet<HexExtension> By)> watched = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> changedAt = new(StringComparer.OrdinalIgnoreCase);
    private string copied = string.Empty;

    private (string, IRuntimeValue)[] Watching(HexExtension extension) =>
    [
        ("watch", Natives.Action("watch", args => Watch(extension, PathFor(extension, args.Text(0))))),
        ("open", Natives.Action("open", args => OpenForExtension(PathFor(extension, args.Text(0)), args.Has(1) ? args[1] : Values.Null))),
        ("copy", Natives.Action("copy", args =>
        {
            copied = args.Text(0);
            UICompositor.Copy(copied);
        })),
        ("copied", Natives.Function("copied", _ => new StringValue(copied)))
    ];

    private void Watch(HexExtension extension, string path)
    {
        if (watched.TryGetValue(path, out var known))
        {
            known.By.Add(extension);
            return;
        }

        if (Path.GetDirectoryName(path) is not { } folder || !Directory.Exists(folder))
            return;

        var watcher = new FileSystemWatcher(folder, Path.GetFileName(path)) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
        watcher.Changed += (_, _) => Changed(path);
        watcher.Created += (_, _) => Changed(path);
        watcher.Renamed += (_, _) => Changed(path);
        watcher.EnableRaisingEvents = true;
        watched[path] = (watcher, [extension]);
    }

    // On the watcher's thread, the editor is woken to hear about it
    private void Changed(string path)
    {
        changedAt[path] = System.Environment.TickCount64;
        Engine.WindowManager.Wake();
    }

    /// <summary>Every tick, the watched files that changed and have been left alone since, told to whoever watches them.</summary>
    private void UpdateWatchedFiles()
    {
        if (changedAt.IsEmpty)
            return;

        long now = System.Environment.TickCount64;
        foreach (var (path, at) in changedAt)
        {
            if (now - at < WATCH_QUIET * 1000 || !changedAt.TryRemove(path, out _) || !watched.TryGetValue(path, out var known))
                continue;

            if (Path.GetExtension(path).ToLowerInvariant() is ".ase" or ".aseprite" or ".png" or ".hor")
                Horizon.UI.Drawing.UISprite.ForgetAll();

            foreach (HexExtension extension in known.By)
            {
                if (extension.Enabled && extension.Failed is null && !extension.Raise("on_file_changed", HexOf(extension), new StringValue(path)))
                    ExtensionFailed(extension);
            }
        }
    }

    /// <summary>Opens a file for an extension, at the place a path of its values is written when there's one.</summary>
    private void OpenForExtension(string path, IRuntimeValue at)
    {
        Open(path);
        if (at is NullValue || OpenDocument(path) is not { IsText: true } opened)
            return;

        try
        {
            if (HexSourceTree.Read(opened.Editor.Text, path, ReadInclude).Find(HexValueEdits.PathOf(at), out _) is { At.Span.IsKnown: true } node)
                OpenAt(path, node.At.Span.Line - 1, node.At.Span.Column - 1);
        }
        catch (Exception)
        {
            // Doesn't run, it's open at the top
        }
    }

    /// <summary>Stops watching everything, as the editor goes.</summary>
    private void StopWatching()
    {
        foreach (var (watcher, _) in watched.Values)
            watcher.Dispose();
        watched.Clear();
    }
}
