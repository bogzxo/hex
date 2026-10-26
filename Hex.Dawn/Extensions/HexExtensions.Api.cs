using System.Numerics;

using Horizon.HIDL;
using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;

namespace Hex.Dawn;

// What an extension is handed, hex. Everything it can do to Hex is on it and nowhere else, in groups below by what
// it is for, docs/extensions.md says what every one does. One is made for an extension and kept, it was made anew for
// every event an extension heard about, sixty closures a time and every run of a file as it's typed is one.
internal sealed partial class HexExtensions
{
    private readonly Dictionary<HexExtension, ObjectValue> hexObjects = [];

    /// <summary>The hex object of an extension, made the first time it is asked for.</summary>
    internal ObjectValue HexOf(HexExtension extension)
    {
        if (!hexObjects.TryGetValue(extension, out ObjectValue hex))
        {
            hexObjects[extension] = hex = Natives.Object(
            [
                .. Talking(extension), .. Screens(extension), .. Files(extension), .. Asking(extension), .. Prompting(extension),
                .. Sides(), .. Programs(extension), .. About(extension), .. Edits(extension), .. Problems(extension),
                .. Watching(extension), .. Caret(), .. TerminalApi
            ]);
        }

        return hex;
    }

    /// <summary>What the hex object has about the terminal's files, the terminal files' to hand over (HexTerminalFiles.Api).</summary>
    internal (string, IRuntimeValue)[] TerminalApi { get; set; } = [];

    /// <summary>The title bar, the status bar, the cards in the corner and the command palette.</summary>
    private (string, IRuntimeValue)[] Talking(HexExtension extension) =>
    [
        ("title_note", Natives.Action("title_note", args => screens.TitleNote(extension, args.Text(0, string.Empty).ReplaceLineEndings(" ")))),
        ("say", Natives.Action("say", args => context.Notices.Say(context.Strings.Said("extensions_api.said", ("name", extension.Name), ("text", args.Text(0, string.Empty)))))),
        ("notify", Natives.Action("notify", args => context.Notices.Notify(context.Strings.Said("extensions_api.said", ("name", extension.Name), ("text", args.Text(0, string.Empty))), error: args.Bool(1, false)))),
        ("command", Natives.Action("command", args =>
        {
            // In the command palette under the extension's name, as long as it is switched on, and on a key if it says one
            string name = args.Text(0);
            IRuntimeValue function = args.Callable(1);
            string key = args.Text(2, string.Empty);
            if (key.Length > 0 && ReadKey(key) is null)
                throw args.Wrong($"no key called {key}, they're written like Ctrl+Shift+K.");
            extension.Commands.RemoveAll(command => command.Name == name);
            extension.Commands.Add((name, function));
            extension.CommandKeys.RemoveAll(command => command.Name == name);
            if (key.Length > 0)
                extension.CommandKeys.Add((name, key));
        }))
    ];

    /// <summary>What it puts on screen, layouts of its folder, see IExtensionScreens.</summary>
    private (string, IRuntimeValue)[] Screens(HexExtension extension) =>
    [
        ("view", Natives.Action("view", args =>
        {
            ObjectValue made = args.Object(0);
            string id = $"{extension.Id}.{Text(made, "id") ?? throw args.Wrong("a view needs an id.")}";
            screens.AddView(new ExtensionViewKind(extension, id, Text(made, "title") ?? id, Text(made, "icon") ?? "extensions",
                Text(made, "layout") ?? throw args.Wrong("a view needs a layout."), Text(made, "when"), FunctionOf(made, "open"),
                Flag(made, "left") ?? true, Flag(made, "right") ?? true));
        })),
        ("panel", Natives.Function("panel", args =>
        {
            ObjectValue made = args.Object(0);
            UIScriptedLayout? page = screens.MakePanelPage(extension, Text(made, "title") ?? extension.Name, Text(made, "icon") ?? string.Empty,
                Text(made, "layout") ?? throw args.Wrong("a page of the panel needs a layout."));
            return Opened(extension, page, FunctionOf(made, "open"));
        })),
        ("window", Natives.Function("window", args =>
        {
            ObjectValue made = args.Object(0);
            Vector2 size = new(Number(made, "width") ?? 480, Number(made, "height") ?? 360);
            UIScriptedLayout? window = screens.MakeWindow(extension, Text(made, "title") ?? extension.Name,
                Text(made, "layout") ?? throw args.Wrong("a window needs a layout."), size);
            return Opened(extension, window, FunctionOf(made, "open"));
        }))
    ];

    /// <summary>Helper method to hand something an extension made to its open function, what the extension gets back for it.</summary>
    private IRuntimeValue Opened(HexExtension extension, UIScriptedLayout? made, IRuntimeValue? open)
    {
        if (made is null)
            return Values.Null;

        if (open is not null && !extension.Invoke(open, made))
            Failed(extension);
        return made;
    }

