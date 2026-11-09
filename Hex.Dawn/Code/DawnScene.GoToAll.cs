using System.Globalization;

using Hex.Core;

using Horizon.Content;
using Horizon.UI.Components;

namespace Hex.Dawn;

// Go to All, Visual Studio's Ctrl+T (and Ctrl+, with its keys), every type and member of the workspace by a few letters
// of its name, with what kind of thing it is, what it's in and the file it's in, and # in quick open the same way VS
// Code has it. The agent harness's index knows every declaration already (HexCodeIndex), so it's asked rather than
// anything being read again. It's searched off the editor's thread for every key typed (UIChoiceSearch), the editor
// only ever holds the rows that are showing.
internal sealed partial class DawnScene
{
    // How many declarations are listed at most, the chooser shows a few hundred and nobody scrolls further than that
    private const int GO_TO_ALL_MOST = 200;

    // How many of the game's texts # in quick open lists after the declarations at most
    private const int TEXTS_MOST = 60;

    // The kinds Go to All lists, each said in words under go_to_all (kind_method)
    private static readonly string[] KindsSaid = ["class", "interface", "struct", "enum", "method", "property", "field", "event", "variable"];

    /// <summary>Ctrl+T, a type or a member of the workspace by a few letters of its name.</summary>
    private void GoToAll()
    {
        UIChoiceSearch search = SymbolSearch(withTexts: false);
        ChooseFrom(Said("go_to_all.title"), Said("go_to_all.message", ("where", DescribeRoots())), new List<UIChoice>(),
            searched: prefix => prefix == '\0' ? search : null);
    }

    /// <summary>
    /// The search of the workspace's declarations, the index handed the folders and woken up first so it's looking by the
    /// time the first key's typed. With the texts of the game after them as well, for quick open's #, which was only them.
    /// Everything the rows need of the editor is taken now, on its thread, so the search reads nothing of it.
    /// </summary>
    private UIChoiceSearch SymbolSearch(bool withTexts)
    {
        harnessRoots = [.. WorkspaceRoots()];
        HexCodeIndex index = Harness;
        Task.Run(() => index.Look());

        // The line under a declaration with its parts as {0} and so on, the words of it taken here
        string inside = Said("go_to_all.detail", ("kind", "{0}"), ("container", "{1}"), ("file", "{2}"), ("line", "{3}"));
        string top = Said("go_to_all.detail_top", ("kind", "{0}"), ("file", "{2}"), ("line", "{3}"));
        Dictionary<string, string> kinds = KindsSaid.ToDictionary(kind => kind, kind => Said("go_to_all.kind_" + kind));
        List<(string Key, string Says)> texts = withTexts
            ? [.. Localisation.Keys.Order(StringComparer.Ordinal).Select(key => (key, HexEvaluation.Shorten(Localisation.Get(key), 60)))]
            : [];

        return (typed, cancel) =>
        {
            var rows = new List<UIChoice>();
            foreach (var (file, symbol, _) in HexCodeIndex.GoToAll(index.Current(), typed, GO_TO_ALL_MOST, cancel))
            {
                string path = file.Path;
                int line = symbol.Line, column = symbol.Column;
                string icon = "symbol-" + symbol.Kind;
                string detail = string.Format(CultureInfo.InvariantCulture, symbol.Container.Length > 0 ? inside : top,
                    kinds.GetValueOrDefault(symbol.Kind, symbol.Kind), symbol.Container, file.Relative, line + 1);
                rows.Add(new UIChoice(symbol.Name, () => OpenAt(path, line, column))
                {
                    Detail = detail,
                    Icon = icon,
                    IconColor = OutlineColor(icon),
                    Id = $"{symbol.Full}  {file.Relative}:{line + 1}"
                });
            }

            foreach (var (key, says) in texts.Where(text => typed.Trim().Length > 0 && UIChooser.Matches(text.Key, typed)).Take(TEXTS_MOST))
            {
                if (cancel.IsCancellationRequested)
                    break;
                rows.Add(new UIChoice(key, () => codeEditing.GoToKey(key)) { Detail = says, Icon = "symbol-string", Id = $"{key}   {says}" });
            }

            return rows;
        };
    }
}
