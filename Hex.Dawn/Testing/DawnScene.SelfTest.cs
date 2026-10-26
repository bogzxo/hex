using System.Numerics;

using Horizon.HIDL.Runtime;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Skinning;

namespace Hex.Dawn;

/// <summary>
/// The editor testing itself. A scripted pointer adds, selects, edits, drags, saves and opens the way somebody
/// with a mouse would, and every step of the way what the editor ended up with is checked and printed.
/// Started with <c>--selftest</c>, see <see cref="Program"/>.
/// </summary>
internal sealed partial class DawnScene
{
    private HexPointerScript? selfTest;
    private bool selfTestReported;

    private void UpdateSelfTest(float dt)
    {
        if (selfTest is null)
            return;

        selfTest.Update(dt);

        if (selfTest.IsFinished && !selfTestReported)
        {
            selfTestReported = true;

            if (options.Exit)
                Quit(selfTest.Failed);
        }
    }

    /// <summary>The item of a menu that opens the menu (inside of it) an item is in, null when it is right there or nowhere.</summary>
    private static string? SubmenuOf(Menu menu, string label)
    {
        // Right there in the menu it is that one, before any of the same name in a menu inside of it
        if (menu.Items.Any(item => item.Label == label && item.Submenu is null))
            return null;

        foreach (MenuItem item in menu.Items)
        {
            if (item.Submenu is { } inside && (inside.Items.Any(each => each.Label == label) || SubmenuOf(inside, label) is not null))
                return item.Label;
        }

        return null;
    }

    /// <summary>Everything inside of a component, all the way down.</summary>
    private static IEnumerable<UIComponent> Descendants(UIComponent of)
    {
        foreach (UIComponent child in of.Children)
        {
            yield return child;
            foreach (UIComponent inside in Descendants(child))
                yield return inside;
        }
    }

    /* What the sections of the test share. The folder it works in, the layout it saves, and what it makes along the way
       to ask about later. They were locals of one method two thousand lines long, the sections are methods of their own
       now, in the files next to this one by what they are about, and they run in the order StartSelfTest calls them */

    // The parts of the test this run is running, see SelfTestSuites
    private HashSet<string> suitesAsked = [];

    private string testFolder = string.Empty, savedFile = string.Empty, dataFile = string.Empty, brokenFile = string.Empty, game = string.Empty;
    private UIComponent? stack, button, label, panel, box;
    private Vector2 grab, last;

    private void StartSelfTest()
    {
        var test = selfTest = new HexPointerScript { Pace = options.Watch ? 8 : 1 };
        compositor.PointerSource = () => test.Pointer;

        // The sides go at once, a check straight after a toggle isn't waiting for a slide. Watched they slide, and
        // TestSlidingSides puts it back for its own checks
        if (!options.Watch)
        {
            left.SlideTime = right.SlideTime = bottom.SlideTime = 0.0f;
        }

        testFolder = Path.GetFullPath(options.Workspace);
        savedFile = Path.Combine(testFolder, "selftest_menu.hor");
        dataFile = Path.Combine(testFolder, "selftest_data.hor");
        brokenFile = Path.Combine(testFolder, "selftest_broken.hor");

        if (!AskedSuites(out HashSet<string> asked))
        {
            test.Check("self-test: the parts asked for are parts of it", () => false);
            return;
        }

        // The first layout has to have happened before there is anything to point at, and a window that starts
        // out maximised takes a moment to get there
        test.Settle(0.5f);
        test.WaitFor(() => Near(chrome.Root.Bounds.Width * compositor.UIScale, Engine.WindowManager.ViewportSize.X, 1), 3.0f);
        test.Settle(0.3f);

        suitesAsked = asked;
        foreach (var (name, _, run) in SelfTestSuites)
        {
            if (!asked.Contains(name))
                continue;

            // How long each part takes is said after it, the slow ones are the ones to look at
            var took = new System.Diagnostics.Stopwatch();
            test.Run(() => { Console.WriteLine($"[Hex self-test] {name}"); took.Restart(); });
            MakeTestFiles();
            run(test);
            test.Run(() => Console.WriteLine($"[Hex self-test] {name} took {took.Elapsed.TotalSeconds:0.0} s"));
        }
    }