    /// <summary>Files, the open ones as they are in Hex and the rest from the disk, the file on screen and pictures.</summary>
    private (string, IRuntimeValue)[] Files(HexExtension extension) =>
    [
        ("read", Natives.Function("read", args => new StringValue(ReadForExtension(PathFor(extension, args.Text(0)))))),
        ("write", Natives.Action("write", args => WriteForExtension(PathFor(extension, args.Text(0)), args.Text(1)))),
        ("exists", Natives.Function("exists", args => Values.Bool(PathFor(extension, args.Text(0)) is var path && (File.Exists(path) || Directory.Exists(path))))),
        ("find_up", Natives.Function("find_up", args => FindUp(PathFor(extension, args.Text(0)), args.Text(1)) is { } found ? new StringValue(found) : Values.Null)),
        ("load", Natives.Function("load", args => LoadForExtension(PathFor(extension, args.Text(0))))),
        ("try_load", Natives.Function("try_load", args => TryLoadForExtension(PathFor(extension, args.Text(0))))),
        ("document", Natives.Function("document", _ => screens.DocumentObject())),
        ("file", Natives.Function("file", _ => context.Documents.Current.Path is { } path ? new StringValue(path) : Values.Null)),
        ("sprite", Natives.Function("sprite", args => Horizon.UI.Drawing.UISprite.Open(PathFor(extension, args.Text(0)))))
    ];

    /// <summary>
    /// What it was told on its tab of the settings, and files and folders picked with Windows' own dialogs, handed to the
    /// function when they have been (a path typed into a box where there are none, anywhere but Windows or Hex testing itself).
    /// </summary>
    private (string, IRuntimeValue)[] Asking(HexExtension extension) =>
    [
        ("setting", Natives.Function("setting", args => SettingOf(extension, args.Text(0)))),
        ("set_setting", Natives.Action("set_setting", args => SetSetting(extension, args.Text(0), args[1]))),
        ("pick_file", Natives.Action("pick_file", args =>
        {
            ObjectValue asked = args.Object(0);
            IRuntimeValue then = args[1];
            string folder = Text(asked, "folder") is { } from ? PathFor(extension, from) : context.Windows.BrowseFolder;
            context.Windows.ChooseFile(Text(asked, "title") ?? context.Strings.Said("extensions_api.pick_file_title", ("name", extension.Name)), folder, [.. FilterOf(asked)], picked => Picked(extension, then, picked));
        })),
        ("pick_folder", Natives.Action("pick_folder", args =>
        {
            IRuntimeValue then = args[0];
            context.Windows.ChooseFolder(picked => Picked(extension, then, picked));
        }))
    ];

    /// <summary>Helper method for pick_file, its filter, a list of what each says and what it matches one after the other.</summary>
    private static List<(string Name, string Pattern)> FilterOf(ObjectValue asked)
    {
        var filter = new List<(string, string)>();
        if (asked.Properties.TryGetValue("filter", out IRuntimeValue? pairs) && pairs is ListValue list)
        {
            for (int i = 0; i + 1 < list.Count; i += 2)
                filter.Add((list[i] is StringValue name ? name.Value : "?", list[i + 1] is StringValue pattern ? pattern.Value : "*.*"));
        }

        return filter;
    }

    /// <summary>Hex's sides, out or away the way Ctrl+B and Ctrl+Alt+B do it, and kept as those are.</summary>
    private (string, IRuntimeValue)[] Sides() =>
    [
        ("show_side", Natives.Action("show_side", args =>
        {
            bool shown = args.Bool(1, true);
            switch (args.Text(0))
            {
                case "left": context.Panels.SideBarShown = shown; break;
                case "right": context.Panels.PropertiesShown = shown; break;
                default: throw args.Wrong("the sides are \"left\" and \"right\".");
            }
        })),
        ("side_shown", Natives.Function("side_shown", args => Values.Bool(args.Text(0) == "left" ? context.Panels.SideBarShown : context.Panels.PropertiesShown)))
    ];

    /// <summary>A program started, a game to try what was edited in, with its arguments and the folder it starts in.</summary>
    private (string, IRuntimeValue)[] Programs(HexExtension extension) =>
    [
        ("start", Natives.Function("start", args => Values.Bool(StartProgram(extension, PathFor(extension, args.Text(0)),
            args.Has(1) && args[1] is ListValue list ? [.. list.Items.Select(item => item is StringValue text ? text.Value : item.ToString() ?? string.Empty)] : [],
            args.Has(2) ? PathFor(extension, args.Text(2)) : null))))
    ];

    /// <summary>Hex's version, the extension's name (put right once its file has said it, see Start) and its folder.</summary>
    private static (string, IRuntimeValue)[] About(HexExtension extension) =>
    [
        ("version", new StringValue(HexAbout.Version)),
        ("name", new StringValue(extension.Name)),
        ("folder", new StringValue(extension.Folder))
    ];

    /* Reading what an extension handed over */

    internal static string? Text(ObjectValue of, string key) =>
        of.Properties.TryGetValue(key, out IRuntimeValue? value) && value is StringValue text ? text.Value : null;

