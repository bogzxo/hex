using System.Numerics;

namespace Hex.Dawn;

// A press on a reference count over a declaration (DawnScene.CodeLens), everywhere it counts listed in a menu under it, by
// file, each file a heading and each place its line number and its line, Visual Studio's code lens list. Picking one goes
// there. HIDL's places are known already, a server's are asked for when it's pressed (a code lens only says how many).
internal sealed partial class DawnScene
{
    // The most places the menu lists, the rest are in the side bar's results by the item at the bottom, and how much of
    // a line it says
    private const int LENS_MENU_MOST = 60;
    private const int LENS_LINE_MOST = 90;

    /// <summary>Called by an editor with the count pressed and where it was painted.</summary>
    private void PressReferenceLens(HexDocument of, UICodeLensAbove lens, UIRect box)
    {
        if (!lensFiles.TryGetValue(of, out LensFile? file) || lens.Id < 0 || lens.Id >= file.Declarations.Count)
            return;

        LensDeclaration declared = file.Declarations[lens.Id];
        Vector2 at = of.Editor.Module is { } module ? module.ToWorld(new Vector2(box.Min.X, box.Min.Y)) : compositor.Pointer.Position;
        if (declared.Places is { } known)
        {
            ShowReferencesMenu(of, declared, known, at);
            return;
        }

        // Typed into since the count came, the editor's moved the count along with its line, and the name went with it
        int line = declared.Line + (lens.Line - declared.LensLine);
        if (!languages.AskReferences(of, line, declared.Column, places => ShowReferencesMenu(of, declared, UsesOf(of, declared, places, line), at)))
            Say(Said("code_lens.no_server"), error: true);
    }

    /// <summary>The menu of everywhere a declaration is used, under its count. A declaration nothing uses says so instead.</summary>
    private void ShowReferencesMenu(HexDocument of, LensDeclaration declared, IReadOnlyList<LanguageServerPlace> places, Vector2 at)
    {
        if (places.Count == 0)
        {
            Say(Said("code_lens.unused", ("name", declared.Name)));
            return;
        }

        List<LanguageServerPlace> sorted = [.. places.OrderBy(place => place.Path, PlatformPaths.Comparer).ThenBy(place => place.Line).ThenBy(place => place.Character)];
        var lines = new Dictionary<string, string[]>(PlatformPaths.Comparer);
        var menu = new Menu("references");
        string? file = null;
        for (int i = 0; i < sorted.Count && i < LENS_MENU_MOST; i++)
        {
            LanguageServerPlace place = sorted[i];
            if (file is null || !PlatformPaths.Same(file, place.Path))
            {
                if (file is not null)
                    menu.AddSeparator();
                file = place.Path;
                string inFile = place.Path;
                string name = place.Path.Length > 0 ? Path.GetFileName(place.Path) : of.Name;
                int count = sorted.Count(other => PlatformPaths.Same(other.Path, inFile));
                menu.Add(Said("code_lens.file", ("file", name), ("count", count.ToString())), static () => { }, icon: UIFileKinds.IconOf(name.Length > 0 ? name : "x.txt"))
                    .IsEnabled = static () => false;
            }

            string text = LineOfPlace(of, place, lines).Trim();
            if (text.Length > LENS_LINE_MOST)
                text = string.Concat(text.AsSpan(0, LENS_LINE_MOST - 3), "...");
            menu.Add($"{place.Line + 1}   {text}", () => GoToReference(of, place));
        }

        if (sorted.Count > LENS_MENU_MOST)
        {
            menu.AddSeparator();
            menu.Add(Said("code_lens.all", ("count", sorted.Count.ToString())), () => ListReferences(of, declared, sorted), icon: "references");
        }

        contextMenu = ContextMenu.ShowAt(chrome, at, menu);
    }

    // The line of a place, off the editor of its file when it's open (the file on screen's even when it's untitled) or the disk
    private string LineOfPlace(HexDocument of, LanguageServerPlace place, Dictionary<string, string[]> lines)
    {
        if (!lines.TryGetValue(place.Path, out string[]? text))
        {
            HexDocument? open = place.Path.Length == 0 ? of : documents.Find(other => other.Path is { } path && PlatformPaths.Same(path, place.Path));
            lines[place.Path] = text = open is not null ? open.Editor.Text.Split('\n') : HexSearch.ReadLines(place.Path);
        }

        return place.Line < text.Length ? text[place.Line].TrimEnd('\r') : string.Empty;
    }

    // Where a place picked in the menu is, the file it's in brought up (or opened) with the caret on the name
    private void GoToReference(HexDocument of, LanguageServerPlace place)
    {
        if (place.Path.Length == 0 || (of.Path is { } path && PlatformPaths.Same(path, place.Path)))
        {
            if (!documents.Contains(of))
                return;
            Show(of);
            code.GoTo(place.Line, place.Character);
            code.Focus();
            return;
        }

        OpenAt(place.Path, place.Line, place.Character);
    }

    // Every place in the side bar's results, for a declaration used in more places than the menu lists
    private void ListReferences(HexDocument of, LensDeclaration declared, List<LanguageServerPlace> places)
    {
        var lines = new Dictionary<string, string[]>(PlatformPaths.Comparer);
        var found = new List<HexResult>(places.Count);
        foreach (LanguageServerPlace place in places)
        {
            string said = LineOfPlace(of, place, lines);
            int lead = said.Length - said.TrimStart().Length;
            found.Add(new HexResult(place.Path.Length > 0 ? place.Path : of.Path ?? of.Name, place.Line, place.Character, said.Trim(), declared.Name.Length, Math.Max(-1, place.Character - lead)));
        }

        ShowResults(Said("code_server.references_title", ("word", declared.Name), ("count", places.Count.ToString())), found);
    }
}
