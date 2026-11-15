using System.Numerics;

using Hex.Core.Projects;

using Horizon.Rendering;

namespace Hex.Dawn;

// New Project, a window of the kinds of project there are down the left (a .NET console app, Python, Java with Maven,
// C++ with CMake and the rest of ProjectTemplates, then what extensions added) with a box over them to narrow them by
// name, and on the right what the kind picked is, what it'll be called, where it goes and whether its language server is
// on the machine, with Install right there when it isn't. Along the bottom under a rule the folder it makes (or what's
// wrong) and Cancel and Create. Create makes it off the editor's thread (ProjectCreation) and opens the folder with its
// main file up, so the server starts on it straight away.
internal sealed partial class DawnScene
{
    private const float NEW_PROJECT_LIST = 240.0f, NEW_PROJECT_FORM = 440.0f, NEW_PROJECT_FIELD = 30.0f;
    private const float NEW_PROJECT_TALL = 372.0f, NEW_PROJECT_GAP = 24.0f, NEW_PROJECT_BROWSE = 100.0f, NEW_PROJECT_SMALL = 0.14f;

    // What a new project is called when nothing's been typed, a number after it when that folder is taken
    private const string NEW_PROJECT_NAME = "MyProject";

    private WindowPanel? newProjectWindow;
    private TextBox? newProjectName, newProjectPlace, newProjectFilter;
    private ToggleButton? newProjectOptional, newProjectGit;
    private Label? newProjectTitle, newProjectAbout, newProjectServer, newProjectSaid;
    private Button? newProjectInstall, newProjectCreate;
    private readonly List<(ProjectTemplate Template, Button Row)> newProjectRows = [];
    private ProjectTemplate newProjectKind = ProjectTemplates.All[0];

    // The whole of what the line along the bottom says, it's shown cut short when it's long
    private string newProjectLine = string.Empty;

    // Whether the name was typed rather than the one put in, a name put in follows the location to a free folder
    private bool newProjectNamed;

    // Where the last one went, the next goes beside it, for the session
    private string? newProjectWhere;

    /// <summary>The window, null while it isn't up or is on its way out. For the self test.</summary>
    internal WindowPanel? NewProjectWindow => newProjectWindow is { Parent: not null, Leaving: false } up ? up : null;

    /// <summary>The kind picked in it.</summary>
    internal ProjectTemplate NewProjectKind => newProjectKind;