    /// <summary>
    /// The parts of the self-test, in the order they run, any of them on its own with <c>--selftest name</c>. The
    /// designer's is the slow one. A part that wants a file an earlier part makes gets a stand-in for it, see MakeTestFiles.
    /// </summary>
    private (string Name, string About, Action<HexPointerScript> Run)[] SelfTestSuites =>
    [
        ("layout", "the designer, adding, selecting, editing, undo, the canvas, saving and opening, tabs", test =>
        {
            TestEditorItself(test);
            TestAdding(test);
            TestSelecting(test);
            TestEditingProperties(test);
            TestEntrances(test);
            TestUndoing(test);
            TestCanvas(test);
            TestReordering(test);
            TestColours(test);
            TestSectionsAndKeys(test);
            TestTemplates(test);
            TestSavingAndOpening(test);
            TestNotes(test);
            TestOwnLayout(test);
            TestScrolling(test);
            TestEditorSize(test);
            TestLayoutDebugger(test);
            TestMultipleSelection(test);
            TestDesignSize(test);
            TestTabs(test);
            TestClosingWithChanges(test);
            TestDraggingEdges(test);
            TestSlidingSides(test);
            TestLayoutTexts(test);
        }),
        ("settings", "the settings window, its search and sections, Color Theme, and the settings file kept in step with it both ways", test =>
        {
            OnLayoutForSettings(test);
            TestSettings(test);
            TestSettingsLooks(test);
            TestSettingsFile(test);
        }),
        ("code", "files of code, values, the console, docs, the table, a layout as code, completion, VS Code's every day keys", test =>
        {
            TestCode(test);
            TestFindBar(test);
            TestPlaces(test);
            TestEditingRound(test);
            TestValuesAndConsole(test);
            TestDocs(test);
            TestArt(test);
            TestCodeFont(test);
            TestZoomKeepsPlace(test);
            TestMinimap(test);
            TestTodos(test);
            TestPeek(test);
            TestInlays(test);
            TestLanguages(test);
            TestTable(test);
            TestLayoutAsCode(test);
            TestCompletion(test);
            TestParity(test);
        }),
        ("workspace", "folders, the explorer, closing tabs, the help windows, the conveniences, files changed on the disk", test =>
        {
            // The explorer is the side bar of a file of code, which the code's part leaves on screen
            OnCode(test, dataFile);
            TestWorkspace(test);
            TestClosingTabs(test);
            TestHelpWindows(test);
            TestConveniences(test);
            TestKeys(test);
            TestDiskChanges(test);
            TestBackups(test);
        }),
        ("extensions", "editing files by their values, extensions, the move editor (on Fighter2D's own moves too), their settings tabs", test =>
        {
            TestExtensions(test);
            TestMoveEditor(test);
            TestMoveEditorLayout(test);
            TestExtensionTabs(test);
            TestExtensionPrompts(test);
            TestTheGamesMoves(test);
        }),
        ("terminal", "the terminal page, Dusk's terminal on the shared host, the console as a profile of it", test =>
        {
            OnCode(test, dataFile);
            TestTerminal(test);
            TestDuskFiles(test);
        }),
        ("minimap", "the minimap on its own, clicked and dragged, on whole pixels at 100% and 111% as it scrolls, no slider on a short file", TestMinimap),
        ("languages", "the languages the code is coloured in, VS Code's grammars and HIDL's own, on their own in a few seconds", test =>
        {
            TestLanguages(test);
        }),
        ("work", "the work going in the background, its cards and their bars, one at a time a key, stopping it, a failure, an install pressed twice", TestWork),
        ("csharp", "C# through csharp-ls, what's wrong underlined and completion after a dot, when csharp-ls is on this machine", TestCSharp),
        ("d", "D through serve-d, coloured, DCD asked for and completion after a dot and of a word, when serve-d is on this machine", TestD),
        ("dialogs", "Hex's own dialog for files and folders, opening, saving over, picking and making a folder", TestDialogs),
        ("anyfile", "Dawn as an editor of any file, the outline of other languages, icons by kind, the values side folding away, pictures", test =>
        {
            TestPictures(test);
            TestAnyFile(test);
        }),
        ("emmet", "Emmet, an abbreviation in an .html file written out on Tab, undone in one step, and a word that isn't one left to indent", TestEmmet),
        ("tools", "the catalog's editing extensions on the real editor, Text tools sorting the selected lines, a snippet written out and gone through on Tab", TestTools),
        ("catalog","the catalog of extensions, a sync of a folder, Install, a newer version offered as Update and read again, a sync that fails", TestCatalog),
        ("tasks", "the tasks of a tasks.hor, run in a terminal tab of their own, their errors in the Problems panel", TestTasks),
        ("effects", "the effects, the caret gliding, a burst on a save and sparks off the caret as it types", TestEffects),
        ("debug", "C# debugged, a breakpoint, F5 to it, the stack and the variables, a step and on to the end", TestDebug),
        ("git", "git in a repository made for it, what changed coloured in the explorer and the tabs, and taken off again by a commit", TestGit),
        ("conflicts", "a real merge conflict settled by a click on its lens and staged from the card, the Accept commands greyed out without one, the words a diff changed and a diff that says nothing is wrong", TestConflicts),
        ("recent", "the commands and files picked last over the rest, a closed file brought back, a save tidying, saving by itself, the glass sliders", TestRecentAndTidy),
        ("typing", "typing fast into a big C# file and a big .hor, every tick timed, no stutter allowed", TestTypingSpeed),
        ("launch", "how Hex starts, the test's folders its own, a step that throws a failure and not a crash, a folder's start bringing back only its tabs", TestLaunch),
        ("garbage", "what sweeping the pointer over the designer and the code allocates", test =>
        {
            // The first hover over a word makes what's kept for every hover after (the docs of the library), the first
            // look at a file's code its values, none of which is garbage of a tick. After the code's part they're made
            // already, on its own every sweep goes over its points once before it's measured
            TestGarbage(test);
        })
    ];

