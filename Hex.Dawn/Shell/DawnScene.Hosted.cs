using System.Collections.Concurrent;

namespace Hex.Dawn;

// What the place Hex runs on hands Dawn beyond the engine (HexHost). On a tablet that's the system's document picker for
// files outside the app and the odd thing to say, a picked document that couldn't be written back. Both answer on the
// host's threads, so what they bring waits in a queue for the next tick. On a desktop none of it is there and nothing
// here does anything.
internal sealed partial class DawnScene
{
    // What the host said and what its pickers picked, a file or a folder, since the last tick
    private readonly ConcurrentQueue<string> hostSaid = new();
    private readonly ConcurrentQueue<(string? Path, bool Folder)> hostPicked = new();

    /// <summary>The File menu's pickers of the host, next to Open, when it has them.</summary>
    private void AddHostedCommands()
    {
        HexHost.Report = said => hostSaid.Enqueue(said);

        if (HexHost.PickFile is { } file)
            commands.Add("File/new", Said("documents.open_from_device"), () => file(path => hostPicked.Enqueue((path, false))), icon: "go-to-file");
        if (HexHost.PickFolder is { } folder)
            commands.Add("File/new", Said("documents.open_folder_from_device"), () => folder(path => hostPicked.Enqueue((path, true))), icon: "root-folder");
    }

    /// <summary>Says what the host said and opens what its pickers picked, a folder as the workspace.</summary>
    private void UpdateHosted()
    {
        // Going to the back, everything not saved into the backups and the workspace as it is kept, now
        if (DawnProgram.flushWanted)
        {
            DawnProgram.flushWanted = false;
            backupWait = 0.0f;
            UpdateBackups(0.0f);
            explorer.KeepWorkspace();
            KeepScratchpads();
            HexLogLine("kept everything for going to the back");
        }

        while (hostSaid.TryDequeue(out string? said))
            Say(said, error: true);

        while (hostPicked.TryDequeue(out var picked))
        {
            if (picked.Path is not { } path)
                Say(Said("documents.nothing_picked"));
            else if (picked.Folder)
                explorer.OpenFolder(path);
            else
                Open(path);
        }
    }

    // A line in the engine's log, which is debug.log and logcat on a tablet
    private static void HexLogLine(string line) => Horizon.Logging.Log.Info("[Hex] " + line);
}
