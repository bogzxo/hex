using Horizon.Platform;
using Horizon.UI.Components;

namespace Hex.Tests;

/// <summary>
/// The pure parts of the Mac's native menu bar, which run anywhere: how a key as the menus write it becomes an NSMenuItem's
/// key equivalent, and how the menus of a MenuBar become the platform's menus. Nothing here touches Objective-C.
/// </summary>
public sealed class MacMenuTests
{
    private const ulong Shift = MacKeyEquivalents.Shift, Control = MacKeyEquivalents.Control, Option = MacKeyEquivalents.Option, Command = MacKeyEquivalents.Command;

    public static TheoryData<string, string, ulong> Keys => new()
    {
        { "Ctrl+O", "o", Command },
        { "Ctrl+Shift+P", "P", Command | Shift },
        { "ctrl+shift+p", "P", Command | Shift },
        { "Cmd+S", "s", Command },
        { "RawCtrl+Tab", "\t", Control },
        { "Ctrl+Alt+B", "b", Command | Option },
        { "Alt+Up", MacKeyEquivalents.Up, Option },
        { "Down", MacKeyEquivalents.Down, 0 },
        { "Ctrl+Left", MacKeyEquivalents.Left, Command },
        { "F5", "", 0 },
        { "Ctrl+F5", "", Command },
        { "Shift+F11", "", Shift },
        { "F1", "", 0 },
        { "Delete", MacKeyEquivalents.Delete, 0 },
        { "Ctrl+Del", MacKeyEquivalents.Delete, Command },
        { "Backspace", "\b", 0 },
        { "Esc", "\u001B", 0 },
        { "Escape", "\u001B", 0 },
        { "Return", "\r", 0 },
        { "Ctrl+Enter", "\r", Command },
        { "Tab", "\t", 0 },
        { "Space", " ", 0 },
        { "Ctrl+,", ",", Command },
        { "Ctrl+`", "`", Command },
        { "Ctrl+Shift+[", "[", Command | Shift },
        { "Ctrl+1", "1", Command },
        { "Ctrl+Number1", "1", Command },
        { "Ctrl+Minus", "-", Command },
        { "Ctrl+ - ", "-", Command },
        { "Home", MacKeyEquivalents.Home, 0 },
        { "Ctrl+End", MacKeyEquivalents.End, Command },
        { "PageUp", MacKeyEquivalents.PageUp, 0 },
    };