    private static bool? Flag(ObjectValue of, string key) =>
        of.Properties.TryGetValue(key, out IRuntimeValue? value) && value is BooleanValue flag ? flag.Value : null;

    private static float? Number(ObjectValue of, string key) =>
        of.Properties.TryGetValue(key, out IRuntimeValue? value) && value is NumberValue number ? number.Value : null;

    internal static IRuntimeValue? FunctionOf(ObjectValue of, string key) =>
        of.Properties.TryGetValue(key, out IRuntimeValue? value) && Values.IsFunction(value) ? value : null;

    /// <summary>A path an extension gave, from its folder unless it is rooted.</summary>
    private static string PathFor(HexExtension extension, string path) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(extension.Folder, path));

    /* What the members do */

    /// <summary>A file run for an extension that can't go wrong, <c>{ values, error }</c> with one of the two null.</summary>
    private IRuntimeValue TryLoadForExtension(string path)
    {
        try
        {
            return Natives.Object(("values", LoadForExtension(path)), ("error", Values.Null));
        }
        catch (Exception e)
        {
            return Natives.Object(("values", Values.Null), ("error", new StringValue(e.Message)));
        }
    }

    private string ReadForExtension(string path)
    {
        if (context.Documents.Find(path) is { IsText: true } open)
            return open.Editor.Text;

        return File.Exists(path) ? File.ReadAllText(path) : throw new HidlRuntimeException($"There is no '{path}' to read.");
    }

    /// <summary>
    /// Writes a file for an extension. One that is open in Hex is changed in its editor, one step to undo, and left for
    /// its save, the rest are written to the disk.
    /// </summary>
    private void WriteForExtension(string path, string text)
    {
        if (context.Documents.Find(path) is { IsText: true } open)
        {
            if (open.Editor.Text != text)
                open.Editor.Document.Replace(text);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        Horizon.UI.UILayout.Forget(path);
    }

    /// <summary>The nearest folder at or over a path that has something of a name in it, null for none.</summary>
    private static string? FindUp(string from, string name)
    {
        for (string? at = Directory.Exists(from) ? from : Path.GetDirectoryName(from); at is not null; at = Path.GetDirectoryName(at))
        {
            string inside = Path.Combine(at, name);
            if (File.Exists(inside) || Directory.Exists(inside))
                return at;
        }

        return null;
    }

    /// <summary>Runs a file of HIDL for an extension and hands back what it declared, includes next to it and the open files first.</summary>
    private IRuntimeValue LoadForExtension(string path)
    {
        var runtime = new Horizon.HIDL.HIDLRuntime
        {
            Output = null,
            BaseDirectory = Path.GetDirectoryName(path),
            IncludeReader = (file, folder) =>
            {
                string full = Path.GetFullPath(Path.IsPathRooted(file) || folder is null ? file : Path.Combine(folder, file));
                return ReadForExtension(full);
            }
        };

        using var cancel = new CancellationTokenSource(1000);
        runtime.Interpreter.Cancellation = cancel.Token;
        runtime.SourceFile = path;
        runtime.Run(ReadForExtension(path));
        return new ObjectValue(new Dictionary<string, IRuntimeValue>(runtime.UserScope.Variables));
    }

    /// <summary>Hands what was picked to the extension that asked, which is switched off if that goes wrong.</summary>
    private void Picked(HexExtension extension, IRuntimeValue then, string path)
    {
        if (extension.Failed is null && Values.IsFunction(then) && !extension.Invoke(then, new StringValue(Path.GetFullPath(path))))
            Failed(extension);
    }

    /// <summary>Starts a program for an extension, false (and said why in the output) if it wouldn't.</summary>
    // How many programs all started are still running, and the name of the last one
    internal int programsRunning;
    internal string runningName = string.Empty;

    internal bool StartProgram(HexExtension extension, string program, string[] arguments, string? folder)
    {
        if (!File.Exists(program))
        {
            context.Notices.Note(context.Strings.Said("extensions_api.no_program", ("name", extension.Name), ("program", program)), error: true);
            return false;
        }

        var start = new System.Diagnostics.ProcessStartInfo(program)
        {
            WorkingDirectory = folder ?? Path.GetDirectoryName(program)!,
            UseShellExecute = false
        };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);

        try
        {
            // Kept count of while it runs, the status bar says a game started from here reloads what's saved
            var process = System.Diagnostics.Process.Start(start);
            if (process is not null)
            {
                Interlocked.Increment(ref programsRunning);
                runningName = Path.GetFileNameWithoutExtension(program);
                process.EnableRaisingEvents = true;
                process.Exited += (_, _) =>
                {
                    Interlocked.Decrement(ref programsRunning);
                    process.Dispose();
                    context.Wake();
                };
            }
            return true;
        }
        catch (Exception e)
        {
            context.Notices.Note(context.Strings.Said("extensions_api.program_failed", ("name", extension.Name), ("program", Path.GetFileName(program)), ("error", e.Message)), error: true);
            return false;
        }
    }
}