    /// <summary>A file on screen as its code, unless what's on screen is code already.</summary>
    private void OnCode(HexPointerScript test, string path)
    {
        test.Run(() =>
        {
            if (document.IsText)
                return;

            Open(path);
            SwitchView(HexView.Code);
        });
        test.Wait();
    }

    /// <summary>
    /// The parts that were asked for, all of them when none were. One that doesn't exist ends the test with the list of
    /// the ones that do, rather than a pass of nothing.
    /// </summary>
    private bool AskedSuites(out HashSet<string> asked)
    {
        asked = options.Suites is { Length: > 0 } named ? new(named, StringComparer.OrdinalIgnoreCase) : [.. SelfTestSuites.Select(suite => suite.Name)];
        string[] unknown = [.. asked.Where(name => !SelfTestSuites.Any(suite => suite.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))];
        if (unknown.Length == 0)
            return true;

        Console.WriteLine($"[Hex self-test] there is no {string.Join(", ", unknown)}, the parts are");
        foreach (var (name, about, _) in SelfTestSuites)
            Console.WriteLine($"[Hex self-test]   {name,-11} {about}");
        return false;
    }

    // What a part finds in place of the layout the designer's part saves and the data the code's part writes, when it is
    // run without them. Written only where the file isn't, so a whole run uses what the parts before it left
    // The layout is written the way Hex writes it, one that isn't is changed as it opens and that isn't what's tested
    private const string STAND_IN_LAYOUT = "// selftest_menu, laid out with Horizon.Hex.\n"
        + "// Load it with UILayout.Load, every component is there under the name it has here.\n#define layout\n\n\n"
        + "let stack1 = compositor.stack({\n    padding: vec(16, 16, 16, 16),\n    color: vec(0.1, 0.12, 0.17, 0.9)\n});\n\n"
        + "let start_button = compositor.button({\n    parent: stack1,\n    size: vec(40, 0),\n    label: \"Fight\"\n});\n\n"
        + "let panel1 = compositor.panel({\n    pos: vec(225.88235, -112.94118),\n    size: vec(240, 160),\n    color: vec(0.1, 0.12, 0.17, 0.9)\n});\n";
    private const string TEST_DATA = "let speed = 3;\r\nlet names = {\r\n    first: \"one\"\r\n};\r\n";

    private void MakeTestFiles()
    {
        selfTest!.Run(() =>
        {
            if (!System.IO.File.Exists(savedFile))
                System.IO.File.WriteAllText(savedFile, STAND_IN_LAYOUT);
            if (!System.IO.File.Exists(dataFile))
                System.IO.File.WriteAllText(dataFile, TEST_DATA);
        });
    }

    /* Helpers of the sections */

    private static bool Near(float a, float b, float within = 0.5f) => MathF.Abs(a - b) <= within;

    // The middle of a component (or another point of it), wherever it has got to by the time the pointer goes there
    private Func<Vector2> At(Func<UIComponent?> find, float x = 0.5f, float y = 0.5f) => () =>
    {
        if (find() is { Module: not null } component)
        {
            UIRect bounds = component.Bounds;
            last = component.Module.ToWorld(new Vector2(bounds.Min.X + bounds.Width * x, bounds.Min.Y + bounds.Height * y));
        }

        return last;
    };

