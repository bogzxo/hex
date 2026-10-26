using System.Globalization;

using Hex.Core.Git;

using Horizon.HIDL.Runtime;
using Horizon.UI.Components;
using Horizon.UI.Scripting;

namespace Hex.Dawn;

// The rows of the properties of files, what a file is (its name, where it is, how big, when), how its text is written
// (its language, encoding, line endings, indentation), what a picture is, and what git says of it. Several files at once
// have what they share, a folder what's in it. The project's rows are in HexProperties.Project.
internal sealed partial class HexProperties
{
    // The keys of the rows, what the values and the changes go by
    private const string NAME = "name", PATH = "path", SIZE = "size", CREATED = "created", MODIFIED = "modified", CONTENTS = "contents";
    private const string LANGUAGE = "language", ENCODING = "encoding", ENDINGS = "line_endings", INDENT = "indentation", LINES = "lines";
    private const string DIMENSIONS = "dimensions", FORMAT = "format";
    private const string GIT_STATUS = "git_status", GIT_BRANCH = "git_branch", GIT_COMMIT = "last_commit";

    // What a language row says for a file whose language is worked out by its name
    private const string AUTO = "auto";

    private void Build(HexDocument document)
    {
        generation++;
        values.Clear();
        var rows = new List<UISetting>();
        string general = Said(shown.Count switch
        {
            0 => "untitled",
            1 when Directory.Exists(shown[0]) => "folder",
            1 => "file",
            _ => "several"
        }, ("count", shown.Count.ToString(CultureInfo.InvariantCulture)));

        if (shown.Count == 0)
        {
            rows.Add(ReadOnly(NAME, general, "name_help"));
            values[NAME] = new StringValue(document.Name);
        }
        else
        {
            GeneralRows(rows, general);
        }

        HexDocument? open = shown.Count == 1 ? context.Documents.Find(shown[0]) : shown.Count == 0 ? document : null;
        // A picture has its size and format, a file of text how it's written, a file of neither (an exe) only what it is
        bool file = shown.Count == 1 && File.Exists(shown[0]);
        Hex.Core.HexImageFacts? picture = file ? Hex.Core.HexFileFacts.Image(shown[0]) : null;
        if (picture is null && (open is { IsText: true, Picture: null } || file && open is null && Hex.Core.HexFileFacts.LooksLikeText(shown[0])))
            TextRows(rows, open);
        if (picture is { } image)
        {
            string group = Said("image");
            rows.Add(ReadOnly(DIMENSIONS, group, "dimensions_help"));
            rows.Add(ReadOnly(FORMAT, group, "format_help"));
            values[DIMENSIONS] = new StringValue(Said("pixels", ("width", image.Width.ToString(CultureInfo.InvariantCulture)), ("height", image.Height.ToString(CultureInfo.InvariantCulture))));
            values[FORMAT] = new StringValue(image.Format);
        }

        GitRows(rows);
        ProjectRows(rows);
        page.Settings = rows;
        page.Refresh();
    }

    // A row with a value to read and copy and nothing to change, under a heading, with a sentence of what it is
    private UISetting ReadOnly(string key, string group, string help) =>
        new(key, Said(key), UIFormKind.Text, Said(help), group, ReadOnly: true);

