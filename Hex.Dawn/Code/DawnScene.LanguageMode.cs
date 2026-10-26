namespace Hex.Dawn;

// Change Language Mode, VS Code's. Ctrl+K M or a click on the language in the status bar lists Auto Detect, Plain Text
// and every language there's a grammar for, and the one picked is what the file is from then on, kept in the folder's
// settings by its path. Configure File Association does the same for every file of an extension, written to the
// user's settings as files_associations ("*.weird" to python). What decides a language lives in the engine
// (CodeLanguages.Files), this puts the settings in force there and colours the open files again when they change.
internal sealed partial class DawnScene
{
    // What the associations and the overrides were last put in force from, the settings and a hash of the one, how many
    // times the other had changed
    private HexSettings? languageModesOf;
    private int associationsHash = -1, overridesShown = -1;

    // The paths given an override from the settings, taken back when the settings say otherwise
    private readonly HashSet<string> overridesPut = new(StringComparer.OrdinalIgnoreCase);

    // How many times what decides a language had changed when the open files were last coloured for it
    private int languageVersionShown = -1;

    private void AddLanguageModeCommands()
    {
        commands.Add("View/look", Said("language_mode.change"), ChangeLanguageMode, "Ctrl+K M", "symbol-misc", UIKeyUse.BoundWhileTyping).IsEnabled = () => !LayoutTools && document.IsText;
        UpdateLanguageModes();
    }

    /// <summary>
    /// Every tick, the associations and the overrides of the settings put in force once they changed (by a row, the
    /// settings file, a folder opening), and every open file coloured again when what decides its language did.
    /// </summary>
    private void UpdateLanguageModes()
    {
        int hash = AssociationsHash(settings.FileAssociations);
        if (!ReferenceEquals(languageModesOf, settings) || hash != associationsHash)
        {
            associationsHash = hash;
            CodeLanguages.SetAssociations(settings.FileAssociations.Select(pair => (pair.Key, pair.Value)));
        }

        if (!ReferenceEquals(languageModesOf, settings) || overridesShown != settings.LanguageOverridesChanged)
        {
            overridesShown = settings.LanguageOverridesChanged;
            foreach (string path in overridesPut)
                CodeLanguages.SetOverride(path, null);
            overridesPut.Clear();
            foreach (var (path, id) in settings.LanguageOverrides)
            {
                CodeLanguages.SetOverride(path, id);
                overridesPut.Add(path);
            }
        }

        languageModesOf = settings;
        if (languageVersionShown == CodeLanguages.Version)
            return;

        languageVersionShown = CodeLanguages.Version;
        foreach (HexDocument open in documents)
        {
            // A picture shown as one has no colours, whatever its name says (an .svg would be XML)
            if (open.IsText && open.Picture is null && (open.Path ?? open.DiffOf) is { } path)
                UseLanguage(open, CodeLanguages.ForFile(path, FirstLine(open)));
        }
    }

    // The first line of an open file, what a #! is looked for in when its name says nothing
    private static string FirstLine(HexDocument of) => of.Editor.Document.LineCount > 0 ? of.Editor.Document.LineAt(0) : string.Empty;

