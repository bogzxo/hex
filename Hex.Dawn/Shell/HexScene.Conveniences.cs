using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The little things every editor has and nobody notices until they aren't there. The commands used last over the rest of
// the palette and the files opened last over the rest of quick open (thinner and a colour of their own, ListBox.Lead),
// the files closed last brought back with Ctrl+Shift+T, a scratchpad on Ctrl+N, the path of the file on screen copied, the trailing spaces and
// the last newline seen to on a save, and a file of code saved by itself a moment after typing stops.
internal sealed partial class HexScene
{
    // How many closed files are remembered for Ctrl+Shift+T, and the files the recent lists are kept in (the settings folder)
    private const int MAX_CLOSED = 20;
    private const string RECENT_COMMANDS_FILE = "hex_recent_commands.txt", RECENT_OPENED_FILE = "hex_recent_opened.txt", SCRATCH_FOLDER = "scratch";

    // Testing itself it keeps them in memory, it'd be the palette of whoever ran it otherwise
    private UIRecentList? recentCommands, recentOpened;
    private UIRecentList RecentCommands => recentCommands ??= new(KeepsRecent ? HexFiles.Kept(RECENT_COMMANDS_FILE) : null);
    private UIRecentList RecentOpened => recentOpened ??= new(KeepsRecent ? HexFiles.Kept(RECENT_OPENED_FILE) : null);

    // The files closed last, the newest at the end, and where the caret was in each
    private readonly List<(string Path, int Line, int Column)> closed = [];

    // The code being waited on to stop changing for the auto save, its version, and how long it has stood still
    private CodeEditor? autoSaveIn;
    private int autoSaveVersion = -1, autoSavedVersion = -1;
    private float autoSaveStill;
    private bool savingQuietly;

    /* Remembering */

    /// <summary>A file was opened, it goes to the top of quick open's lead, by the name quick open lists it by.</summary>
    private void RememberOpened(string path) => RecentOpened.Remember(Relative(path));

    /// <summary>A file is being closed, it's kept so Ctrl+Shift+T can bring it back where the caret was.</summary>
    private void RememberClosed(HexDocument closing)
    {
        if (closing.Path is not { } path)
            return;

        string full = Path.GetFullPath(path);
        closed.RemoveAll(known => string.Equals(known.Path, full, PlatformPaths.Comparison));
        var caret = closing.IsText ? closing.Editor.Document.Caret : default;
        closed.Add((full, caret.Line, caret.Column));
        if (closed.Count > MAX_CLOSED)
            closed.RemoveAt(0);
    }

    /// <summary>Ctrl+Shift+T, the file closed last opened again, the caret where it was. One that's gone is skipped.</summary>
    private void ReopenClosed()
    {
        while (closed.Count > 0)
        {
            var (path, line, column) = closed[^1];
            closed.RemoveAt(closed.Count - 1);
            if (!File.Exists(path))
                continue;

            OpenAt(path, line, column);
            return;
        }

        Say("nothing closed to bring back");
    }

    /// <summary>Copies where the file on screen is, the whole path or the path inside the workspace.</summary>
    private void CopyPath(bool relative)
    {
        if (document.Path is not { } path)
            return;

        string copied = relative ? Relative(path) : Path.GetFullPath(path);
        UIClipboard.Copy(copied);
        Say($"copied {copied}");
    }

    /* Scratchpads */

    /// <summary>
    /// Ctrl+N, an empty file of text to put something down in, made in the scratch folder of the settings folder next to
    /// the exe (testing itself, in the test's folder) and opened, saved like any other file and there the next time.
    /// They're numbered, the next number after the last one there is.
    /// </summary>
    private void NewScratchpad()
    {
        string folder = options.SelfTest ? Path.Combine(testFolder, "scratch") : Path.Combine(TerminalFiles.UserFolder, SCRATCH_FOLDER);
        try
        {
            Directory.CreateDirectory(folder);
            int number = 1;
            while (File.Exists(Path.Combine(folder, $"scratch {number}.txt")))
                number++;

            string path = Path.Combine(folder, $"scratch {number}.txt");
            File.WriteAllText(path, string.Empty);
            OpenAt(path, 0, 0);
        }
        catch (Exception e)
        {
            Say($"couldn't make a scratchpad, {e.Message}", error: true);
        }
    }

    /* Saving */

    /// <summary>
    /// The text a file of code is saved with, without the spaces at the ends of its lines and with one newline at the end,
    /// whichever of those the settings ask for. Null when it's the same, so a save that changes nothing records nothing.
    /// </summary>
    private string? Tidied(string text)
    {
        if (!settings.TrimWhitespace && !settings.FinalNewline)
            return null;

        string tidy = text;
        if (settings.TrimWhitespace)
            tidy = string.Join('\n', tidy.Split('\n').Select(line => line.TrimEnd(' ', '\t')));
        if (settings.FinalNewline && tidy.Length > 0 && !tidy.EndsWith('\n'))
            tidy += "\n";

        return tidy == text ? null : tidy;
    }

    /// <summary>Every tick, a file of code saved by itself once it has stood still long enough, when the settings say so.</summary>
    private void UpdateAutoSave(float dt)
    {
        if (!settings.AutoSave || !document.IsText || document.Path is not { } path || compositor.Focus is not CodeEditor typedIn)
        {
            autoSaveIn = null;
            return;
        }

        int version = typedIn.Document.Version;
        if (typedIn != autoSaveIn || version != autoSaveVersion)
        {
            autoSaveIn = typedIn;
            autoSaveVersion = version;
            autoSaveStill = 0.0f;
            return;
        }

        // A version is tried once, a save that asked something (the file changed on disk) isn't asked again every second
        autoSaveStill += dt;
        if (autoSaveStill < settings.AutoSaveDelay || version == autoSavedVersion || !(document.CodeEdited || document.Modified))
            return;

        // A file turning into a layout asks first, that's never done behind anybody's back
        if (Horizon.HIDL.Preprocessing.HIDLPreprocessor.IsLayout(document.Editor.Text) && !Horizon.HIDL.Preprocessing.HIDLPreprocessor.IsLayout(document.SavedCode))
            return;

        autoSavedVersion = version;
        savingQuietly = true;
        try
        {
            SaveTo(path);
        }
        finally
        {
            savingQuietly = false;
        }
    }
}