    private void GeneralRows(List<UISetting> rows, string group)
    {
        bool one = shown.Count == 1;
        rows.Add(one ? new UISetting(NAME, Said(NAME), UIFormKind.Text, Said("name_help_rename"), group) : ReadOnly(NAME, group, "name_help"));
        rows.Add(ReadOnly(PATH, group, one ? "path_help" : "paths_help"));
        rows.Add(ReadOnly(SIZE, group, "size_help"));

        values[NAME] = new StringValue(one ? Path.GetFileName(shown[0].TrimEnd('\\', '/')) : Said("count_items", ("count", shown.Count.ToString(CultureInfo.InvariantCulture))));
        values[PATH] = new StringValue(one ? shown[0] : CommonFolder(shown));

        long bytes = 0;
        int files = 0, folders = 0;
        foreach (string path in shown)
        {
            if (File.Exists(path))
            {
                bytes += new FileInfo(path).Length;
                files++;
            }
            else
            {
                folders++;
            }
        }

        values[SIZE] = new StringValue(folders > 0 ? Said("counting") : Hex.Core.HexFileFacts.SizeExactly(bytes));
        if (folders > 0)
            CountFolders(bytes, generation);

        if (one)
        {
            rows.Add(ReadOnly(CREATED, group, "created_help"));
            rows.Add(ReadOnly(MODIFIED, group, "modified_help"));
            values[CREATED] = new StringValue(When(File.GetCreationTime(shown[0])));
            values[MODIFIED] = new StringValue(When(File.GetLastWriteTime(shown[0])));
        }

        if (folders > 0)
        {
            rows.Add(ReadOnly(CONTENTS, group, "contents_help"));
            values[CONTENTS] = new StringValue(Said("counting"));
        }
    }

    private static string When(DateTime at) => at.ToString("d MMMM yyyy, HH:mm:ss", CultureInfo.InvariantCulture);

    // The folder several files share, what their paths start with
    private static string CommonFolder(List<string> paths)
    {
        string common = Path.GetDirectoryName(paths[0]) ?? paths[0];
        while (common.Length > 0 && !paths.All(path => PlatformPaths.IsUnder(path, common)))
            common = Path.GetDirectoryName(common) ?? string.Empty;
        return common;
    }