    // A hash of the associations that changes when any of them does, made without a string or a list every tick
    private static int AssociationsHash(Dictionary<string, string> associations)
    {
        int hash = associations.Count;
        foreach (var (glob, id) in associations)
            hash ^= HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(glob), StringComparer.OrdinalIgnoreCase.GetHashCode(id));
        return hash;
    }

    /// <summary>A file coloured with another language, its language server told, nothing done when it's the one it has.</summary>
    private void UseLanguage(HexDocument of, ICodeHighlighter? language)
    {
        if (ReferenceEquals(of.Editor.Highlighter, language))
            return;

        of.Editor.Highlighter = language;
        of.Editor.CheckSyntax = ReferenceEquals(language, CodeLanguages.Hidl) && of.DiffOf is null;
        languages.LanguageChanged(of);
        languageFor = null;
    }

    /// <summary>
    /// Change Language Mode, the languages there are to pick from for the file on screen, Auto Detect saying what it
    /// would be, the one it is now marked.
    /// </summary>
    private void ChangeLanguageMode()
    {
        if (LayoutTools || !document.IsText)
            return;

        HexDocument of = document;
        string? path = of.Path;
        string now = CodeLanguages.LanguageIdOf(of.Editor.Highlighter);
        string Marked(string text, bool current) => current ? Said("language_mode.current", ("row", text)) : text;

        ICodeHighlighter? detected = path is null ? null : CodeLanguages.Detect(path, FirstLine(of));
        var choices = new List<(string Text, Action Picked)>
        {
            (Said("language_mode.auto_detect", ("language", detected?.Name ?? Said("language_mode.plain_text"))), () => PickLanguage(of, null)),
            (Marked(Said("language_mode.plain_text"), now == CodeLanguages.PLAIN_TEXT), () => PickLanguage(of, CodeLanguages.PLAIN_TEXT))
        };

        string extension = path is null ? string.Empty : Path.GetExtension(CodeLanguages.WithoutBackupSuffix(path));
        if (extension.Length > 0)
            choices.Add((Said("language_mode.configure", ("extension", extension)), () => ConfigureFileAssociation(extension)));

        foreach (CodeLanguage language in AllLanguages())
            choices.Add((Marked($"{language.Name} ({language.Id})", language.Id == now), () => PickLanguage(of, language.Id)));

        ChooseFrom(Said("language_mode.title"), Said("language_mode.text", ("name", of.Path is { } file ? Path.GetFileName(file) : of.Name)), choices);
    }

    // Every language there's a grammar for with HIDL among them, which the engine's list leaves out (its server is Hex)
    private static IEnumerable<CodeLanguage> AllLanguages() =>
        CodeLanguages.Languages().Append(new CodeLanguage("HIDL", "hidl", [".hor"])).OrderBy(language => language.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A file's language picked by hand, null for worked out again, kept in the folder's settings when it has a path, and
    /// the file coloured with it straight away.
    /// </summary>
    private void PickLanguage(HexDocument of, string? languageId)
    {
        ICodeHighlighter? language;
        if (of.Path is { } path)
        {
            CodeLanguages.SetOverride(path, languageId);
            if (settings.SetLanguageOverride(path, languageId) is { } problem)
                Say(problem, error: true);

            // The settings say what was just put in force, nothing to do again next tick
            overridesShown = settings.LanguageOverridesChanged;
            string full = Path.GetFullPath(path);
            if (languageId is null)
                overridesPut.Remove(full);
            else
                overridesPut.Add(full);
            language = CodeLanguages.ForFile(path, FirstLine(of));
        }
        else
        {
            language = languageId is null ? CodeLanguages.ForFile(of.Name) : CodeLanguages.ForLanguageId(languageId);
        }

        UseLanguage(of, language);
        Say(Said("language_mode.changed", ("name", of.Path is { } file ? Path.GetFileName(file) : of.Name), ("language", language?.Name ?? Said("language_mode.plain_text"))));
    }

    /// <summary>
    /// Configure File Association, the languages again, and the one picked is what every file of the extension is from
    /// then on, files_associations in the user's settings, the open ones coloured again with it.
    /// </summary>
    private void ConfigureFileAssociation(string extension)
    {
        string glob = "*" + extension;
        settings.FileAssociations.TryGetValue(glob, out string? now);
        var choices = new List<(string Text, Action Picked)>
        {
            (Said("language_mode.plain_text"), () => AssociateFiles(glob, CodeLanguages.PLAIN_TEXT))
        };
        foreach (CodeLanguage language in AllLanguages())
        {
            string text = $"{language.Name} ({language.Id})";
            choices.Add((language.Id == now ? Said("language_mode.current", ("row", text)) : text, () => AssociateFiles(glob, language.Id)));
        }

        // The pick closed the first chooser before it ran, this one opens in its place
        ChooseFrom(Said("language_mode.configure_title", ("extension", extension)), Said("language_mode.configure_text", ("glob", glob)), choices);
    }

    // Helper method to keep an association in the user's settings and put it in force
    private void AssociateFiles(string glob, string languageId)
    {
        settings.FileAssociations[glob] = languageId;
        SaveSettings();
        UpdateLanguageModes();
        Say(Said("language_mode.associated", ("glob", glob), ("language", CodeLanguages.ForLanguageId(languageId)?.Name ?? Said("language_mode.plain_text"))));
    }
}