    // Scrolls the properties to the row of a property, the way somebody would before clicking it. The rows are in
    // sections now and the ones further down are out of sight at the start
    private void Reveal(string editor)
    {
        selfTest!.Run(() => inspectorScroll.ScrollIntoView(designer.editors[editor]));
        selfTest.Wait();
    }

    // Something out of one of the menus. A click on its word on the bar, the pointer rested on the item of the menu
    // inside it the item is in (when it is in one), and a click on the item
    private void Pick(string title, Func<string> item)
    {
        HexPointerScript test = selfTest!;
        test.Click(() => chrome.ToWorld(menu.TitleBounds(title).Center));
        test.Hover(() => menu.OpenMenu is { } open && SubmenuOf(open, item()) is { } inside ? chrome.ToWorld(menu.ItemBounds(inside).Center) : test.Pointer.Position);

        // Resting on it long enough for its menu to take over from one the pointer opened on its way there
        test.Settle(0.4f);
        test.Click(() => chrome.ToWorld(menu.ItemBounds(item()).Center));
    }

    // The same out of the menu a right click brought up
    private void PickFromMenu(string item)
    {
        HexPointerScript test = selfTest!;
        test.Hover(() => contextMenu?.Showing is { } open && SubmenuOf(open, item) is { } inside ? chrome.ToWorld(contextMenu.ItemBounds(inside).Center) : test.Pointer.Position);
        test.Settle(0.4f);
        test.Click(() => chrome.ToWorld(contextMenu!.ItemBounds(item).Center));
    }

    private Func<Vector2> Part(string name, float x = 0.5f, float y = 0.5f) => At(() => layout.Get<UIComponent>(name), x, y);
    private Func<Vector2> Kind(string kind) => At(() => designer.paletteButtons[Array.IndexOf(HexDesigner.Palette, kind)]);
    private Func<Vector2> Row(Func<UIComponent?> of) => At(() => of() is { } component ? designer.treeRows.GetValueOrDefault(component) : null);

    // The middle of a row of the side bar's tree, scrolled to first the way somebody would
    private Func<Vector2> TreeRow(Func<UITreeItem?> find) => () =>
    {
        if (find() is { } item)
        {
            sideTree.Reveal(item);
            if (sideTree.RowBounds(item) is { } bounds && sideTree.Module is { } module)
                last = module.ToWorld(bounds.Center);
        }

        return last;
    };

    private Func<Vector2> Explorer(Func<string> path) => TreeRow(() => explorer.explorerRows.Find(row => row.Path == path()).Row);
    private Func<Vector2> FileRow(string name) => TreeRow(() => fileRows.Find(row => Path.GetFileName(row.Path) == name).Row);

    // A row of the list a dropdown of the inspector has open, and a point of one of the areas of a colour picker
    private Func<Vector2> Option(string editor, int row) => () => chrome.ToWorld(((Dropdown)designer.editors[editor]).RowBounds(row).Center);
    private Func<Vector2> In(Func<UIRect> area, float x, float y) => () => chrome.ToWorld(area().Min + area().Size * new Vector2(x, y));

    // Whether the columns of the editor are as tall as each other and reach the edges of the window, the activity
    // bar on the left edge, the properties on the right, all of it standing on the status bar
    private bool Fits()
    {
        UIRect screen = chrome.Root.Bounds;
        float floor = screen.Min.Y + statusBar.Bounds.Height;

        bool fits = Near(left.Bounds.Height, center.Bounds.Height, 2) && Near(center.Bounds.Height, right.Bounds.Height, 2)
            && Near(body.Bounds.Min.Y, floor, 3) && Near(statusBar.Bounds.Min.Y, screen.Min.Y, 2)
            && Near(activity.Bounds.Min.X, screen.Min.X, 3)
            && (Near(right.Bounds.Max.X, screen.Max.X, 3) || Near(canvas.Bounds.Width, MIN_CENTER, 1));

        // What doesn't fit, for whoever reads why the check failed
        if (!fits)
            Console.WriteLine($"[Hex self-test]   heights {left.Bounds.Height:0} {center.Bounds.Height:0} {right.Bounds.Height:0} {activity.Bounds.Height:0}, body from {body.Bounds.Min.Y:0} (floor {floor:0}), status {statusBar.Bounds.Min.Y:0} of {screen.Min.Y:0}, right {right.Bounds.Max.X:0} of {screen.Max.X:0}, canvas {canvas.Bounds.Width:0}");

        return fits;
    }

    private string Code() => document.GenerateCode();

    private static void TypeInto(TextBox box, string text)
    {
        box.Clear();
        box.Insert(text);
    }
}
