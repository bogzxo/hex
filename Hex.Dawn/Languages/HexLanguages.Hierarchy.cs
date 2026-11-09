using Horizon.UI.Scripting;

namespace Hex.Dawn;

// Ctrl+F12 to what implements a name and Ctrl+K Ctrl+T for what calls it, Visual Studio's keys. Somebody on an
// interface's method pressed F12 and landed on the interface, the code that runs was nowhere to be had
internal sealed partial class HexLanguages
{
    /// <summary>Ctrl+F12, where the name at the caret is implemented. One place goes there, more are listed on the left.</summary>
    private void GoToImplementation()
    {
        HexDocument of = context.Documents.Current;
        string word = WordAtCaret(of);
        if (word.Length == 0)
        {
            context.Notices.Say(context.Strings.Said("code_hints.caret_not_on_name"), error: true);
            return;
        }

        if (!Serves(of, "implementationProvider", out LanguageServer server, out string path))
        {
            context.Notices.Say(context.Strings.Said("code_server.no_implementation", ("word", word), ("server", "the language server")), error: true);
            return;
        }

        UITextPosition caret = of.Editor.Document.Caret;
        var request = CaptureNavigation(of, server);
        WhenNavigating(request, server.Implementation(path, caret.Line, caret.Column), said =>
        {
            // Nothing, or just the virtual or interface member itself, has its overrides looked for among its references
            if (OnlyItself(said, word, new Dictionary<string, string[]>(PlatformPaths.Comparer)))
                FindOverrides(server, path, caret, word, found =>
                {
                    if (NavigationCurrent(request)) ShowImplementations(server, word, found.Count > 0 ? found : said);
                });
            else
                ShowImplementations(server, word, said);
        });
    }

    // Where a name is implemented, gone to when it's one place and listed on the left when it's more
    private void ShowImplementations(LanguageServer server, string word, IReadOnlyList<LanguageServerPlace> places)
    {
        if (places.Count == 0)
        {
            context.Notices.Say(context.Strings.Said("code_server.no_implementation", ("word", word), ("server", ShownName(server))));
            return;
        }

        if (places.Count > 1)
        {
            ShowPlaces(context.Strings.Said("code_server.implementations_title", ("word", word), ("count", places.Count.ToString())), places);
            return;
        }

        LanguageServerPlace place = places[0];
        context.Documents.OpenAt(place.Path, place.Line, NameColumn(place, word));
        context.Notices.Say(context.Strings.Said("code_server.implemented_at", ("word", word), ("file", Path.GetFileName(place.Path)), ("line", (place.Line + 1).ToString())));
    }

    /// <summary>
    /// Ctrl+K Ctrl+T, the call hierarchy of what's at the caret as a tree on the left, the calls to it and the calls
    /// from it (HexLanguages.HierarchyTree). Falls back to Find All References when the server has no call hierarchy,
    /// or nothing there it has one of, which is the next best thing.
    /// </summary>
    private void ShowCallers()
    {
        HexDocument of = context.Documents.Current;
        string word = WordAtCaret(of);
        if (word.Length == 0)
        {
            context.Notices.Say(context.Strings.Said("code_hints.caret_not_on_name"), error: true);
            return;
        }

        if (!Serves(of, "callHierarchyProvider", out LanguageServer server, out string path))
        {
            FindReferences(of);
            return;
        }

        UITextPosition caret = of.Editor.Document.Caret;
        int version = of.Editor.Document.Version, workspace = editWorkspaceGeneration;
        FlushEditsToServer(server);
        When(ReadCallHierarchy(server.CallItems(path, caret.Line, caret.Column)), items =>
        {
            if (workspace != editWorkspaceGeneration || context.Documents.Current != of
                || of.Editor.Document.Version != version || of.Editor.Document.Caret != caret) return;
            if (items is null)
            {
                context.Notices.Say(Said("code_server.call_hierarchy_failed"), error: true);
                return;
            }
            if (items.Count > 0)
            {
                ShowHierarchy(server, items[0], path, caret, word);
                return;
            }

            if (!FindReferences(of))
                context.Notices.Say(context.Strings.Said("code_server.no_callers", ("word", word), ("server", ShownName(server))));
        });
    }

    /// <summary>The two commands, there in a file whose server does them.</summary>
    private void AddHierarchyCommands()
    {
        UICommands commands = context.Commands;
        commands.Add("Go/symbols", context.Strings.Said("code_server.switch_header_source"), SwitchHeaderSource, icon: "go-to-file")
            .IsVisible = () => CanSwitchHeaderSource(context.Documents.Current);
        commands.Add("Edit/selection", context.Strings.Said("code_server.expand_selection"), ExpandSelection, "Alt+Shift+Right", "expand-all", UIKeyUse.BoundWhileTyping)
            .IsVisible = () => CanServe(context.Documents.Current, "selectionRangeProvider");
        commands.Add("Edit/selection", context.Strings.Said("code_server.shrink_selection"), ShrinkSelection, "Alt+Shift+Left", "collapse-all", UIKeyUse.BoundWhileTyping)
            .IsEnabled = () => SelectionHistoryCurrent() && selectionHistory!.Index > 0;
        commands.Add("Go/symbols", context.Strings.Said("code_server.go_to_type_definition"), GoToTypeDefinition, icon: "symbol-class")
            .IsVisible = () => CanServe(context.Documents.Current, "typeDefinitionProvider");
        commands.Add("Go/symbols", context.Strings.Said("code_server.type_hierarchy"), ShowTypeHierarchy, icon: "symbol-class")
            .IsVisible = () => CanServe(context.Documents.Current, "typeHierarchyProvider");
        commands.Add("Go/symbols", context.Strings.Said("code_server.go_to_implementation"), GoToImplementation, "Ctrl+F12", "symbol-interface", UIKeyUse.BoundWhileTyping)
            .IsVisible = () => CanServe(context.Documents.Current, "implementationProvider");
        commands.Add("Go/symbols", context.Strings.Said("code_server.call_hierarchy"), ShowCallers, "Ctrl+K Ctrl+T", "call-incoming", UIKeyUse.BoundWhileTyping)
            .IsVisible = () => CanServe(context.Documents.Current, "callHierarchyProvider") || CanServe(context.Documents.Current, "referencesProvider");
    }
}
