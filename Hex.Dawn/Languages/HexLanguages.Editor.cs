using System.Numerics;

using Horizon.Rendering;

namespace Hex.Dawn;

// The panel under the language servers' rows in the settings. Every server there is, a line each, what language it's
// for, its files, where it is and what was found of it, the ones added here with Edit and Remove. Under them the way to
// add one, a language of the ones Hex colours picked from a list, the files it's for (".d|.di", a bar between two), the
// program with a Browse to find it, and what it's started with if it wants anything.
internal sealed partial class HexLanguages
{
    private const float EDITOR_WIDTH = 640.0f, CAPTION_WIDTH = 110.0f, FIELD_HEIGHT = 28.0f, ROW_BUTTON_WIDTH = 74.0f;

    // How wide every box of the form is, the server's with Browse after it, so their left and right edges line up
    private const float BROWSE_WIDTH = 100.0f, FIELD_WIDTH = EDITOR_WIDTH - CAPTION_WIDTH - BROWSE_WIDTH - 10.0f;

    // The panel while the settings are up, the rows of the servers in it, and the form for adding one
    private StackPanel? serverRows;
    private Dropdown? addLanguage;
    private TextBox? addExtensions, addCommand, addArguments;
    private Label? addSaid;

    // The languages Hex colours, by name, read the first time they're wanted
    private IReadOnlyList<CodeLanguage>? codeLanguages;
    private IReadOnlyList<CodeLanguage> CodeLanguagesList => codeLanguages ??= CodeLanguages.Languages();

    /// <summary>What a server's language is called, by its id, its name in language_servers.hor for one Hex doesn't colour.</summary>
    private string LanguageNameOf(HexServerEntry entry) =>
        CodeLanguagesList.FirstOrDefault(language => language.Id.Equals(entry.LanguageId, StringComparison.OrdinalIgnoreCase))?.Name ?? entry.Name;