    // The files under the folders picked counted and summed off the editor's thread, a big folder takes a moment
    private void CountFolders(long filesBytes, int asked)
    {
        string[] folders = [.. shown.Where(Directory.Exists)];
        context.Work.InBackground(Said("counting"), () =>
        {
            long bytes = filesBytes;
            int files = 0, inside = 0;
            foreach (string folder in folders)
            {
                try
                {
                    foreach (string entry in Directory.EnumerateFileSystemEntries(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                    {
                        if (File.Exists(entry))
                        {
                            files++;
                            bytes += new FileInfo(entry).Length;
                        }
                        else
                        {
                            inside++;
                        }

                        if (files + inside > 200_000)
                            break;
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }

            return (bytes, files, inside);
        }, counted =>
        {
            if (asked != generation)
                return;

            values[SIZE] = new StringValue(Hex.Core.HexFileFacts.SizeExactly(counted.bytes));
            values[CONTENTS] = new StringValue(Said("contents_said", ("files", HexText.Count("file", counted.files)), ("folders", HexText.Count("folder", counted.inside))));
            page.Refresh(SIZE);
            page.Refresh(CONTENTS);
        }, quiet: true);
    }

    private void TextRows(List<UISetting> rows, HexDocument? open)
    {
        string group = Said("text");
        string? path = shown.Count == 1 ? shown[0] : open?.Path;

        // Every language there's a grammar for, Auto Detect first saying what that comes to, Change Language Mode beside it
        CodeLanguage[] languages = [.. CodeLanguages.Languages().Append(new CodeLanguage("HIDL", "hidl", [".hor"])).OrderBy(language => language.Name, StringComparer.OrdinalIgnoreCase)];
        ICodeHighlighter? detected = path is null ? null : CodeLanguages.Detect(path, null);
        string[] choices = [AUTO, CodeLanguages.PLAIN_TEXT, .. languages.Select(language => language.Id)];
        string[] labels = [Said("auto_detect", ("language", detected?.Name ?? Said("plain_text"))), Said("plain_text"), .. languages.Select(language => language.Name)];
        rows.Add(new UISetting(LANGUAGE, Said(LANGUAGE), UIFormKind.Choice, Said("language_help"), group, Choices: choices, ChoiceLabels: labels,
            Buttons: open is not null && ReferenceEquals(open, context.Documents.Current) ? [Said("change_language_mode")] : null));
        string id = path is not null && context.Settings.Values.LanguageOverrides.TryGetValue(Path.GetFullPath(path), out string? set) ? set : AUTO;
        values[LANGUAGE] = new StringValue(id);

        Hex.Core.HexTextFacts? facts = null;
        try
        {
            if (path is not null && File.Exists(path))
                facts = Hex.Core.HexFileFacts.Text(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        rows.Add(ReadOnly(ENCODING, group, "encoding_help"));
        values[ENCODING] = new StringValue(facts?.Encoding ?? "UTF-8");

        // An open file's line endings are what it will be saved with, and changed here as the status bar changes them
        if (open is { IsText: true })
        {
            rows.Add(new UISetting(ENDINGS, Said(ENDINGS), UIFormKind.Choice, Said("line_endings_help_open"), group, Choices: ["LF", "CRLF"], ChoiceLabels: ["LF (\\n)", "CRLF (\\r\\n)"]));
            values[ENDINGS] = new StringValue(open.CrLf ? "CRLF" : "LF");
        }
        else
        {
            rows.Add(ReadOnly(ENDINGS, group, "line_endings_help"));
            values[ENDINGS] = new StringValue(facts?.LineEndings ?? "None");
        }

        rows.Add(ReadOnly(INDENT, group, "indentation_help"));
        values[INDENT] = new StringValue(open is { IsText: true }
            ? open.Editor.Document.Indent.StartsWith('\t') ? Said("tabs", ("size", open.Editor.TabSize.ToString(CultureInfo.InvariantCulture))) : Said("spaces", ("size", open.Editor.Document.Indent.Length.ToString(CultureInfo.InvariantCulture)))
            : facts?.Indentation ?? "None");

        rows.Add(ReadOnly(LINES, group, "lines_help"));
        values[LINES] = new StringValue((open is { IsText: true } ? open.Editor.Document.LineCount : facts?.Lines ?? 0).ToString("N0", CultureInfo.InvariantCulture));
    }

    // What changes in an open file without the rows being made again, its line endings and how many lines it has
    private void RefreshDocumentRows()
    {
        HexDocument? open = shown.Count == 1 ? context.Documents.Find(shown[0]) : shown.Count == 0 ? shownDocument : null;
        if (open is not { IsText: true } || !values.ContainsKey(LINES))
            return;

        values[ENDINGS] = new StringValue(open.CrLf ? "CRLF" : "LF");
        values[LINES] = new StringValue(open.Editor.Document.LineCount.ToString("N0", CultureInfo.InvariantCulture));
        page.Refresh(ENDINGS);
        page.Refresh(LINES);
    }

    private void GitRows(List<UISetting> rows)
    {
        if (Git is not { } git || shown.Count == 0 || git.StatusFor(shown[0]) is not { } status)
            return;

        string group = Said("git");
        rows.Add(ReadOnly(GIT_STATUS, group, "git_status_help"));
        rows.Add(ReadOnly(GIT_BRANCH, group, "git_branch_help"));
        values[GIT_STATUS] = new StringValue(string.Join(", ", shown.Select(path => StatusText(Directory.Exists(path) ? new GitFileState(git.FolderChange(path), false, false) : git.StateOf(path))).Distinct()));
        values[GIT_BRANCH] = new StringValue(status.Branch ?? Said("detached"));

        if (shown.Count != 1)
            return;

        rows.Add(ReadOnly(GIT_COMMIT, group, "last_commit_help"));
        values[GIT_COMMIT] = new StringValue(Said("counting"));
        string file = shown[0], root = status.Root;
        int asked = generation;
        context.Work.InBackground(Said("last_commit"), () =>
        {
            GitResult said = GitProcess.Run(root, ["log", "-1", "--format=%h  %s  (%an, %ar)", "--", file], patience: TimeSpan.FromSeconds(10));
            return said.Exit == 0 ? said.Output.Trim() : string.Empty;
        }, last =>
        {
            if (asked != generation)
                return;
            values[GIT_COMMIT] = new StringValue(last.Length > 0 ? last : Said("never_committed"));
            page.Refresh(GIT_COMMIT);
        }, quiet: true);
    }

    // How git's state of a file is said, its change and whether it's staged
    private string StatusText(GitFileState state)
    {
        string change = state.Change switch
        {
            GitChange.None => Said("git_unchanged"),
            GitChange.Ignored => Said("git_ignored"),
            GitChange.Untracked => Said("git_untracked"),
            GitChange.Added => Said("git_added"),
            GitChange.Renamed => Said("git_renamed"),
            GitChange.Modified => Said("git_modified"),
            GitChange.Deleted => Said("git_deleted"),
            _ => Said("git_conflicted")
        };

        return state.Staged && state.Unstaged ? Said("git_partly_staged", ("change", change))
            : state.Staged ? Said("git_staged", ("change", change))
            : change;
    }

    /* What a change on the page does */

    private bool IsModified(string key) => key switch
    {
        LANGUAGE => values.GetValueOrDefault(LANGUAGE) is StringValue { Value: not AUTO },
        COPY => values.GetValueOrDefault(COPY) is StringValue { Value: not (Hex.Core.Projects.DotnetEvaluation.NEVER or MIXED) },
        _ => false
    };

    private void Changed(string key, IRuntimeValue value)
    {
        string text = value is StringValue said ? said.Value : string.Empty;
        switch (key)
        {
            case NAME when shown.Count == 1:
                string name = text.Trim();
                if (name.Length > 0 && name != Path.GetFileName(shown[0].TrimEnd('\\', '/')) && Rename?.Invoke(shown[0], name) == true)
                {
                    string renamed = Path.Combine(Path.GetDirectoryName(shown[0])!, name);
                    if (picked.Count > 0)
                        picked = [renamed];
                    rowsStale = true;
                    context.Window.RefreshRight();
                }
                else
                {
                    page.Refresh(NAME);
                }

                break;
            case LANGUAGE:
                SetLanguage(text);
                break;
            case ENDINGS:
                if ((shown.Count == 1 ? context.Documents.Find(shown[0]) : shownDocument) is { IsText: true } open && open.CrLf != (text == "CRLF"))
                {
                    open.CrLf = text == "CRLF";
                    context.Window[DawnParts.Code] = context.Window[DawnParts.Tabs] = true;
                    context.Notices.Say(Said("line_endings_set", ("name", open.Name), ("ending", text)));
                }

                values[ENDINGS] = new StringValue(text);
                break;
            case BUILD_ACTION or COPY:
                ChangeProject(key, text);
                break;
        }
    }

    private void Reset(string key)
    {
        if (key == LANGUAGE)
            SetLanguage(AUTO);
        else if (key == COPY)
            ChangeProject(COPY, Hex.Core.Projects.DotnetEvaluation.NEVER);
    }

    // A file's language picked, the open one coloured with it straight away, one that isn't open kept for when it is
    private void SetLanguage(string id)
    {
        string? language = id == AUTO ? null : id;
        values[LANGUAGE] = new StringValue(id);
        HexDocument? open = shown.Count == 1 ? context.Documents.Find(shown[0]) : shownDocument;
        if (open is not null && PickLanguage is { } pick)
        {
            pick(open, language);
        }
        else if (shown.Count == 1)
        {
            CodeLanguages.SetOverride(shown[0], language);
            if (context.Settings.Values.SetLanguageOverride(shown[0], language) is { } problem)
                context.Notices.Say(problem, error: true);
        }

        page.Refresh(LANGUAGE);
    }

    private void Pressed(string key, int which)
    {
        if (key == LANGUAGE)
            ChangeLanguageMode?.Invoke();
        else if (key == PROJECT && shownProject is { } project)
            context.Documents.Open(project);
    }

    private string? NoteOf(string key) => key switch
    {
        COPY or BUILD_ACTION when notes.TryGetValue(key, out string? from) => Said("set_in", ("file", Path.GetFileName(from))),
        _ => null
    };

    private string? NoteTipOf(string key) => notes.TryGetValue(key, out string? from) ? Said("set_in_tip", ("file", from)) : null;

    // Where a setting of the project's comes from when that's an import, by the row
    private readonly Dictionary<string, string> notes = new(StringComparer.Ordinal);
}
