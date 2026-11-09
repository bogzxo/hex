using Horizon.UI.Components;
using Horizon.UI.Scripting;

namespace Hex.Dawn;

internal sealed partial class HexLanguages
{
    private int typeHierarchyRequest;

    private void ShowTypeHierarchy()
    {
        HexDocument doc = context.Documents.Current;
        if (!Serves(doc, "typeHierarchyProvider", out var server, out var path)) return;
        FlushEditsToServer(server);
        var caret = doc.Editor.Document.Caret;
        int version = doc.Editor.Document.Version, workspace = editWorkspaceGeneration, request = ++typeHierarchyRequest;
        When(ReadTypes(server.TypeItems(path, caret.Line, caret.Column)), result =>
        {
            if (request != typeHierarchyRequest || workspace != editWorkspaceGeneration || context.Documents.Current != doc
                || doc.Editor.Document.Version != version || doc.Editor.Document.Caret != caret) return;
            if (result.Items is not { } items)
            {
                context.Notices.Say(Said("code_server.type_hierarchy_failed"), error: true);
                return;
            }
            if (items.Count == 0)
            {
                context.Notices.Say(Said("code_server.no_type_hierarchy"));
                return;
            }
            var roots = new List<UITreeItem>();
            foreach (var item in items)
            {
                foreach (bool bases in new[] { true, false })
                {
                    var row = new UITreeItem(Said(bases ? "code_server.base_types" : "code_server.derived_types", ("name", item.Name)), "symbol-class")
                    { Expandable = true, Expanded = true };
                    roots.Add(row);
                    FillTypes(row, server, item, bases, workspace);
                }
            }
            context.Window.ShowResultsTree(Said("code_server.type_hierarchy"), roots);
        });
    }

    private void FillTypes(UITreeItem row, LanguageServer server, LanguageServerTypeItem item, bool bases, int workspace)
    {
        if (row.Children is not null || workspace != editWorkspaceGeneration) return;
        row.Children = [Note(Said("code_server.asking"))];
        When(ReadTypes(server.RelatedTypes(item, bases)), result =>
        {
            if (workspace != editWorkspaceGeneration) return;
            row.Children = result.Items is null ? [Note(Said("code_server.type_hierarchy_failed"))]
                : result.Items.Count == 0 ? [Note(Said("code_server.no_related_types"))]
                : [.. result.Items.Select(other => new UITreeItem(other.Name, other.Kind == 11 ? "symbol-interface" : "symbol-class")
                {
                    Note = other.Detail,
                    Detail = $"{Path.GetFileName(other.Declared.Path)}:{other.Declared.Line + 1}",
                    Tooltip = other.Declared.Path,
                    Expandable = true,
                    Tag = new SideTag(Pressed: () => { if (workspace == editWorkspaceGeneration) GoTo(other.Declared); },
                        Expanding: opened => FillTypes(opened, server, other, bases, workspace))
                })];
            context.Window.RefreshResultsTree();
        });
    }

    private sealed record TypeAnswer(IReadOnlyList<LanguageServerTypeItem>? Items);
    private static async Task<TypeAnswer> ReadTypes(Task<IReadOnlyList<LanguageServerTypeItem>> request)
    {
        try { return new(await request.ConfigureAwait(false)); }
        catch (Exception) { return new(null); }
    }
}