    [Theory]
    [MemberData(nameof(Keys))]
    public void AKeyBecomesAKeyEquivalent(string written, string key, ulong modifiers)
    {
        MacKeyEquivalent? mapped = MacKeyEquivalents.Map(written);
        Assert.NotNull(mapped);
        Assert.Equal(key, mapped.Value.Key);
        Assert.Equal(modifiers, mapped.Value.Modifiers);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl+K Z")]
    [InlineData("Ctrl+K Ctrl+S")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Shift")]
    [InlineData("Ctrl+Nonsense")]
    [InlineData("A B C")]
    public void NoKeyEquivalentForWhatIsNotOneKey(string written)
    {
        Assert.Null(MacKeyEquivalents.Map(written));
    }

    [Fact]
    public void NullIsNoKey() => Assert.Null(MacKeyEquivalents.Map(null));

    [Fact]
    public void AChordIsKnownAsOne()
    {
        Assert.True(MacKeyEquivalents.IsChord("Ctrl+K Z"));
        Assert.True(MacKeyEquivalents.IsChord("Ctrl+K Ctrl+S"));
        Assert.False(MacKeyEquivalents.IsChord("Ctrl+K"));
        Assert.False(MacKeyEquivalents.IsChord("ctrl + alt + b"));
        Assert.False(MacKeyEquivalents.IsChord(""));
    }

    [Fact]
    public void GapsAroundAPlusAreNotAChord()
    {
        MacKeyEquivalent? mapped = MacKeyEquivalents.Map("ctrl + alt + b");
        Assert.Equal(new MacKeyEquivalent("b", Command | Option), mapped);
    }

    [Fact]
    public void GlyphsWriteTheMacsOwnSigns()
    {
        Assert.Equal("⌘K Z", MacKeyEquivalents.Glyphs("Ctrl+K Z"));
        Assert.Equal("⇧⌘P", MacKeyEquivalents.Glyphs("Ctrl+Shift+P"));
        Assert.Equal("⌃⌥⇧⌘F5", MacKeyEquivalents.Glyphs("RawCtrl+Alt+Shift+Ctrl+F5"));
        Assert.Equal(string.Empty, MacKeyEquivalents.Glyphs(""));
    }

    /* The bridge */

    private static MenuBar SmallBar(out MenuItem hidden, out MenuItem disabled, out MenuItem ticked, out List<string> done)
    {
        var picks = new List<string>();
        done = picks;

        var bar = new MenuBar();
        Menu file = bar.AddMenu("File");
        file.Add("New", () => picks.Add("New"), "Ctrl+N");
        file.Add("Open...", () => picks.Add("Open"), "Ctrl+O");
        file.AddSeparator();
        hidden = file.Add("Secret", () => picks.Add("Secret"));
        hidden.IsVisible = () => false;
        file.AddSeparator();
        disabled = file.Add("Save", () => picks.Add("Save"), "Ctrl+S");
        disabled.IsEnabled = () => false;
        ticked = file.Add("Autosave", () => picks.Add("Autosave"));
        ticked.IsChecked = () => true;
        Menu recent = file.AddMenu("Open Recent");
        recent.Add("a.txt", () => picks.Add("a"));

        Menu layout = bar.AddMenu("Layout");
        layout.IsVisible = () => false;
        layout.Add("Group", () => { }, "Ctrl+G");

        Menu help = bar.AddMenu("Help");
        help.Add("About", () => picks.Add("About"));
        return bar;
    }

    [Fact]
    public void TheBridgeMakesTheMenusOfABar()
    {
        MenuBar bar = SmallBar(out _, out _, out _, out _);

        IReadOnlyList<PlatformMenu> menus = UIMenuBridge.FromBar(bar, title => title == "Help" ? PlatformMenuRole.HelpSearch : PlatformMenuRole.None);

        Assert.Equal(["File", "Layout", "Help"], menus.Select(menu => menu.Title));
        Assert.Equal(PlatformMenuRole.HelpSearch, menus[2].Role);
        Assert.Equal(PlatformMenuRole.None, menus[0].Role);
        Assert.True(menus[0].IsVisible());
        Assert.False(menus[1].IsVisible());
        Assert.True(menus[2].IsVisible());
    }

    [Fact]
    public void ItemsCarryTheirTitlesKeysAndState()
    {
        MenuBar bar = SmallBar(out _, out _, out _, out List<string> done);

        IReadOnlyList<PlatformMenuItem> items = UIMenuBridge.FromBar(bar)[0].Items;

        // The hidden item is gone, and so is the rule that would have been doubled by it
        Assert.Equal(["New", "Open...", "", "Save", "Autosave", "Open Recent"], items.Select(item => item.Label));
        Assert.Equal([false, false, true, false, false, false], items.Select(item => item.IsSeparator));
        Assert.Equal("Ctrl+N", items[0].Shortcut);
        Assert.True(items[0].IsEnabled());
        Assert.False(items[3].IsEnabled());
        Assert.True(items[4].IsChecked());
        Assert.False(items[0].IsChecked());

        items[1].Picked!();
        Assert.Equal(["Open"], done);
    }

    [Fact]
    public void ASubmenuIsAMenuOfItsOwn()
    {
        MenuBar bar = SmallBar(out _, out _, out _, out _);

        PlatformMenuItem recent = UIMenuBridge.FromBar(bar)[0].Items.Last();

        Assert.NotNull(recent.Submenu);
        Assert.Equal("Open Recent", recent.Submenu.Title);
        Assert.Equal(["a.txt"], recent.Submenu.Items.Select(item => item.Label));
        Assert.Null(recent.Picked);
    }

    [Fact]
    public void AMenuIsReadAgainAsItChanges()
    {
        var bar = new MenuBar();
        Menu file = bar.AddMenu("File");
        file.Add("New", () => { });
        Menu recent = file.AddMenu("Recent");
        file.Items[1].IsVisible = recent.HasShown;

        PlatformMenu mirrored = UIMenuBridge.FromBar(bar)[0];
        Assert.Single(mirrored.Items);

        // Nothing in the submenu: it isn't there. Something put in it later makes it appear in the same mirror
        recent.Add("a.txt", () => { });
        Assert.Equal(["New", "Recent"], mirrored.Items.Select(item => item.Label));

        recent.Clear();
        Assert.Equal(["New"], mirrored.Items.Select(item => item.Label));
    }

    [Fact]
    public void RulesCollapse()
    {
        var menu = new Menu("Edit");
        menu.AddSeparator();
        MenuItem a = menu.Add("A", () => { });
        menu.AddSeparator();
        MenuItem b = menu.Add("B", () => { });
        menu.AddSeparator();
        MenuItem c = menu.Add("C", () => { });
        menu.AddSeparator();

        Assert.Equal(["A", "", "B", "", "C"], UIMenuBridge.ItemsOf(menu).Select(item => item.Label));

        b.IsVisible = () => false;
        Assert.Equal(["A", "", "C"], UIMenuBridge.ItemsOf(menu).Select(item => item.Label));

        a.IsVisible = () => false;
        Assert.Equal(["C"], UIMenuBridge.ItemsOf(menu).Select(item => item.Label));

        c.IsVisible = () => false;
        Assert.Empty(UIMenuBridge.ItemsOf(menu));
    }

    [Fact]
    public void AnItemForThePaletteOnlyIsLeftOut()
    {
        var menu = new Menu("View");
        menu.Add("Shown", () => { });
        menu.Add("Rare", () => { }).PaletteOnly = true;

        Assert.Equal(["Shown"], UIMenuBridge.ItemsOf(menu).Select(item => item.Label));
    }

    [Fact]
    public void RolesAreKeptOnTheMenusOnly()
    {
        var menu = new PlatformMenu("Window", [PlatformMenuItem.Separator()], PlatformMenuRole.WindowList);
        Assert.Equal(PlatformMenuRole.WindowList, menu.Role);
        Assert.False(menu.IsDynamic);
        Assert.False(menu.Items[0].IsEnabled());
    }
}
