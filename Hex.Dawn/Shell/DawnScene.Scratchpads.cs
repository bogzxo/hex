using Horizon.UI.Components;

namespace Hex.Dawn;

// Scratchpads, files of whatever, put down without thinking about where. They live in the scratch folder of the settings
// folder, one lot for every Hex and not one per workspace, so they're there whatever folder is open. One is saved quietly
// a moment after typing stops and when it's closed, it's never asked about, and an empty one is deleted when it closes.
// Open Scratchpad lists them by their first lines, Move into Workspace makes one a real file. docs/scratchpad-plan.md has why.
internal sealed partial class DawnScene
{
    private const string SCRATCH_FOLDER = "scratch", RECENT_SCRATCH_FILE = "hex_recent_scratch.txt";

    // How much of a first line names a scratchpad in the list, and how much of a file is read to find that line
    private const int SCRATCH_NAME_LENGTH = 60, SCRATCH_PEEK = 4096;

    // The languages picked last for a scratchpad, over the rest of the list
    private UIRecentList? recentScratchLanguages;
    private UIRecentList RecentScratchLanguages => recentScratchLanguages ??= new(KeepsRecent ? HexFiles.Kept(RECENT_SCRATCH_FILE) : null);

    // Testing itself it keeps them in the test's folder
    private string ScratchFolder => options.SelfTest ? Path.Combine(testFolder, SCRATCH_FOLDER) : Path.Combine(TerminalFiles.UserFolder, SCRATCH_FOLDER);

    /// <summary>Whether a document is a scratchpad, a file of text right in the scratch folder.</summary>
    private bool IsScratchpad(HexDocument of) => of.IsText && of.Path is { } path && IsScratchpad(path);