    /// <summary>The window for a new project, over the editor.</summary>
    internal void ShowNewProject()
    {
        newProjectWindow?.Parent?.Remove(newProjectWindow);
        newProjectRows.Clear();
        newProjectNamed = false;

        // A kind an extension took away since is gone from the list, the first is picked instead
        if (!ProjectTemplates.All.Contains(newProjectKind))
            newProjectKind = ProjectTemplates.All[0];

        var window = newProjectWindow = new WindowPanel(Said("new_project.title"))
        {
            Icon = "new-folder",
            Modal = true,
            Size = new Vector2(NEW_PROJECT_LIST + NEW_PROJECT_GAP + NEW_PROJECT_FORM + 41.0f, 0.0f),
            ResizeGrip = false,
            Padding = new UIEdges(20.0f, 16.0f, 20.0f, 18.0f),
            Intro = UIIntro.Pop,
            IntroTime = 0.18f
        };
        window.OnClosed = () => window.Parent?.Remove(window);

        // The two columns, then the rule and the foot under them, one after the other
        var body = window.Add(new StackPanel { Spacing = 14.0f, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft });
        var columns = body.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = NEW_PROJECT_GAP / 2.0f, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft });
        BuildProjectKinds(columns);
        columns.Add(new Divider { Direction = UIDirection.Vertical, Size = new Vector2(1.0f, NEW_PROJECT_TALL + NEW_PROJECT_FIELD + 8.0f), Anchor = Origin.TopLeft, Pivot = Origin.TopLeft });
        BuildProjectForm(columns);

        float across = NEW_PROJECT_LIST + NEW_PROJECT_GAP + NEW_PROJECT_FORM + 1.0f;
        body.Add(new Divider { Size = new Vector2(across, 1.0f), Anchor = Origin.Left, Pivot = Origin.Left });

        // What it says along the bottom on the left (the folder it makes, or what's wrong), Cancel and Create on the right
        var foot = body.Add(new Panel { Size = new Vector2(across, NEW_PROJECT_FIELD), Anchor = Origin.Left, Pivot = Origin.Left });
        newProjectSaid = foot.Add(new Label { Size = new Vector2(across - 230.0f, NEW_PROJECT_FIELD), Anchor = Origin.Left, Pivot = Origin.Left, Align = Origin.Left, TextScale = NEW_PROJECT_SMALL });
        var buttons = foot.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 10.0f, Anchor = Origin.Right, Pivot = Origin.Right });
        buttons.Add(new Button { Label = Said("new_project.cancel"), Size = new Vector2(100.0f, NEW_PROJECT_FIELD), OnPressed = window.Close });
        newProjectCreate = buttons.Add(new Button { Label = Said("new_project.create"), Primary = true, Size = new Vector2(100.0f, NEW_PROJECT_FIELD), OnPressed = CreateProject });

        PickProjectKind(newProjectKind);
        DressWindow(window);
        chrome.Root.Add(window);

        // The name is put in and selected, so typing one replaces it and Enter takes it as it is
        newProjectName!.Focus();
        newProjectName.SelectAll();
    }

    // The kinds down the left, a box to narrow them over a list that scrolls when extensions have added more than fit
    private void BuildProjectKinds(StackPanel columns)
    {
        var side = columns.Add(new StackPanel { Spacing = 8.0f, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Size = new Vector2(NEW_PROJECT_LIST, 0.0f) });
        newProjectFilter = side.Add(new TextBox
        {
            Size = new Vector2(NEW_PROJECT_LIST, NEW_PROJECT_FIELD),
            Placeholder = Said("new_project.search"),
            LeadIcon = "search",
            MaxLength = 64,
            OnChanged = FilterProjectKinds,
            OnSubmitted = _ => newProjectName?.Focus()
        });

        var scroll = side.Add(new ScrollPanel { Size = new Vector2(NEW_PROJECT_LIST, NEW_PROJECT_TALL), Anchor = Origin.TopLeft, Pivot = Origin.TopLeft });
        var list = scroll.Add(new StackPanel { Spacing = 2.0f, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Size = new Vector2(NEW_PROJECT_LIST - 10.0f, 0.0f) });
        foreach (ProjectTemplate template in ProjectTemplates.All)
        {
            ProjectTemplate picked = template;
            var row = list.Add(new Button
            {
                Label = KindTitle(template),
                Icon = HexExplorer.FileIcon(template.Main),
                Style = "button_flat",
                Align = Origin.Left,
                Animated = false,
                Size = new Vector2(NEW_PROJECT_LIST - 10.0f, 32.0f),
                Tooltip = template.Language,
                OnPressed = () => PickProjectKind(picked)
            });
            newProjectRows.Add((template, row));
        }
    }

    // What the kind is, its name, where it goes and what comes with it, down the right
    private void BuildProjectForm(StackPanel columns)
    {
        var form = columns.Add(new StackPanel { Spacing = 6.0f, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Size = new Vector2(NEW_PROJECT_FORM, 0.0f) });
        newProjectTitle = form.Add(new Label { Bold = true, TextScale = 0.19f, Align = Origin.Left, Anchor = Origin.Left, Pivot = Origin.Left });
        newProjectAbout = form.Add(new Label { Wrap = true, Size = new Vector2(NEW_PROJECT_FORM, 0.0f), Align = Origin.TopLeft, Anchor = Origin.Left, Pivot = Origin.Left, Dim = true });
        form.Add(new Spacer { Space = 10.0f });

        newProjectName = new TextBox
        {
            Size = new Vector2(NEW_PROJECT_FORM, NEW_PROJECT_FIELD),
            Placeholder = Said("new_project.name_hint"),
            MaxLength = 64,
            OnChanged = _ =>
            {
                newProjectNamed = true;
                CheckNewProject();
            },
            OnSubmitted = _ => CreateProject()
        };
        form.Add(NewProjectCaption(Said("new_project.name")));
        form.Add(newProjectName);
        form.Add(new Spacer { Space = 6.0f });

        newProjectPlace = new TextBox
        {
            Size = new Vector2(NEW_PROJECT_FORM - NEW_PROJECT_BROWSE - 8.0f, NEW_PROJECT_FIELD),
            MaxLength = 400,
            Text = newProjectWhere ?? UsualProjectPlace(),
            OnChanged = _ => CheckNewProject(),
            OnSubmitted = _ => CreateProject()
        };
        form.Add(NewProjectCaption(Said("new_project.location")));
        var placeLine = form.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 8.0f, Anchor = Origin.Left, Pivot = Origin.Left, Size = new Vector2(NEW_PROJECT_FORM, NEW_PROJECT_FIELD) });
        placeLine.Add(newProjectPlace);
        placeLine.Add(new Button { Label = Said("new_project.browse"), Icon = "folder-opened", Size = new Vector2(NEW_PROJECT_BROWSE, NEW_PROJECT_FIELD), Anchor = Origin.Left, Pivot = Origin.Left, OnPressed = BrowseProjectPlace });
        form.Add(new Spacer { Space = 10.0f });

        newProjectOptional = form.Add(new ToggleButton(Said("new_project.venv"), startingState: true) { OnPressed = CheckNewProject, Anchor = Origin.Left, Pivot = Origin.Left });
        newProjectGit = form.Add(new ToggleButton(Said("new_project.git"), startingState: ProjectCreation.Has("git")) { Anchor = Origin.Left, Pivot = Origin.Left });
        form.Add(new Spacer { Space = 10.0f });

        // Whether the language's server is here, and the way to get it when it isn't
        newProjectServer = form.Add(new Label { Wrap = true, Size = new Vector2(NEW_PROJECT_FORM, 0.0f), Align = Origin.TopLeft, Anchor = Origin.Left, Pivot = Origin.Left, TextScale = NEW_PROJECT_SMALL });
        newProjectInstall = form.Add(new Button { Size = new Vector2(220.0f, 28.0f), Icon = "cloud-download", Anchor = Origin.Left, Pivot = Origin.Left, OnPressed = InstallProjectServer });
    }

    // The key a kind's texts are under, its id with underscores
    private static string KindKey(ProjectTemplate template) => template.Id.Replace('-', '_');

    // What a kind's row says, an extension's own title or the texts' for one of Hex's
    private string KindTitle(ProjectTemplate template) => template.BuiltIn ? Said("new_project.kind_" + KindKey(template)) : template.Title;

    private string KindAbout(ProjectTemplate template)
    {
        if (template.BuiltIn)
            return Said("new_project.about_" + KindKey(template));

        string from = extensions.All.FirstOrDefault(extension => extension.Id == template.Owner)?.Name ?? template.Owner;
        string by = Said("new_project.from_extension", ("extension", from));
        return template.About.Length > 0 ? template.About + " " + by : by;
    }

    private Label NewProjectCaption(string text) => new() { Text = text, TextScale = NEW_PROJECT_SMALL, Align = Origin.Left, Anchor = Origin.Left, Pivot = Origin.Left };

    // The rows narrowed to the ones whose name or language has what's typed, the one picked moved to the first left
    private void FilterProjectKinds(string typed)
    {
        string wanted = typed.Trim();
        ProjectTemplate? first = null;
        foreach (var (template, row) in newProjectRows)
        {
            row.Visible = wanted.Length == 0
                || row.Label.Contains(wanted, StringComparison.OrdinalIgnoreCase)
                || template.Language.Contains(wanted, StringComparison.OrdinalIgnoreCase);
            if (row.Visible)
                first ??= template;
        }

        if (first is not null && !newProjectRows.Any(pair => pair.Template == newProjectKind && pair.Row.Visible))
            PickProjectKind(first);
        else
            CheckNewProject();
    }

    /// <summary>A kind picked, its row lit, what it is said and whether its server and tools are here looked at.</summary>
    internal void PickProjectKind(ProjectTemplate template)
    {
        newProjectKind = template;
        foreach (var (each, row) in newProjectRows)
            row.Selected = each == template;

        if (newProjectTitle is not null)
            newProjectTitle.Text = KindTitle(template);
        if (newProjectAbout is not null)
            newProjectAbout.Text = KindAbout(template);
        if (newProjectOptional is not null)
        {
            newProjectOptional.Visible = template.Optional.Length > 0 || template.OptionalLabel.Length > 0;
            newProjectOptional.Label = template.OptionalLabel.Length > 0 ? template.OptionalLabel : Said("new_project.venv");
        }

        ShowProjectServer();
        PutProjectName();
        CheckNewProject();
    }

    // The name put in, MyProject or the first MyProject2, 3... whose folder is free, while nobody has typed one
    private void PutProjectName()
    {
        if (newProjectName is null || newProjectNamed)
            return;

        string place = newProjectPlace?.Text.Trim() ?? string.Empty;
        string name = NEW_PROJECT_NAME;
        for (int number = 2; number < 100 && place.Length > 0 && NewProjectFolderAt(place, name) is { } folder && !ProjectCreation.FolderFree(folder); number++)
            name = NEW_PROJECT_NAME + number.ToString(System.Globalization.CultureInfo.InvariantCulture);

        newProjectName.Text = name;
        newProjectNamed = false;
    }

    // The kind's language server, found where the settings say it is or on the PATH, and Install when it isn't. A kind
    // of an extension's with no server Hex knows says nothing about one
    private void ShowProjectServer()
    {
        if (newProjectServer is null || newProjectInstall is null)
            return;

        HexServerEntry? entry = newProjectKind.Server.Length > 0 ? languages.ServerEntry(newProjectKind.Server) : null;
        LanguageServers.Registration? registration = LanguageServers.RegistrationFor(newProjectKind.Main);
        if (entry is null && registration is null)
        {
            newProjectServer.Visible = newProjectInstall.Visible = false;
            return;
        }

        string command = registration?.Command ?? entry?.Command ?? newProjectKind.Server;
        bool here = registration is not null && LanguageServers.IsAvailable(registration.Command);
        string name = Path.GetFileNameWithoutExtension(command);

        newProjectServer.Visible = true;
        newProjectServer.Text = here
            ? Said("new_project.server_found", ("command", name))
            : Said("new_project.server_missing", ("command", name));
        newProjectServer.Color = here ? compositor.Skin?.SuccessColor : compositor.Skin?.WarningColor;
        newProjectInstall.Visible = !here && entry is not null;
        newProjectInstall.Label = entry is { Install.Length: 0 } ? Said("new_project.get_server", ("command", name)) : Said("new_project.install_server", ("command", name));
    }

    private void InstallProjectServer()
    {
        if (languages.ServerEntry(newProjectKind.Server) is { } entry)
            languages.InstallServer(entry);
    }

    // Browse beside the location, the folder picked is where the project's own folder goes
    private void BrowseProjectPlace() => Context.Windows.ChooseFolder(picked =>
    {
        if (newProjectPlace is null)
            return;

        newProjectPlace.Text = picked;
        PutProjectName();
        CheckNewProject();
    });

    /// <summary>
    /// Where a new project goes when nothing says otherwise, beside the folder that's open (the folder of projects it's
    /// in), or source/repos in the home folder where there is one, Visual Studio's, or the home folder.
    /// </summary>
    private string UsualProjectPlace()
    {
        if (!explorer.workspace.IsEmpty && Path.GetDirectoryName(Path.GetFullPath(explorer.workspace.Folders[0])) is { } beside)
            return beside;

        string repos = Path.Combine(PlatformPaths.Home, "source", "repos");
        return Directory.Exists(repos) ? repos : PlatformPaths.Home;
    }

    /// <summary>The folder the project would be made as, null while the name or the location won't do.</summary>
    private string? NewProjectFolder()
    {
        string name = newProjectName?.Text.Trim() ?? string.Empty, place = newProjectPlace?.Text.Trim() ?? string.Empty;
        return ProjectTemplates.Check(name) != ProjectNameProblem.None || place.Length == 0 ? null : NewProjectFolderAt(place, name);
    }

    // A name's folder in a place, ~ the home folder the way the file dialog takes it, null for a place that isn't a full path
    private static string? NewProjectFolderAt(string place, string name)
    {
        try
        {
            if (place == "~" || place.StartsWith("~/", StringComparison.Ordinal) || place.StartsWith("~\\", StringComparison.Ordinal))
                place = PlatformPaths.Home + place[1..];
            return Path.IsPathFullyQualified(place) ? Path.GetFullPath(Path.Combine(place, name)) : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    // What's typed checked as it's typed, the line at the bottom says what's wrong or where it goes, and Create is only
    // there for something that'll work. An empty name isn't scolded before anybody has typed in the box
    private void CheckNewProject()
    {
        if (newProjectSaid is null || newProjectCreate is null || newProjectName is null)
            return;

        string? problem = NewProjectProblem(out string? folder);
        bool quiet = !newProjectNamed && newProjectName.Text.Trim().Length == 0;
        string said = problem is null ? Said("new_project.creates", ("folder", folder!)) : quiet ? string.Empty : problem;
        newProjectLine = said;
        newProjectSaid.Text = FitNewProjectLine(said, newProjectSaid.Size.X);
        newProjectSaid.Color = problem is null ? compositor.Skin?.DimTextColor : compositor.Skin?.ErrorColor;
        newProjectCreate.Enabled = problem is null;
    }

    // The line along the bottom cut in the middle with dots when it's longer than its room, so a deep folder keeps its
    // start and the project's own name at its end and never runs under Cancel and Create
    private string FitNewProjectLine(string text, float room)
    {
        if (compositor.Skin is not { } skin || skin.Font.Measure(text, NEW_PROJECT_SMALL, markup: false).X <= room)
            return text;

        const string cut = "...";
        float dots = skin.Font.Measure(cut, NEW_PROJECT_SMALL, markup: false).X;
        int low = 0, high = text.Length;
        while (low < high)
        {
            int keep = (low + high + 1) / 2;
            int head = (keep + 1) / 2, tail = keep - head;
            float wide = skin.Font.Measure(text.AsSpan(0, head), NEW_PROJECT_SMALL, markup: false).X + dots
                + skin.Font.Measure(text.AsSpan(text.Length - tail), NEW_PROJECT_SMALL, markup: false).X;
            if (wide <= room)
                low = keep;
            else
                high = keep - 1;
        }

        int start = (low + 1) / 2;
        return text[..start] + cut + text[(text.Length - (low - start))..];
    }

    // What's wrong with what's in the window, null for nothing, and the folder it'd make when nothing is
    private string? NewProjectProblem(out string? folder)
    {
        folder = null;

        // Narrowed to nothing, the kind on the right is one that isn't in the list, so it isn't made
        if (newProjectRows.Count > 0 && !newProjectRows.Any(pair => pair.Template == newProjectKind && pair.Row.Visible))
            return Said("new_project.no_match");

        string name = newProjectName?.Text.Trim() ?? string.Empty;
        switch (ProjectTemplates.Check(name))
        {
            case ProjectNameProblem.Empty: return Said("new_project.name_empty");
            case ProjectNameProblem.BadStart: return Said("new_project.name_start");
            case ProjectNameProblem.BadCharacter: return Said("new_project.name_characters");
            case ProjectNameProblem.TooLong: return Said("new_project.name_long");
        }

        if (NewProjectFolder() is not { } made)
            return Said("new_project.no_location");
        folder = made;
        if (!ProjectCreation.FolderFree(made))
            return Said("new_project.taken", ("folder", made));
        if (!ProjectCreation.Has(newProjectKind.Tool))
            return Said("new_project.no_tool", ("tool", newProjectKind.Tool));

        // A step wanted with no program for it here would fail half way, it's said up front instead
        if (newProjectOptional is { Visible: true, State: true } && !ProjectCreation.Has(newProjectKind.Optional))
            return Said("new_project.no_tool", ("tool", newProjectKind.Optional));
        return null;
    }

    /// <summary>Create, the window down and the project made in the background, its folder opened when it's there.</summary>
    internal void CreateProject()
    {
        if (newProjectWindow is null || NewProjectProblem(out string? folder) is { } problem)
        {
            if (newProjectWindow is not null)
            {
                newProjectNamed = true;
                CheckNewProject();
            }
            return;
        }

        string name = newProjectName!.Text.Trim();
        bool optional = newProjectOptional is { Visible: true, State: true };

        // An extension's project is worked out here, its HIDL runs on the editor's thread and nowhere else
        ProjectContents contents;
        try
        {
            contents = ProjectTemplates.Contents(newProjectKind, name, optional);
        }
        catch (InvalidOperationException e)
        {
            newProjectSaid!.Text = FitNewProjectLine(newProjectLine = e.Message, newProjectSaid.Size.X);
            newProjectSaid.Color = compositor.Skin?.ErrorColor;
            return;
        }

        var request = new ProjectRequest(newProjectKind, name, folder!, optional, newProjectGit?.State == true, contents);
        newProjectWhere = Path.GetDirectoryName(folder!);
        newProjectWindow.Close();

        Context.Work.InBackground(Said("new_project.creating", ("name", name)), () => ProjectCreation.Make(request, _ => { }), failed =>
        {
            if (failed is not null)
            {
                Say(Said("new_project.failed", ("name", name), ("why", failed)), error: true);
                return;
            }

            explorer.OpenFolder(request.Folder);
            string main = Path.Combine(request.Folder, ProjectTemplates.MainFile(request.Template, name).Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(main))
                Open(main);
            Say(Said("new_project.created", ("name", name), ("folder", request.Folder)));
        });
    }
}
