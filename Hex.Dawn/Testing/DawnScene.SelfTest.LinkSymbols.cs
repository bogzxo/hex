using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    // The first path segment looks exactly like another advertised root. Only the index host
    // can tell that this declaration belongs to the clone, inside a directory of that name.
    private static void LinkTestSeedSymbol(string clone)
    {
        string nested = Path.Combine(clone, LINK_PEER_FOLDER);
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "Remote Symbol.cs"), "// fixture\n\npublic class LinkSymbolTarget { }\n");
    }

    private void TestLinkSymbolNavigation(HexPointerScript test, Func<HexLinkPeer?> peer)
    {
        const string query = "LinkSymbolTarget";
        string wire = $"{LINK_SYNC_FOLDER}/{LINK_PEER_FOLDER}/Remote Symbol.cs";
        string command = Said("link.remote_go_to_symbol");
        string result = $"class {query}   {wire}:3";
        ListBox? Rows() => textDialog is { IsOpen: true } dialog ? Descendants(dialog).OfType<ListBox>().FirstOrDefault() : null;
        void Choose(string label)
        {
            if (Rows() is not { } list)
                return;
            int index = Array.IndexOf(list.Items, label);
            if (index < 0)
                return;
            list.Choose(index);
            list.OnActivated?.Invoke(label);
        }

        test.Run(() =>
        {
            if (peer() is { } other)
                LinkOpenRemoteCommand(other);
        });
        test.WaitFor(() => Rows()?.Items.Contains(command) == true, 20.0f);
        test.Run(() =>
        {
            Console.WriteLine($"[Hex self-test] link-files: remote browser choices: {string.Join(" | ", Rows()?.Items ?? [])}");
            Choose(command);
            if (textDialog is { IsOpen: true } dialog)
                Descendants(dialog).OfType<TextBox>().FirstOrDefault()?.OnSubmitted?.Invoke(query);
        });
        test.WaitFor(() => Rows()?.Items.Contains(result) == true, 30.0f);
        test.Check("link-files: Go to Symbol shows the indexed root and declaration line", () =>
        {
            Console.WriteLine($"[Hex self-test] link-files: symbol choices: {string.Join(" | ", Rows()?.Items ?? [])}");
            return Rows()?.Items.Contains(result) == true;
        });
        test.Run(() => Choose(result));
        bool AtSymbol() => peer() is { } other && document.Path is { } path
            && PlatformPaths.Same(path, LinkRemoteCopyOf(other.Device.Id, wire)) && document.Editor.Document.Caret == new UITextPosition(2, 0);
        test.WaitFor(AtSymbol, 20.0f);
        test.Check("link-files: selecting the symbol opens its remote copy at the declaration", () => AtSymbol()
            && document.Editor.Text.Contains("public class LinkSymbolTarget", StringComparison.Ordinal));
    }
}