    private bool IsScratchpad(string path)
    {
        // Asked every tick by the auto save, so the answer's kept for the path asked last. Working it out made two strings a tick
        if (ReferenceEquals(path, scratchAsked.Path))
            return scratchAsked.Is;

        bool scratch = string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(ScratchFolder).TrimEnd(Path.DirectorySeparatorChar), PlatformPaths.Comparison);
        scratchAsked = (path, scratch);
        return scratch;
    }

    private (string? Path, bool Is) scratchAsked;

    /* Making one */

    /// <summary>Ctrl+N, a scratchpad of plain text, no questions first.</summary>
    private void NewScratchpad() => NewScratchpad(".txt");

    /// <summary>
    /// A scratchpad made with an extension and opened. The extension is all a language needs, the colours, the language
    /// server and HIDL's Values pane come with it the same as for any file. They're numbered, the first number free.
    /// </summary>
    private void NewScratchpad(string extension)
    {
        string folder = ScratchFolder;
        try
        {
            Directory.CreateDirectory(folder);
            SweepScratchpads();

            // A number is taken whatever the extension, scratch 3.py and scratch 3.txt side by side would only confuse
            int number = 1;
            while (Directory.EnumerateFiles(folder, $"scratch {number}.*").Any())
                number++;

            string path = Path.Combine(folder, $"scratch {number}{extension}");
            File.WriteAllText(path, string.Empty);
            OpenAt(path, 0, 0);
        }
        catch (Exception e)
        {
            Say(Said("conveniences.no_scratchpad", ("reason", e.Message)), error: true);
        }
    }

    /// <summary>Ctrl+Alt+N, a language picked first, plain text and the ones picked last on top.</summary>
    private void NewScratchpadIn()
    {
        var choices = new List<UIChoice> { Row(Said("language_mode.plain_text"), ".txt") };
        foreach (CodeLanguage language in AllLanguages())
        {
            // Some languages are known by whole file names (Dockerfile), those aren't an extension to put on one
            if (language.Extensions.FirstOrDefault(extension => extension.StartsWith('.')) is { } extension)
                choices.Add(Row(language.Name, extension));
        }

        ChooseFrom(Said("scratchpads.language_title"), Said("scratchpads.language_text"), choices, recent: RecentScratchLanguages);

        UIChoice Row(string name, string extension) => new(name, () => NewScratchpad(extension)) { Detail = extension, Icon = "note" };
    }

    /* Getting back to one */

    /// <summary>Open Scratchpad, every one of them the newest first, named by its first line with its file and when after.</summary>
    private void OpenScratchpad()
    {
        SweepScratchpads();
        var choices = new List<UIChoice>();
        foreach (FileInfo file in ScratchFiles().Select(path => new FileInfo(path)).OrderByDescending(file => file.LastWriteTimeUtc))
        {
            string name = ScratchName(file.FullName);
            string path = file.FullName;
            choices.Add(new UIChoice(name, () => OpenAt(path, 0, 0))
            {
                Detail = $"{file.Name}  {HexGit.Ago(file.LastWriteTime)}",
                Icon = "note",
                Id = $"{name}  {file.Name}"
            });
        }

        if (choices.Count == 0)
        {
            Say(Said("scratchpads.none"));
            return;
        }

        ChooseFrom(Said("scratchpads.open_title"), Said("scratchpads.open_text"), choices);
    }

    // Every file in the scratch folder, none when there isn't one yet
    private IEnumerable<string> ScratchFiles()
    {
        string folder = ScratchFolder;
        return Directory.Exists(folder) ? Directory.EnumerateFiles(folder) : [];
    }

    /// <summary>
    /// What a scratchpad is called in the list, its first line with something on it and without the # or // in front, cut
    /// short when it's long. The file's name when there's nothing in it. An open one goes by what's typed, not what's saved.
    /// </summary>
    private string ScratchName(string path)
    {
        string text;
        if (OpenScratchpadAt(path) is { } open)
        {
            text = open.Editor.Text;
        }
        else
        {
            try
            {
                using var reader = new StreamReader(path);
                var buffer = new char[SCRATCH_PEEK];
                text = new string(buffer, 0, reader.ReadBlock(buffer, 0, buffer.Length));
            }
            catch (IOException)
            {
                text = string.Empty;
            }
        }

        string? first = text.Split('\n').Select(line => line.Trim().TrimStart('#', '/', '*', '-', '>', ';', ' ', '\t').Trim()).FirstOrDefault(line => line.Length > 0);
        if (first is null)
            return Path.GetFileName(path);

        return first.Length > SCRATCH_NAME_LENGTH ? first[..SCRATCH_NAME_LENGTH].TrimEnd() + "…" : first;
    }

    // The open document of a file, null when it isn't open
    private HexDocument? OpenScratchpadAt(string path) =>
        documents.FirstOrDefault(open => open.Path is { } at && string.Equals(Path.GetFullPath(at), Path.GetFullPath(path), PlatformPaths.Comparison));

    /// <summary>The scratchpads with nothing in them that aren't open deleted, nobody wants a dozen empty files to pick from.</summary>
    private void SweepScratchpads()
    {
        foreach (string path in ScratchFiles().ToList())
        {
            try
            {
                var file = new FileInfo(path);
                if (file.Length <= SCRATCH_PEEK && OpenScratchpadAt(path) is null && string.IsNullOrWhiteSpace(File.ReadAllText(path)))
                    file.Delete();
            }
            catch (IOException)
            {
                // in use by something else, it's swept next time
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /* Keeping one */

    /// <summary>A scratchpad's changes written to its file without a word, whichever document is on screen.</summary>
    private void SaveScratchpad(HexDocument scratchpad)
    {
        if (scratchpad.Path is not { } path || !HasChanges(scratchpad))
            return;

        // Saving goes by the document on screen, so it's shown for the save and the one that was put back after
        HexDocument was = document;
        if (was != scratchpad)
            Show(scratchpad);

        savingQuietly = true;
        try
        {
            SaveTo(path);
        }
        finally
        {
            savingQuietly = false;
        }

        if (was != scratchpad && documents.Contains(was))
            Show(was);
    }

    /// <summary>
    /// Closing the scratchpad on screen, saved without asking or deleted when there's nothing in it. False when it isn't
    /// one, or saving it didn't go (the file changed on the disk), and closing should ask the way it does for any file.
    /// </summary>
    private bool ClosedScratchpad()
    {
        if (!IsScratchpad(document) || document.Path is not { } path)
            return false;

        if (string.IsNullOrWhiteSpace(document.Editor.Text))
        {
            CloseNow();
            DeleteScratchpad(path);
            return true;
        }

        SaveScratchpad(document);
        if (HasChanges(document))
            return false;

        CloseNow();
        return true;
    }

    /// <summary>Hex is closing, every scratchpad saved quietly and the empty ones deleted, so only real files are asked about.</summary>
    private void KeepScratchpads()
    {
        foreach (HexDocument scratchpad in documents.Where(IsScratchpad).ToList())
        {
            if (string.IsNullOrWhiteSpace(scratchpad.Editor.Text))
                DeleteScratchpad(scratchpad.Path!);
            else
                SaveScratchpad(scratchpad);
        }
    }

    // Helper method to delete a scratchpad's file, one that won't go is swept next time
    private static void DeleteScratchpad(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /* Making one a real file */

    /// <summary>
    /// Move Scratchpad into Workspace, a name asked for in the first folder of the workspace (its first line made a file
    /// name, as a start), the file moved there and opened in its place where the caret was. A real file from then on.
    /// </summary>
    private void MoveScratchpadIntoWorkspace()
    {
        if (!IsScratchpad(document) || document.Path is not { } from)
        {
            Say(Said("scratchpads.not_one"), error: true);
            return;
        }

        if (explorer.workspace.IsEmpty)
        {
            Say(Said("scratchpads.no_workspace"), error: true);
            return;
        }

        HexDocument scratchpad = document;
        string folder = explorer.workspace.Folders[0], extension = Path.GetExtension(from);
        AskText(Said("scratchpads.move_title"), Said("scratchpads.move_text", ("folder", Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))), FileNameFrom(ScratchName(from)) + extension, Said("scratchpads.move"),
            typed => MoveScratchpad(scratchpad, Path.Combine(folder, typed.Trim())));
    }

    /// <summary>A scratchpad moved to where it was asked to go, saved first, and opened from there.</summary>
    private void MoveScratchpad(HexDocument scratchpad, string to)
    {
        string name = Path.GetFileName(to);
        if (!documents.Contains(scratchpad) || scratchpad.Path is not { } from)
            return;

        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            Say(Said("explorer_files.bad_file_name", ("name", name)), error: true);
            return;
        }

        if (Path.GetExtension(to).Length == 0)
            to += Path.GetExtension(from);
        if (File.Exists(to))
        {
            Say(Said("scratchpads.taken", ("name", Path.GetFileName(to))), error: true);
            return;
        }

        SaveScratchpad(scratchpad);
        if (HasChanges(scratchpad))
            return;

        Show(scratchpad);
        var caret = scratchpad.Editor.Document.Caret;
        try
        {
            File.Move(from, to);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Say(Said("scratchpads.move_failed", ("reason", e.Message)), error: true);
            return;
        }

        CloseNow();
        OpenAt(to, caret.Line, caret.Column);
        Say(Said("scratchpads.moved", ("path", Relative(to))));
    }

    // Helper method to make a scratchpad's name a file's, its first few words lowercase with dashes between them
    private static string FileNameFrom(string name)
    {
        var words = System.Text.RegularExpressions.Regex.Split(name, @"[^\p{L}\p{N}]+").Where(word => word.Length > 0).Take(5).Select(word => word.ToLowerInvariant()).ToList();
        return words.Count == 0 ? "scratch" : string.Join('-', words);
    }
}