    /// <summary>The panel of the servers and the form that adds one, for the section under the servers' rows.</summary>
    private StackPanel MakeServerEditor()
    {
        var panel = new StackPanel { Spacing = 8.0f, Padding = new UIEdges(14.0f, 10.0f, 10.0f, 14.0f) };
        panel.Add(Heading(context.Strings.Said("language_settings.on_this_machine")));
        serverRows = panel.Add(new StackPanel { Spacing = 6.0f, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft });
        FillServerRows();

        panel.Add(new Spacer { Size = new Vector2(EDITOR_WIDTH, 8.0f) });
        panel.Add(Heading(context.Strings.Said("language_settings.add_title")));
        panel.Add(new Label
        {
            Text = context.Strings.Said("language_settings.add_help"),
            Wrap = true,
            Size = new Vector2(EDITOR_WIDTH, 0.0f),
            Align = Origin.TopLeft,
            Anchor = Origin.TopLeft,
            Pivot = Origin.TopLeft,
            Color = context.Window.Compositor.Skin?.DimTextColor
        });

        string[] names = [.. CodeLanguagesList.Select(language => language.Name)];
        addLanguage = new Dropdown(names) { Size = new Vector2(FIELD_WIDTH, FIELD_HEIGHT), MaxRows = 12, OnChanged = LanguagePicked };
        panel.Add(Field(context.Strings.Said("language_settings.language"), addLanguage));

        addExtensions = new TextBox { Size = new Vector2(FIELD_WIDTH, FIELD_HEIGHT), Placeholder = ".d|.di", MaxLength = 200 };
        panel.Add(Field(context.Strings.Said("language_settings.extensions"), addExtensions));

        addCommand = new TextBox { Size = new Vector2(FIELD_WIDTH, FIELD_HEIGHT), Placeholder = context.Strings.Said("language_settings.server_hint"), MaxLength = 1024 };
        var browse = new Button { Label = context.Strings.Said("language_settings.browse"), Size = new Vector2(BROWSE_WIDTH, FIELD_HEIGHT), OnPressed = BrowseForServer };
        panel.Add(Field(context.Strings.Said("language_settings.server"), addCommand, browse));

        addArguments = new TextBox { Size = new Vector2(FIELD_WIDTH, FIELD_HEIGHT), Placeholder = context.Strings.Said("language_settings.arguments_hint"), MaxLength = 400 };
        panel.Add(Field(context.Strings.Said("language_settings.arguments"), addArguments));

        var foot = panel.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 12.0f, Anchor = Origin.Left, Pivot = Origin.Left });
        foot.Add(new Button { Label = context.Strings.Said("language_settings.add"), Primary = true, Size = new Vector2(120.0f, FIELD_HEIGHT), OnPressed = AddFromForm });
        addSaid = foot.Add(new Label { Size = new Vector2(EDITOR_WIDTH - 132.0f, FIELD_HEIGHT), Align = Origin.Left, Wrap = true });

        // A language to start from, D, the one there's no server for until one's added
        if (names.Length > 0)
        {
            addLanguage.Value = names.Contains("D") ? "D" : names[0];
            LanguagePicked(addLanguage.Value);
        }

        RefreshServerStatus();
        return panel;
    }

    // A heading of the panel, from the left as the rows under it are. Laid in the middle of the panel it sat 5 pixels in
    private static Label Heading(string text) =>
        new() { Text = $"[b]{UIRichText.Literal(text)}[/b]", Markdown = true, Size = new Vector2(EDITOR_WIDTH, 0.0f), Align = Origin.TopLeft, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft };

    // A line of the form, what it is on the left and its control (and a button after it) on the right
    private static StackPanel Field(string caption, UIComponent control, UIComponent? after = null)
    {
        var line = new StackPanel { Direction = UIDirection.Horizontal, Spacing = 10.0f, Anchor = Origin.Left, Pivot = Origin.Left };
        line.Add(new Label { Text = caption, Size = new Vector2(CAPTION_WIDTH, FIELD_HEIGHT), Align = Origin.Left });
        line.Add(control);
        if (after is not null)
            line.Add(after);
        return line;
    }

    /// <summary>Helper method to put a line a server in the rows, what was found of it said in it and written again as that changes.</summary>
    private void FillServerRows()
    {
        if (serverRows is null)
            return;

        foreach (UIComponent row in serverRows.Children.ToArray())
            serverRows.Remove(row);
        serverStatus.Clear();

        foreach (HexServerEntry entry in ServerEntries)
        {
            bool added = IsAdded(entry);
            var row = serverRows.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 8.0f, Anchor = Origin.Left, Pivot = Origin.Left });

            // Every line wraps at one width, room left for Edit and Remove whether a row has them or not. The added ones
            // wrapped shorter than the rest and the list looked ragged
            var line = row.Add(new Label
            {
                Markdown = true,
                Wrap = true,
                Size = new Vector2(EDITOR_WIDTH - (ROW_BUTTON_WIDTH + 8.0f) * 2.0f, 0.0f),
                Align = Origin.TopLeft
            });
            serverStatus[entry.Name] = line;

            if (added)
            {
                HexServerEntry of = entry;
                row.Add(new Button { Label = context.Strings.Said("language_settings.edit"), Size = new Vector2(ROW_BUTTON_WIDTH, FIELD_HEIGHT), OnPressed = () => EditServer(of) });
                row.Add(new Button { Label = context.Strings.Said("language_settings.remove"), Size = new Vector2(ROW_BUTTON_WIDTH, FIELD_HEIGHT), OnPressed = () => RemoveServer(of.LanguageId) });
            }
        }
    }

    // The settings closed, the panel and its form let go of
    private void ForgetServerEditor()
    {
        serverStatus.Clear();
        serverRows = null;
        addLanguage = null;
        addExtensions = addCommand = addArguments = null;
        addSaid = null;
    }

    /// <summary>The rows made again after a server was added or taken away, while the settings are up.</summary>
    private void RebuildServerEditor()
    {
        FillServerRows();
        RefreshServerStatus();
    }

    // A language picked in the form, its files filled in, and what it was added as if it was
    private void LanguagePicked(string name)
    {
        if (CodeLanguagesList.FirstOrDefault(language => language.Name == name) is not { } picked || addExtensions is null)
            return;

        HexAddedServer? was = settings.AddedServers.Find(server => server.Language.Equals(picked.Id, StringComparison.OrdinalIgnoreCase));
        addExtensions.Text = string.Join("|", was?.Extensions ?? picked.Extensions);
        if (was is not null)
        {
            addCommand!.Text = was.Command;
            addArguments!.Text = string.Join(" ", was.Arguments);
        }
    }

    // Edit on a row, the form filled in with what it was added as
    private void EditServer(HexServerEntry entry)
    {
        if (addLanguage is null || CodeLanguagesList.FirstOrDefault(language => language.Id.Equals(entry.LanguageId, StringComparison.OrdinalIgnoreCase)) is not { } language)
            return;

        addLanguage.Value = language.Name;
        LanguagePicked(language.Name);
        addCommand!.Focus();
    }

    // Browse, the program found with Hex's own dialog
    private void BrowseForServer()
    {
        string now = addCommand?.Text.Trim().Trim('"') ?? string.Empty;
        string from = now.Length > 0 && Path.GetDirectoryName(now) is { Length: > 0 } folder && Directory.Exists(folder) ? folder : context.Windows.BrowseFolder;
        (string, string)[] filter = OperatingSystem.IsWindows() ? [(context.Strings.Said("language_settings.programs"), "*.exe;*.cmd;*.bat")] : [];
        context.Windows.ChooseFile(context.Strings.Said("language_settings.where_is", ("command", addLanguage?.Value ?? string.Empty)), from, filter, path =>
        {
            if (addCommand is not null)
                addCommand.Text = Path.GetFullPath(path);
        });
    }

    /// <summary>The files typed for a server, a bar between two, each with its dot whether it was typed or not.</summary>
    internal static string[] ParseExtensions(string typed) =>
        [.. typed.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(extension => extension.TrimStart('*'))
            .Where(extension => extension.Length > 0 && extension != ".")
            .Select(extension => extension.StartsWith('.') ? extension : "." + extension)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    // Add, the form's server added, or what it says is missing
    private void AddFromForm()
    {
        if (addLanguage is null || addExtensions is null || addCommand is null || addArguments is null || addSaid is null)
            return;

        if (CodeLanguagesList.FirstOrDefault(language => language.Name == addLanguage.Value) is not { } language)
        {
            addSaid.Text = context.Strings.Said("language_settings.needs_language");
            return;
        }

        string[] extensions = ParseExtensions(addExtensions.Text);
        if (extensions.Length == 0)
        {
            addSaid.Text = context.Strings.Said("language_settings.needs_extensions");
            return;
        }

        string command = addCommand.Text.Trim().Trim('"');
        if (command.Length == 0)
        {
            addSaid.Text = context.Strings.Said("language_settings.needs_server");
            return;
        }

        string[] arguments = addArguments.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        AddServer(new HexAddedServer(language.Id, language.Name, extensions, command, arguments));
        addSaid.Text = context.Strings.Said("language_settings.added", ("language", language.Name));
    }

    /* What a test fills the form with and presses, the way the UI does */

    internal void FillServerForm(string language, string extensions, string command, string arguments = "")
    {
        if (addLanguage is null)
            return;

        addLanguage.Value = language;
        LanguagePicked(language);
        addExtensions!.Text = extensions;
        addCommand!.Text = command;
        addArguments!.Text = arguments;
    }

    internal void PressAddServer() => AddFromForm();

    /// <summary>The keyboard in the form's server box, which has the settings scroll down to it.</summary>
    internal void FocusServerForm() => addCommand?.Focus();

    /// <summary>What the form last said under Add, empty for nothing.</summary>
    internal string ServerFormSaid => addSaid?.Text ?? string.Empty;
}
