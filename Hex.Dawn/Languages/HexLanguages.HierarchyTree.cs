using Horizon.Core.Platform;
using Horizon.UI.Components;
using Horizon.UI.Scripting;

namespace Hex.Dawn;

// The call hierarchy as Visual Studio has it, a tree in the side bar with Calls to and Calls from the name at the caret.
// Every caller opens to its own callers (and everything called to what it calls), asked of the server as it's opened
// and not before, and the lines the calls are on come first under it, a press on any of them goes there. It was a flat
// list of the callers' lines, nothing further up the chain to be had without going there and asking again.
internal sealed partial class HexLanguages
{
    // The most places a row's detail says the lines of, the rest are a count
    private const int SITES_SAID = 4;

    /// <summary>The tree for something a server has a call hierarchy of, the calls to it asked for straight away.</summary>
    private void ShowHierarchy(LanguageServer server, LanguageServerCallItem item, string path, UITextPosition caret, string word)
    {
        var to = new UITreeItem(Said("code_server.calls_to", ("name", item.Name)), "call-incoming") { Expandable = true, Expanded = true };
        var from = new UITreeItem(Said("code_server.calls_from", ("name", item.Name)), "call-outgoing") { Expandable = true };
        from.Tag = new SideTag(Expanding: opened => FillCalls(opened, server, item, incoming: false, null));

        // csharp-ls said nobody called a method two files used, the references it does know are listed in their place
        FillCalls(to, server, item, incoming: true, () => ReferencesInstead(to, server, path, caret, word));
        context.Window.ShowResultsTree(Said("code_server.hierarchy_title", ("word", item.Name)), [to, from]);
    }

    /// <summary>
    /// The rows under one that opens, the calls to something (or from it), asked once. With none, whatever's given to do
    /// instead, or a note that says so.
    /// </summary>
    private void FillCalls(UITreeItem row, LanguageServer server, LanguageServerCallItem item, bool incoming, Action? none)
    {
        if (row.Children is not null)
            return;

        row.Children = [Note(Said("code_server.asking"))];
        When(Calls(server, item, incoming), calls =>
        {
            if (calls is null)
            {
                row.Children = [Note(Said("code_server.call_hierarchy_failed"))];
                context.Window.RefreshResultsTree();
                return;
            }
            if (calls.Count == 0 && none is not null)
            {
                none();
                return;
            }

            row.Children = calls.Count == 0 ? [Note(Said("code_server.no_calls"))] : [.. calls.Select(call => CallRow(server, call, incoming))];
            context.Window.RefreshResultsTree();
        });
    }

    // Null denotes a failed request; an empty collection is a successful answer with no calls.
    private static Task<IReadOnlyList<LanguageServerCall>?> Calls(LanguageServer server, LanguageServerCallItem item, bool incoming) =>
        ReadCallHierarchy(incoming ? server.IncomingCalls(item) : server.OutgoingCalls(item));

    private static async Task<T?> ReadCallHierarchy<T>(Task<T> request) where T : class
    {
        try { return await request.ConfigureAwait(false); }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// A caller (or something called), its name, what it's in and the lines of the calls. A press goes to the first of
    /// those, and it opens to every call's line and then its own calls, the same way again.
    /// </summary>
    private UITreeItem CallRow(LanguageServer server, LanguageServerCall call, bool incoming)
    {
        LanguageServerCallItem other = call.Other;
        IReadOnlyList<LanguageServerPlace> sites = call.Sites.Count > 0 ? call.Sites : [other.Declared];
        LanguageServerPlace first = sites[0];
        return new UITreeItem(other.Name, KindIcon(other.Kind))
        {
            Note = other.Detail,
            Detail = SitesSaid(sites),
            Tooltip = $"{first.Path}:{first.Line + 1}",
            Expandable = true,
            Tag = new SideTag(Pressed: () => GoTo(first), Expanding: opened =>
            {
                if (opened.Children is not null)
                    return;

                var lines = new Dictionary<string, string[]>(PlatformPaths.Comparer);
                var children = sites.Select(site => SiteRow(site, lines)).ToList();
                UITreeItem asking = Note(Said("code_server.asking"));
                children.Add(asking);
                opened.Children = children;
                When(Calls(server, other, incoming), further =>
                {
                    children.Remove(asking);
                    if (further is null) children.Add(Note(Said("code_server.call_hierarchy_failed")));
                    else children.AddRange(further.Select(next => CallRow(server, next, incoming)));
                    context.Window.RefreshResultsTree();
                });
            })
        };
    }

    /// <summary>A line a call is on, what's written there with the call lit, a press goes to it.</summary>
    private UITreeItem SiteRow(LanguageServerPlace site, Dictionary<string, string[]> lines)
    {
        string said = LineOf(site, lines).TrimEnd('\r');
        int lead = said.Length - said.TrimStart().Length;
        int length = site.EndLine == site.Line ? Math.Max(0, site.EndCharacter - site.Character) : 0;
        string text = said.Trim();
        int at = site.Character - lead;
        return new UITreeItem(text, "debug-stackframe-dot")
        {
            Mark = length > 0 && at >= 0 && at + length <= text.Length ? (at, length) : default,
            Detail = $"{site.Line + 1}",
            Tooltip = $"{site.Path}:{site.Line + 1}",
            Tag = new SideTag(Pressed: () => GoTo(site))
        };
    }

    /// <summary>Under Calls to, with no callers the server knows of, the places the name is used, which is the next best thing.</summary>
    private void ReferencesInstead(UITreeItem row, LanguageServer server, string path, UITextPosition caret, string word)
    {
        if (!server.Offers("referencesProvider"))
        {
            row.Children = [Note(Said("code_server.no_callers", ("word", word), ("server", ShownName(server))))];
            context.Window.RefreshResultsTree();
            return;
        }

        When(server.References(path, caret.Line, caret.Column), all =>
        {
            var lines = new Dictionary<string, string[]>(PlatformPaths.Comparer);
            List<LanguageServerPlace> places = ProjectPlaces(server, all, word);
            var children = new List<UITreeItem> { Note(Said("code_server.callers_from_references", ("server", ShownName(server)))) };
            children.AddRange(places.OrderBy(place => place.Path, PlatformPaths.Comparer).ThenBy(place => place.Line).Select(place =>
            {
                UITreeItem site = SiteRow(place, lines);
                site.Note = Path.GetFileName(place.Path);
                return site;
            }));
            row.Children = children;
            context.Window.RefreshResultsTree();
        });
    }

    // The file and the lines of some calls, "Program.cs:12, 40", the lines past the first few as a count
    private static string SitesSaid(IReadOnlyList<LanguageServerPlace> sites)
    {
        var numbers = sites.Select(site => site.Line + 1).Distinct().ToList();
        string said = string.Join(", ", numbers.Take(SITES_SAID));
        if (numbers.Count > SITES_SAID)
            said += $" +{numbers.Count - SITES_SAID}";
        return $"{Path.GetFileName(sites[0].Path)}:{said}";
    }

    // The icon of a symbol by the kind a server says it is
    private static string KindIcon(int kind) => kind switch
    {
        5 => "symbol-class",
        7 => "symbol-property",
        9 => "symbol-constructor",
        _ => "symbol-method"
    };

    private void GoTo(LanguageServerPlace place) => context.Documents.OpenAt(place.Path, place.Line, place.Character);

    private UITreeItem Note(string text) => HexNotes.Note(text, context.Panels.Dim);

    private string Said(string key, params (string Name, string Value)[] values) => context.Strings.Said(key, values);
}
