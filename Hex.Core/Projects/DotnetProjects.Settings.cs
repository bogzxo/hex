using System.Text;

namespace Hex.Core.Projects;

/// <summary>What changing files' project settings came to, the project as it evaluates now and the files it didn't take for.</summary>
internal sealed record DotnetSettingsResult(DotnetEvaluation Evaluation, bool Written, IReadOnlyList<string> NotTaken);

// A file's Build Action and Copy to Output Directory changed, written into the project file the way Visual Studio
// writes them (ProjectFileEdits), then the SDK asked again whether that's what the project now says. When it isn't (a
// glob's Update further down won, an import set it after the project) the setting goes in an ItemGroup of its own at
// the end of the project, and what still doesn't take is said rather than guessed at. Imported files are never written.
internal sealed partial class DotnetProjects
{
    /// <summary>
    /// Files of a project given a Build Action (null leaves it) and a Copy to Output Directory (null leaves it, Never is
    /// Do not copy and takes Hex's own setting off again), off the editor's thread.
    /// </summary>
    public async Task<DotnetSettingsResult> Apply(string project, IReadOnlyList<string> files, string? buildAction, string? copyToOutput, CancellationToken cancel = default)
    {
        project = Path.GetFullPath(project);
        DotnetEvaluation before = await Evaluate(project, cancel);
        var (original, encoding) = ReadProject(project);

        string text = original;
        foreach (string file in files)
            text = Change(text, before, file, buildAction, copyToOutput);

        bool written = text != original;
        DotnetEvaluation after = written ? await Write(project, text, encoding, cancel) : before;
        List<string> wrong = [.. files.Where(file => !Took(after, file, buildAction, copyToOutput))];

        // What didn't take goes at the end of the project, after whatever won over it
        if (wrong.Count > 0 && copyToOutput is not null)
        {
            string again = text;
            foreach (string file in wrong)
            {
                if (after.BuildItemOf(file) is { } item && item.CopyToOutput != DotnetEvaluation.Normalise(copyToOutput))
                    again = ProjectFileEdits.AppendUpdate(again, item.Type, RelativeTo(project, file), ProjectFileEdits.COPY_TO_OUTPUT, DotnetEvaluation.Normalise(copyToOutput));
            }

            if (again != text)
            {
                after = await Write(project, again, encoding, cancel);
                written = true;
                wrong = [.. files.Where(file => !Took(after, file, buildAction, copyToOutput))];
            }
        }

        // Nothing took at all (an import after the project has the last word), the project goes back to how it was
        if (written && wrong.Count == files.Count)
        {
            after = await Write(project, original, encoding, cancel);
            written = false;
        }

        return new DotnetSettingsResult(after, written, wrong);
    }

    /// <summary>One file's settings changed in the project's text, by what the project evaluated to before.</summary>
    internal static string Change(string text, DotnetEvaluation evaluation, string file, string? buildAction, string? copyToOutput)
    {
        string path = RelativeTo(evaluation.Project, file);
        DotnetItem? item = evaluation.BuildItemOf(file);
        string? type = item?.Type;

        if (buildAction is not null && !buildAction.Equals(type, StringComparison.OrdinalIgnoreCase))
        {
            text = ProjectFileEdits.SetItemType(text, path, type, buildAction);
            type = buildAction;
        }

        if (copyToOutput is not null)
        {
            string copy = DotnetEvaluation.Normalise(copyToOutput);
            if (type is null)
            {
                // A file in no item has to be one to be copied, None is what Visual Studio makes it
                if (copy == DotnetEvaluation.NEVER)
                    return text;
                text = ProjectFileEdits.SetItemType(text, path, null, "None");
                type = "None";
            }

            text = ProjectFileEdits.SetMetadata(text, type, path, ProjectFileEdits.COPY_TO_OUTPUT, copy == DotnetEvaluation.NEVER ? null : copy);
        }

        return text;
    }

    /// <summary>Whether the project says what was asked of a file now.</summary>
    private static bool Took(DotnetEvaluation evaluation, string file, string? buildAction, string? copyToOutput)
    {
        DotnetItem? item = evaluation.BuildItemOf(file);
        if (buildAction is not null && !buildAction.Equals(item?.Type, StringComparison.OrdinalIgnoreCase))
            return false;
        return copyToOutput is null || (item?.CopyToOutput ?? DotnetEvaluation.NEVER) == DotnetEvaluation.Normalise(copyToOutput);
    }

    /// <summary>A file as the project writes it, from the project's folder with backslashes, the way MSBuild has them.</summary>
    internal static string RelativeTo(string project, string file) =>
        Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(project))!, Path.GetFullPath(file)).Replace('/', '\\');

    // The project's text, and the encoding it was in, with or without the BOM it had
    private static (string Text, Encoding Encoding) ReadProject(string project)
    {
        byte[] bytes = File.ReadAllBytes(project);
        bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        return (new UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)), new UTF8Encoding(bom));
    }

    // The project written back as it was read, and evaluated again
    private async Task<DotnetEvaluation> Write(string project, string text, Encoding encoding, CancellationToken cancel)
    {
        await File.WriteAllTextAsync(project, text, encoding, cancel);
        Forget(project);
        return await Evaluate(project, cancel);
    }

    /// <summary>
    /// Where a setting of a file comes from when the project's own text doesn't say it, the Directory.Build.props or
    /// .targets over it that mentions it, or else the file its item was made in. Null when the project says it itself or
    /// it's the default.
    /// </summary>
    public static string? ImportedFrom(DotnetEvaluation evaluation, string file, string metadata)
    {
        if (evaluation.BuildItemOf(file) is not { } item || !item.Metadata.TryGetValue(metadata, out string? value) || value.Length == 0)
            return null;

        string path = RelativeTo(evaluation.Project, file);
        try
        {
            if (ProjectFileEdits.SaysMetadata(File.ReadAllText(evaluation.Project), path, metadata))
                return null;

            for (string? folder = evaluation.Folder; folder is not null; folder = Path.GetDirectoryName(folder))
            {
                foreach (string name in Imported)
                {
                    string import = Path.Combine(folder, name);
                    if (File.Exists(import) && File.ReadAllText(import).Contains(metadata, StringComparison.OrdinalIgnoreCase))
                        return import;
                }
            }
        }
        catch (IOException)
        {
        }

        return item.DefinedIn is { Length: > 0 } defined && !PlatformPaths.Same(defined, evaluation.Project) ? defined : null;
    }
}
