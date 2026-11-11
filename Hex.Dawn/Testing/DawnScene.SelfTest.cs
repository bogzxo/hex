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

    /// <summary>
    /// The items that open the menus (inside of each other) an item of a menu is in, outermost first. Empty when it is
    /// right there, null when it is nowhere.
    /// </summary>
    private static List<string>? SubmenuPath(Menu menu, string label)
    {
        // Right there in the menu it is that one, before any of the same name in a menu inside of it
        if (menu.Items.Any(item => item.Label == label && item.Submenu is null))
            return [];

        foreach (MenuItem item in menu.Items)
        {
            if (item.Submenu is { } inside && SubmenuPath(inside, label) is { } deeper)
                return [item.Label, .. deeper];
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
            test.Run(TidyAfterSuite);
            test.Run(() => Console.WriteLine($"[Hex self-test] {name} took {took.Elapsed.TotalSeconds:0.0} s"));
        }
    }

    /// <summary>
    /// The parts of the self-test, in the order they run, any of them on its own with <c>--selftest name</c>. The
    /// designer's is the slow one. A part that wants a file an earlier part makes gets a stand-in for it, see MakeTestFiles.
    /// </summary>
    private (string Name, string About, Action<HexPointerScript> Run)[] SelfTestSuites =>
    [
        ("native-shortcuts", "Windows key messages through the native keyboard and application shortcut dispatcher", TestNativeShortcuts),
        ("ux", "mode focus, panel alignment, editor tab overflow and narrow layouts", TestUx),
        ("interactions", "palette dismissal, single-click results, empty queries, focus and confirmation boundaries", TestInteractions),
        ("agents-layout", "Agent setup, sidebar geometry, tool disclosure and runner sizing", TestAgentLayout),
        ("agents-markdown", "Agent replies render Markdown, tables, links and streaming updates at different widths", TestAgentMarkdown),
        ("scrollbars", "default scrollbar track clicks and thumb dragging without activating rows", TestScrollbars),
        ("file-icons", "file-type font glyphs, extension matching and unknown-file fallbacks", TestFileIcons),
        ("problems", "search, severity groups, colours and navigation in the problems panel", TestProblems),
        ("updates", "update close-prompt cancellation and failed handoff preserve the editor", TestUpdateHandoff),
        ("save-integrity", "encoding, Save As and failed-write preservation", TestDocumentSave),
        ("lossless", "every .hor of the engine's Assets and Hex's own saved unchanged byte for byte, a hand written layout edited in place", TestLossless),
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
        ("settings", "the settings window, its search and sections, Color Theme, the Visual Studio look, and the settings file kept in step with it both ways", test =>
        {
            OnLayoutForSettings(test);
            TestSettings(test);
            TestSettingsLooks(test);
            TestVisualStudioLook(test);
            TestSettingsFile(test);
            TestSettingsWheel(test);
            TestBackdropArrange(test);
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
            TestSurface(test);
            TestTodos(test);
            TestPeek(test);
            TestInlays(test);
            TestCodeLens(test);
            TestSemanticHidl(test);
            TestLanguages(test);
            TestTable(test);
            TestLayoutAsCode(test);
            TestCompletion(test);
            TestParity(test);
        }),
        ("reload-integrity", "failed layout reloads preserve the open document", TestReloadIntegrity),
        ("workspace", "folders, the explorer, copying, moving and renaming files in it, closing tabs, the help windows, the conveniences, files changed on the disk", test =>
        {
            // The explorer is the side bar of a file of code, which the code's part leaves on screen
            OnCode(test, dataFile);
            TestWorkspace(test);
            TestClosingTabs(test);
            TestHelpWindows(test);
            TestConveniences(test);
            TestKeys(test);
            TestFocus(test);
            TestDiskChanges(test);
            TestBackups(test);
            TestExplorerOps(test);
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
            TestTerminalScroll(test);
            TestDuskFiles(test);
        }),
        ("pty", "the shell under the terminal itself, started in its folder, a line in and out, a new size heard and exit ending it with its code", TestPty),
        ("lens", "reference counts over the declarations of a HIDL file and of C# through csharp-ls when it's on this machine, a press listing where each is used and a pick going there", test =>
        {
            TestCodeLens(test);
            TestCodeLensCSharp(test);
        }),
        ("minimap", "the minimap on its own, clicked and dragged, on whole pixels at 100% and 111% as it scrolls, no slider on a short file", TestMinimap),
        ("middle-scroll", "middle-click autoscroll and hold-to-scroll without moving the caret", TestMiddleScroll),
        ("surface", "how the code is drawn, the sticky lines of Allman C#, a squiggle under all of what it's on, the bar's thumb, the find bar clear of the minimap, a diff's two columns of numbers", TestSurface),
        ("server-startup", "Language server initialization failure recovery", TestServerStartup),
        ("pyright", "the globally installed Python server, diagnostics, completion and hover", TestPyright),
        ("python-stubs", "Python stub diagnostics, hover and consumer type checking", TestPythonStubs),
        ("python-rename", "Python semantic rename across unopened files and unsaved buffers", TestPythonRename),
        ("completion-imports", "Native completion imports, undo and stale edit protection", TestCompletionImports),
        ("completion-imports-boundary", "Native completion imports at the start of the replaced symbol", test => TestCompletionImports(test, firstColumn: true)),
        ("cpp-include-completion", "C++ header completion through native compilation and debugging", TestCppIncludes),
        ("completion-documentation", "Native C# completion signatures and XML documentation", TestCompletionDocumentation),
        ("completion-triggers", "Native C++ scope and Python dictionary completion from typed punctuation", test => { TestCompletionTriggers(test, "cpp"); TestCompletionTriggers(test, "python"); }),
        ("header-path-completion", "Native C++ include paths with punctuation, subdirectories and undo", TestHeaderPathCompletion),
        ("using-quick-fix", "Native C# using quick fix preserves unsaved work and breakpoints", TestUsingQuickFix),
        ("range-formatting", "Native selection formatting preserves surrounding code, typing and undo", TestRangeFormatting),
        ("on-type-formatting", "Native C#/C++ typing formats, indentation, undo and stale response safety", test => { TestOnTypeFormatting(test, "csharp"); TestOnTypeFormatting(test, "cpp"); TestOnTypeFormatting(test, "python"); TestOnTypeFormattingSettings(test); }),
        ("type-hierarchy", "Native C++ base and derived type trees and declaration navigation", TestTypeHierarchy),
        ("native-inlays", "Native C++ hints appear after server startup without typing", TestNativeInlays),
        ("native-signatures", "Native C++ active parameters follow the caret", TestNativeSignatures),
        ("signature-documentation", "Native C#/Python/C++ method and parameter documentation in signature help", test => { TestSignatureDocumentation(test, "csharp"); TestSignatureDocumentation(test, "python"); TestSignatureDocumentation(test, "cpp"); }),
        ("python-signatures", "Native Python active parameters follow the caret", test => TestNativeSignatures(test, "python")),
        ("csharp-signatures", "Native C# active parameters follow the caret", test => TestNativeSignatures(test, "csharp")),
        ("quoted-signatures", "Quoted commas preserve signature arguments in C#, Python and C++", test =>
        {
            TestNativeSignatures(test, "csharp", true);
            TestNativeSignatures(test, "python", true);
            TestNativeSignatures(test, "cpp", true);
        }),
        ("hidl-inlays", "HIDL parameter hints, caret layout and editing", TestInlays),
        ("call-hierarchy", "Native C++ callers, callees and source navigation", TestCallHierarchy),
        ("frame-values", "Pinned values follow the selected native call-stack frame", TestFrameValues),
        ("python-data-tips", "Native Python compound data tips and pinned refresh", test => TestNativeDataTips(test, "python")),
        ("python-list-tips", "Native Python list item watch expressions", test => TestNativeDataTips(test, "python", true)),
        ("cpp-data-tips", "Native C++ compound data tips and pinned refresh", test => TestNativeDataTips(test, "cpp")),
        ("csharp-data-tips", "Native C# compound data tips and pinned refresh", test => TestNativeDataTips(test, "csharp")),
        ("csharp-exceptions", "Native C# thrown-exception filters, continuation and saved selection", test => TestExceptionBreakpoints(test, false)),
        ("python-exceptions", "Native Python raised-exception filters, continuation and saved selection", test => TestExceptionBreakpoints(test, true)),
        ("python-threads", "Python thread selection, stacks, locals and stepping", test => TestDebugThreads(test, "python")),
        ("cpp-threads", "C++ thread selection, stacks, locals and stepping", test => TestDebugThreads(test, "cpp")),
        ("debug-threads", "Native thread selection, stacks, locals and stepping", TestDebugThreads),
        ("csharp-run-to", "Temporary Run to Cursor breakpoint lifecycle", test => TestRunToCursor(test, "csharp")),
        ("python-run-to", "Python Run to Cursor", test => TestRunToCursor(test, "python")),
        ("set-next-statement", "Native execution jumps, skipped statements, errors and capability gating", test => { TestSetNextStatement(test, "python"); TestSetNextStatement(test, "csharp"); TestSetNextStatement(test, "cpp"); }),
        ("debug-completion", "Native paused-frame expression completion, acceptance, undo and stale prompts", test => { TestDebugCompletion(test, "python"); TestDebugCompletion(test, "cpp"); TestDebugCompletion(test, "csharp"); }),
        ("data-breakpoints", "Native write watchpoints, value-change stops, controls and session lifetime", test => { TestDataBreakpoints(test, "cpp"); TestDataBreakpoints(test, "python"); TestDataBreakpoints(test, "csharp"); }),
        ("cpp-run-to", "C++ Run to Cursor", test => TestRunToCursor(test, "cpp")),
        ("csharp-edit-variable", "Native local variable editing and stale prompt rejection", test => TestEditVariable(test, "csharp")),
        ("python-edit-variable", "Native Python local variable editing", test => TestEditVariable(test, "python")),
        ("cpp-edit-variable", "Native C++ local variable editing", test => TestEditVariable(test, "cpp")),
        ("cpp-exceptions", "Native C++ thrown-exception filters, continuation and saved selection", test => TestExceptionBreakpoints(test, false, true)),
        ("completion-documentation-race", "Completion documentation selection races and failure recovery", TestCompletionDocumentationRace),
        ("clangd-midword", "C++ completion replaces the suffix after the caret", test => TestClangd(test, ".cpp", ".h", midWord: true)),
        ("missing-sdk", "C# SDK resolution failure reaches Problems and opens global.json", TestMissingSdk),
        ("nuget-restore", "Offline NuGet failure navigation and recovery into C# debugging", TestNuGetRestore),
        ("python-environment", "Pyright resolves packages from the project virtual environment", TestPythonEnvironment),
        ("cpp-pause", "C++ menu and toolbar pause with native LLDB", test => TestDebugPause(test, "cpp")),
        ("python-pause", "Python menu and toolbar pause with native debugpy", test => TestDebugPause(test, "python")),
        ("debug-workspace-switch", "Active C# debugging ends when another workspace opens", test => TestDebugPause(test, "csharp", switchWorkspace: true)),
        ("dotnet-project-terminal", "Owning project terminal and native SDK completion", TestDotnetProjectTerminal),
        ("variable-pages", "Native debugger collection paging", TestVariablePages),
        ("watch-object", "Native C# compound Watch context menu, expansion, editing and persistence", TestWatchObject),
        ("watch-value", "Native C#/Python Watch value assignments and unsupported C++ capability gating", test => { TestWatchValue(test, "csharp"); TestWatchValue(test, "python"); TestWatchValue(test, "cpp"); }),
        ("function-breakpoints", "Native C#/Python/C++ named function breakpoints, source coexistence and enable/remove lifecycle", test => { TestFunctionBreakpoints(test, "csharp"); TestFunctionBreakpoints(test, "python"); TestFunctionBreakpoints(test, "cpp"); }),
        ("function-breakpoint-persistence", "Isolated source/function breakpoint persistence and workspace switching", TestFunctionBreakpointPersistence),
        ("native-folding", "Native C#/C++ folding and Python symbol folds, rendering, edits and undo", test => { TestNativeFolding(test, "csharp"); TestNativeFolding(test, "python"); TestNativeFolding(test, "cpp"); }),
        ("watch-persistence", "Workspace watch persistence and isolation", TestWatchPersistence),
        ("debug-pause", "C# menu pause, call stack, continue and toolbar pause", TestDebugPause),
        ("debug-attach", "Native .NET process selection, attachment, breakpoints, stepping and safe detach", TestDebugAttach),
        ("debug-modules", "Native loaded modules, symbols, library loads, refresh/filter and session isolation", test => { TestDebugModules(test, "csharp"); TestDebugModules(test, "cpp"); TestDebugModules(test, "python"); }),
        ("debug-sources", "Native generated-source retrieval, read-only navigation, stepping and session ownership", TestDebuggerSources),
        ("disassembly", "Native disassembly, instruction stepping and frames without source", test => { TestDisassembly(test, "cpp"); TestDisassembly(test, "csharp"); TestDisassembly(test, "python"); }),
        ("debug-memory", "Native memory references, hexadecimal/ASCII views, paging and stale-context protection", test => { TestMemoryView(test, "cpp"); TestMemoryView(test, "csharp"); TestMemoryView(test, "python"); }),
        ("instruction-breakpoints", "Native instruction breakpoints from F9 and the disassembly gutter", TestInstructionBreakpoints),
        ("launch-profile-environment", "Project URL editing and C# F5 environment parity with the native SDK", test => { TestLaunchProfileEnvironment(test); TestAspNetLaunchProfile(test); }),
        ("dotnet-task-environment", "Native dotnet run environment options and launch-profile precedence under debugging", TestDotnetRunEnvironment),
        ("dotnet-target-frameworks", "Native multi-target framework build, task debugging, selection and restart", TestDotnetTargetFrameworks),
        ("dotnet-runtime-options", "Native task runtime/architecture/OS selection and self-contained debugger launch", TestDotnetRuntimeOptions),
        ("organize-imports", "Native import organization with safe edits and undo", TestOrganizeImports),
        ("diagnostic-locations", "Native diagnostic related-source navigation", TestDiagnosticLocations),
        ("diagnostic-documentation", "Native diagnostic documentation at the caret", TestDiagnosticDocumentation),
        ("header-source", "Native clangd header/source navigation", TestHeaderSource),
        ("framework-navigation", "Read-only C# framework source and native navigation within metadata", TestFrameworkNavigation),
        ("definition-results", "Native multiple definitions, mixed source destinations and guarded selection", test => { TestDefinitionResults(test); TestUnionDefinitionResults(test); }),
        ("hover-races", "Delayed hover responses cannot overwrite current help", test => languages.TestHoverRaces(test, Path.Combine(testFolder, "hover races"))),
        ("signature-races", "Delayed signature responses cannot block or overwrite newer requests", test => languages.TestSignatureRaces(test, Path.Combine(testFolder, "signature races"))),
        ("native-highlights", "Native C++, Python and C# symbol occurrence scopes and access kinds", test => { TestNativeHighlights(test, "cpp"); TestNativeHighlights(test, "python"); TestNativeHighlights(test, "csharp"); }),
        ("type-definition", "Go to a variable's type through native C++, Python and C# language servers", test => { TestTypeDefinition(test, "cpp"); TestTypeDefinition(test, "python"); TestTypeDefinition(test, "csharp"); }),
        ("selection-ranges", "Expand and shrink syntax selections through native language servers", test => { TestSelectionRanges(test, "cpp"); TestSelectionRanges(test, "python"); TestSelectionRanges(test, "csharp"); }),
        ("navigation-races", "Delayed navigation respects caret, edits, workspace and newer requests", test => languages.TestNavigationRaces(test, Path.Combine(testFolder, "navigation races"))),
        ("python-create-environment", "Create and select a native virtual environment with pip", TestCreatePythonEnvironment),
        ("python-terminal", "Python terminal resolves python and pip from the workspace environment", TestPythonTerminal),
        ("python-test-environments", "Python interpreter changes refresh native discovery and retire obsolete results", test => testExplorer.TestPythonEnvironments(test, Path.Combine(testFolder, "python test environments"), explorer.OpenFolder, languages.UsePythonInterpreter)),
        ("npminstall", "a real offline npm install through the language-server Install action", TestNpmInstall),
        ("languages", "the languages the code is coloured in, VS Code's grammars and HIDL's own, on their own in a few seconds", test =>
        {
            TestLanguages(test);
        }),
        ("search", "the search view in the side bar, live results, every match a row, the toggles, include and exclude, dismissing, Replace in Files", TestSearchView),
        ("shell", "the shell round the editor, the side bar one width whatever is in it, empty notes, the title over the welcome, long tabs, Extensions lit, the explorer's buttons", TestShellLayout),
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
        ("automations", "the automations of a tasks.hor in the palette, HIDL run off the editor's thread and stopped, a command line in a tab, and the workspace's Push all against repositories made for it", TestAutomations),
        ("run_target", "the arrow beside Run, a run task picked, kept with the workspace and run by F5, and the file on screen again", TestRunTarget),
        ("properties", "the Properties panel, a file of a C# project given Copy if newer and a Build Action, several files, a folder, a picture, a component", TestProperties),
        ("menus", "every menu twenty rows at most and no two rules in a row, every list a menu opens inside the window, a menu taller than the window scrolling", TestMenus),
        ("effects", "the effects, the caret gliding, a burst on a save and sparks off the caret as it types", TestEffects),
        ("debug", "C# debugged, a breakpoint, F5 to it, the stack and the variables, a step and on to the end", TestDebug),
        ("breakpoint-history", "Breakpoint positions and settings follow paste, deletion, undo and redo", TestBreakpointHistory),
        ("debug-cancel", "Stop cancels a real delayed build without a later launch, then debugging can restart", TestDebugCancel),
        ("python-debug", "Python gutter, F5, locals, stepping, restart and stop with real debugpy", TestPythonDebug),
        ("native-debug", "C++ compilation, breakpoint, locals, stepping and stop with LLVM", TestNativeDebug),
        ("cmake-debug", "CMake multi-file project compilation, library breakpoint, locals and stepping", TestCMakeDebug),
        ("cmake-preset-debug", "CMake preset selection preserves configuration and binary directory when debugging", test => TestCMakeDebug(test, true)),
        ("cmake-configurations", "CMake multi-configuration target selection, build and remembered debugging", test => TestCMakeDebug(test, true, true)),
        ("cmake-library-configurations", "Library-only configuration selection updates clangd and Test Explorer", test => TestCMakeConfigure(test, true)),
        ("cmake-configure", "Configure a library-only CMake project and refresh clangd without debugging", TestCMakeConfigure),
        ("cmake-switch-preset", "Switch native CMake presets and update compilation and build directories", TestCMakeSwitchPreset),
        ("clangd-services", "CMake-backed C++ definitions, references, completion and hover", TestClangd),
        ("clangd-extensions", "C++ services for cxx sources and hxx headers", test => TestClangd(test, ".cxx", ".hxx")),
        ("cmake-problems", "CMake configure error navigation and successful reconfigure recovery", TestCMakeProblems),
        ("ctest", "CTest discovery, native build/run, debug results and build diagnostics", test =>
        {
            string root = Path.Combine(testFolder, "ctest");
            testExplorer.TestCTestExplorer(test, root, explorer.OpenFolder);
            TestCTestBuildProblems(test, root);
        }),
        ("dotnet-terminal", "Native SDK and NuGet autocomplete in the terminal, including project frameworks", TestDotnetTerminal),
        ("python-tests", "Python unittest discovery, execution, results and navigation", test => testExplorer.TestPythonExplorer(test, Path.Combine(testFolder, "python-tests"), explorer.OpenFolder)),
        ("pytest", "Native pytest discovery, parameter selection, results and debugging", test => testExplorer.TestPytestExplorer(test, Path.Combine(testFolder, "pytest"), explorer.OpenFolder)),
        ("python-tests-nested", "Nested unittest package discovery, selected execution, navigation and debugging", test => testExplorer.TestPythonExplorer(test, Path.Combine(testFolder, "python-tests-nested"), explorer.OpenFolder, nested: true)),
        ("repository", "read-only solution explorer qualification against HEX_TEST_SOLUTION", TestRepository),
        ("test-explorer-toggle", "Test Explorer activity button opens, closes and reopens", TestTestExplorerToggle),
        ("language-edit-races", "Late language edits cannot overwrite typing or another workspace", test => languages.TestEditRaces(test, Path.Combine(testFolder, "edit-races"))),
        ("test-configuration", "Test Explorer runs the selected build configuration", test => testExplorer.TestConfiguration(test, Path.Combine(testFolder, "test-configuration"), ChooseConfiguration)),
        ("test-parallel-frameworks", "Overlapping native framework hosts, isolated outputs, SDK opt-out and cancellation", test => testExplorer.TestParallelFrameworks(test, Path.Combine(testFolder, "parallel-frameworks"), (root, file) => { explorer.OpenFolder(root); Open(file); }, explorer.OpenFolder)),
        ("test-target-frameworks", "Native multi-target test results, targeted reruns and sequential debug hosts", test =>
        {
            void OpenTests(string root, string file) { explorer.OpenFolder(root); Open(file); }
            testExplorer.TestTargetFrameworks(test, Path.Combine(testFolder, "test-target-frameworks"), OpenTests);
            testExplorer.TestSingleTargetFrameworkRetry(test, Path.Combine(testFolder, "single-target-retry"), OpenTests);
        }),
        ("test-workspace", "Test Explorer replaces discovery and ignores old results when the workspace changes", test => testExplorer.TestWorkspaceSwitch(test, Path.Combine(testFolder, "test-workspaces"), explorer.OpenFolder)),
        ("git", "git in a repository made for it, what changed coloured in the explorer and the tabs, and taken off again by a commit", TestGit),
        ("conflicts", "a real merge conflict settled by a click on its lens and staged from the card, the Accept commands greyed out without one, the words a diff changed and a diff that says nothing is wrong", TestConflicts),
        ("recent", "the commands and files picked last over the rest, a closed file brought back, a save tidying, saving by itself, the glass sliders", TestRecentAndTidy),
        ("scratchpads", "a scratchpad in a language, closed without a question, the empty ones gone, the list by first lines and one moved into the workspace", TestScratchpads),
        ("test-debug-cancel", "test debug Restart/Stop owns cancellation, attachment and results per attempt", TestTestDebugCancel),
        ("rename-safety", "real C# semantic rename rejects framework members without text fallback and preserves valid references", TestRenameSafety),
        ("modal-shortcuts", "dialogs own keyboard input without leaking application shortcuts or chords", TestModalShortcuts),
        ("save-evaluation", "atomic editor saves preserve evaluated project membership; real additions and removals refresh it", TestSaveEvaluation),
        ("project-references", "reference picker and edits preserve MSBuild path expressions and verify outcomes", TestProjectReferences),
        ("solution-switch", "solution switching rebinds open documents without losing unsaved text", TestSolutionSwitch),
        ("semantic", "semantic highlighting, HIDL's names by what they are, C# through csharp-ls in ReSharper's colours when it's on this machine", test =>
        {
            TestSemanticHidl(test);
            TestSemanticCSharp(test);
            TestCodeLensCSharp(test);
            TestSemanticD(test);
        }),
        ("typing", "typing fast into a big C# file and a big .hor, every tick timed, no stutter allowed", TestTypingSpeed),
        ("uicost", "what the UI costs sitting still, typing, the wheel, a window dragged and a notebook scrolled, said and not checked", TestUICost),
        ("notebook", "a Jupyter notebook as its cells, run in a real kernel when there's one, outputs, input(), interrupting and restarting, saved", TestNotebook),
        ("notebookengineering", "engineering reference calculations, inline math, SVG plots, rich output textures and save", TestNotebookEngineering),
        ("notebookfile", "an isolated copy of HEX_NOTEBOOK_TEST_FILE, optionally executed", TestNotebookFile),
        ("code-surface", "code rendering, compact find and pinned-scope visibility", TestSurface),
        ("zoom-persistence", "debounced zoom settings and lifecycle flush", TestZoomPersistence),
        ("document-retention", "twenty document open/close retention cycles", TestDocumentRetention),
        ("source-retention", "C#/Python/C++ open/edit/close collection with breakpoint and undo histories", TestSourceDocumentRetention),
        ("document-memory", "thirty-minute edit/save/close session under the normal GC policy; measurements require review", TestDocumentMemory),
        ("notebook-wrap", "backdrop wrapping during notebook scrolling", TestNotebookWrap),
        ("notebook-zoom", "centred notebook zoom and viewport resize", TestNotebookZoom),
        ("notebookhtml", "a notebook exported as HTML from the palette through the save dialog, unsaved edits in it, the notebook left unsaved, SVG kept vector", TestNotebookHtml),
        ("explorerfiles", "stable refresh, multi-file clipboard shortcuts, staged cut and asynchronous paste", TestExplorerFiles),
        ("markdown", "native Markdown preview, live changes, legacy state and shared scroll speed", TestNativeMarkdown),
        ("still", "the code staying still as a line is typed red and put right again, every line in sight and the code's box watched every tick", TestStill),
        ("launch", "how Hex starts, the test's folders its own, a step that throws a failure and not a crash, a folder's start bringing back only its tabs", TestLaunch),
        ("mcp", "the MCP server for agents, a client at its door with the token, a screenshot, the ui tree, a click, typing, a command, problems and waiting", TestMcp),
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
        // OS input tests take the foreground briefly, so they only run when explicitly named.
        asked = options.Suites is { Length: > 0 } named ? new(named, StringComparer.OrdinalIgnoreCase) : [.. SelfTestSuites.Where(suite => suite.Name is not ("pyright" or "npminstall" or "native-shortcuts" or "repository")).Select(suite => suite.Name)];
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

    // Something out of one of the menus. A click on its word on the bar, the pointer rested on each item of the menus
    // inside it the item is in (when it is in any), and a click on the item
    private void Pick(string title, Func<string> item)
    {
        HexPointerScript test = selfTest!;
        test.Click(() => chrome.ToWorld(menu.TitleBounds(title).Center));
        Through(test, () => menu.OpenMenu, label => menu.ItemBounds(label), item);
        test.Click(() => chrome.ToWorld(menu.ItemBounds(item()).Center));
    }

    // The same out of the menu a right click brought up
    private void PickFromMenu(string item)
    {
        HexPointerScript test = selfTest!;
        Through(test, () => contextMenu?.Showing, label => contextMenu!.ItemBounds(label), () => item);
        test.Click(() => chrome.ToWorld(contextMenu!.ItemBounds(item).Center));
    }

    // The pointer rested on the items that open the menus an item is in, one after the other, the first long enough for
    // its menu to take over from one the pointer opened on its way there, and each menu waited for before the next
    private void Through(HexPointerScript test, Func<Menu?> open, Func<string, UIRect> bounds, Func<string> item)
    {
        string? Step(int depth) => open() is { } shown && SubmenuPath(shown, item()) is { } path && path.Count > depth ? path[depth] : null;
        for (int depth = 0; depth < 3; depth++)
        {
            int at = depth;
            test.Hover(() => Step(at) is { } label ? chrome.ToWorld(bounds(label).Center) : test.Pointer.Position);
            if (at == 0)
                test.Settle(0.4f);
            test.WaitFor(() => Step(at) is null || !bounds(Step(at + 1) ?? item()).IsEmpty, 1.0f);
        }
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
