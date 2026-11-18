using System.Globalization;
using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Skinning;

namespace Hex.Dawn;

// What the harness saved, drawn the way Horizon's performance overlay draws a frame. Along the top the saving as one large
// figure and tiles of what it's made of, each a small heading, its number and a hair-thin meter. Then a bar cut into each
// tool's part of the saving, the way the overlay cuts the GPU's frame into its passes, and under it a table a tool a row,
// a dot of the tool's colour in that bar, its name, its numbers in columns lined up on the right and a meter of how much
// of what it would have read it saved. The agent systems' share in the same table under them, and what the numbers are
// last. Before, it was cards a tool each in the window's own type and read as put together in a hurry.
internal sealed partial class DawnScene
{
    // How tall a row of the table is, and how wide its columns of numbers are from the right
    private const float HARNESS_ROW = 20.0f;
    private const float COL_PERCENT = 44, COL_METER = 74, COL_BASE = 72, COL_BACK = 72, COL_CACHED = 54, COL_CALLS = 58;

    // The colours of the tools' parts of the saving, told apart in the bar and on their dots
    private static readonly Vector4[] HARNESS_HUES =
    [
        new(0.38f, 0.62f, 1.0f, 1.0f), new(0.32f, 0.82f, 0.72f, 1.0f), new(0.55f, 0.82f, 0.4f, 1.0f), new(0.98f, 0.76f, 0.32f, 1.0f),
        new(0.74f, 0.55f, 0.98f, 1.0f), new(0.96f, 0.5f, 0.68f, 1.0f), new(0.98f, 0.58f, 0.36f, 1.0f), new(0.45f, 0.78f, 0.95f, 1.0f)
    ];

    /// <summary>What the harness saved since the count began, its window opened, or closed when it's up.</summary>
    private void ShowHarnessSaved()
    {
        // A toggle, pressed again it closes the window that's up rather than opening another over it
        if (harnessWindow is { } open)
        {
            open.Close();
            return;
        }

        var tools = Harness.Stats.Rows();
        var clients = Harness.Stats.ClientRows();
        var window = new WindowPanel(Said("agents_panel.harness_title"))
        {
            Icon = "zap",
            Size = new Vector2(720.0f, MathF.Min(760.0f, 330.0f + (tools.Count + clients.Count) * HARNESS_ROW)),
            MinSize = new Vector2(560.0f, 260.0f)
        };
        DressWindow(window);
        harnessWindow = window;
        window.OnClosed = () =>
        {
            window.Parent?.Remove(window);
            harnessWindow = null;
        };

        var scroll = window.Add(new ScrollPanel { Fill = UIFill.Both, Padding = new UIEdges(18, 14, 12, 16) });
        var column = scroll.Add(new StackPanel { Spacing = 8.0f, Fill = UIFill.Horizontal });
        if (tools.Count == 0)
            column.Add(WindowEmpty("zap", Said("agents_panel.harness_none")));
        else
            DrawHarnessSaved(column, tools, clients);

        chrome.Root.Add(window);
        window.TakeKeyboard();
    }

    private void DrawHarnessSaved(StackPanel column, List<(string Name, long Calls, long Kept, long Said, long Plain)> tools,
        List<(string Name, long Calls, long Kept, long Said, long Plain)> clients)
    {
        UISkin? skin = Skin;
        Vector4 good = skin?.Palette.Success ?? new Vector4(0.35f, 0.8f, 0.45f, 1.0f);
        Vector4 track = skin?.Palette.Track ?? new Vector4(0, 0, 0, 0.35f);
        var (calls, said, plain) = Harness.Stats.Totals();
        long kept = tools.Sum(tool => tool.Kept);
        double share = plain <= 0 ? 0 : (double)(plain - said) / plain;

        // The large figure and the tiles beside it
        var head = column.Add(new Panel { Fill = UIFill.Horizontal, Size = new Vector2(0, 54), Color = Vector4.Zero });
        head.Add(new Label
        {
            Text = $"[color:{HexColour(good)}]{share * 100:0}[/color][dim]%[/dim]", TextScale = 0.34f, Bold = true, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Position = new Vector2(-1, 2)
        });
        head.Add(new Label { Text = UIRichText.Literal(Said("agents_panel.harness_saved_word")), TextScale = 0.12f, Dim = true, Anchor = Origin.BottomLeft, Pivot = Origin.BottomLeft });
        var tiles = head.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 18, Anchor = Origin.TopRight, Pivot = Origin.TopRight });
        HarnessTile(tiles, Said("agents_panel.harness_tile_saved"), HexHarnessStats.Tokens(plain - said), Said("agents_panel.harness_tokens"), share, good, track);
        HarnessTile(tiles, Said("agents_panel.harness_tile_back"), HexHarnessStats.Tokens(said), Said("agents_panel.harness_tokens"), plain <= 0 ? 0 : (double)said / plain, RUNNING_BLUE, track);
        HarnessTile(tiles, Said("agents_panel.harness_tile_plain"), HexHarnessStats.Tokens(plain), Said("agents_panel.harness_tokens"), 1.0, skin?.Palette.DimText ?? new Vector4(0.6f, 0.6f, 0.6f, 1), track);
        HarnessTile(tiles, Said("agents_panel.harness_tile_calls"), calls.ToString("N0", CultureInfo.InvariantCulture),
            Said("agents_panel.harness_cached", ("count", kept.ToString("N0", CultureInfo.InvariantCulture))), calls <= 0 ? 0 : (double)kept / calls, HARNESS_HUES[4], track);
        column.Add(HarnessRule());

        // Each tool's part of the saving, the bar and the table
        long saving = Math.Max(1, tools.Sum(tool => Math.Max(0, tool.Plain - tool.Said)));
        HarnessHeading(column, Said("agents_panel.harness_by_tool"), Said("agents_panel.harness_by_tool_note"));
        column.Add(new HexStackBar
        {
            Fill = UIFill.Horizontal, Size = new Vector2(0, 10), Track = track,
            Parts = [.. tools.Select((tool, at) => ((double)Math.Max(0, tool.Plain - tool.Said) / saving, HARNESS_HUES[at % HARNESS_HUES.Length]))]
        });
        column.Add(HarnessColumns(Said("agents_panel.harness_col_tool"), cached: true));
        for (int at = 0; at < tools.Count; at++)
        {
            var tool = tools[at];
            column.Add(HarnessTableRow(tool.Name, HARNESS_HUES[at % HARNESS_HUES.Length], tool.Calls, tool.Kept, tool.Said, tool.Plain, track));
        }

        // The agent systems' share
        if (clients.Count > 0)
        {
            column.Add(HarnessRule());
            HarnessHeading(column, Said("agents_panel.harness_by_client"), Said("agents_panel.harness_by_client_note"));
            column.Add(HarnessColumns(Said("agents_panel.harness_col_client"), cached: false));
            foreach (var client in clients)
                column.Add(HarnessTableRow(client.Name, AgentTintOfClient(client.Name), client.Calls, null, client.Said, client.Plain, track));
        }

        column.Add(HarnessRule());
        column.Add(new Label { Text = UIRichText.Literal(Said("agents_panel.harness_about")), TextScale = 0.11f, Dim = true, Wrap = true, Fill = UIFill.Horizontal, Align = Origin.Left });
    }

    // A tile of the head, its small heading, its number large with its unit dim after it, and a hair of a meter under them
    private static void HarnessTile(StackPanel tiles, string heading, string value, string unit, double share, Vector4 colour, Vector4 track)
    {
        var tile = tiles.Add(new StackPanel { Spacing = 2, Size = new Vector2(96, 54) });
        tile.Add(new Label { Text = UIRichText.Literal(heading.ToUpperInvariant()), TextScale = 0.1f, Dim = true, Align = Origin.Left, Fill = UIFill.Horizontal });
        tile.Add(new Label { Text = $"{UIRichText.Literal(value)} [dim]{UIRichText.Literal(unit)}[/dim]", TextScale = 0.16f, Bold = true, Align = Origin.Left, Fill = UIFill.Horizontal });
        tile.Add(new HexShareBar { Share = (float)Math.Clamp(share, 0, 1), Colour = colour, Track = track, Fill = UIFill.Horizontal, Size = new Vector2(0, 3) });
    }

    // A section's heading in small capitals with a note dim at its right, as the overlay heads its parts
    private static void HarnessHeading(StackPanel column, string heading, string note)
    {
        var title = column.Add(new Panel { Fill = UIFill.Horizontal, Size = new Vector2(0, 16), Color = Vector4.Zero });
        title.Add(new Label { Text = UIRichText.Literal(heading.ToUpperInvariant()), TextScale = 0.11f, Bold = true, Anchor = Origin.Left, Pivot = Origin.Left });
        title.Add(new Label { Text = UIRichText.Literal(note), TextScale = 0.11f, Dim = true, Anchor = Origin.Right, Pivot = Origin.Right });
    }

    // A hair of a line between the parts
    private Panel HarnessRule()
    {
        Vector4 line = Skin?.BorderColor ?? new Vector4(1, 1, 1, 0.08f);
        return new Panel { Fill = UIFill.Horizontal, Size = new Vector2(0, 1), Color = line };
    }

    // The table's column names, dim and right-aligned over the numbers they name
    private Panel HarnessColumns(string first, bool cached)
    {
        var row = new Panel { Fill = UIFill.Horizontal, Size = new Vector2(0, 16), Color = Vector4.Zero };
        row.Add(new Label { Text = UIRichText.Literal(first), TextScale = 0.105f, Dim = true, Anchor = Origin.Left, Pivot = Origin.Left, Position = new Vector2(16, 0) });
        float at = 0;
        void Column(string name, float wide)
        {
            row.Add(new Label { Text = UIRichText.Literal(name), TextScale = 0.105f, Dim = true, Anchor = Origin.Right, Pivot = Origin.Right, Position = new Vector2(-at, 0) });
            at += wide;
        }

        Column(Said("agents_panel.harness_col_saved"), COL_PERCENT + COL_METER);
        Column(Said("agents_panel.harness_col_plain"), COL_BASE);
        Column(Said("agents_panel.harness_col_back"), COL_BACK);
        Column(cached ? Said("agents_panel.harness_col_cached") : string.Empty, COL_CACHED);
        Column(Said("agents_panel.harness_col_calls"), COL_CALLS);
        return row;
    }

    // A row of the table, the dot of its colour and its name, then its numbers in their columns and a meter of what it saved
    private Panel HarnessTableRow(string name, Vector4 dot, long calls, long? kept, long said, long plain, Vector4 track)
    {
        UISkin? skin = Skin;
        double share = plain <= 0 ? 0 : (double)(plain - said) / plain;
        // Under half saved is the warning's colour, the tool reads about as much as the plain way would
        Vector4 colour = share >= 0.5 ? skin?.Palette.Success ?? new Vector4(0.35f, 0.8f, 0.45f, 1.0f) : skin?.Palette.Warning ?? new Vector4(1.0f, 0.78f, 0.3f, 1.0f);
        var row = new Panel { Fill = UIFill.Horizontal, Size = new Vector2(0, HARNESS_ROW), Color = Vector4.Zero };
        row.Add(new Panel { Anchor = Origin.Left, Pivot = Origin.Left, Position = new Vector2(2, 0), Size = new Vector2(7, 7), Radius = 3.5f, Color = dot });
        row.Add(new Label { Text = UIRichText.Literal(name), TextScale = 0.125f, Anchor = Origin.Left, Pivot = Origin.Left, Position = new Vector2(16, 0) }).Also(label => label.CodeFont = code.Font);

        float at = 0;
        void Number(string text, float wide, bool dim = false)
        {
            row.Add(new Label { Text = text, TextScale = 0.125f, Dim = dim, Anchor = Origin.Right, Pivot = Origin.Right, Position = new Vector2(-at, 0) });
            at += wide;
        }

        Number($"[color:{HexColour(colour)}]{share * 100:0}%[/color]", COL_PERCENT);
        row.Add(new HexShareBar { Share = (float)share, Colour = colour, Track = track, Anchor = Origin.Right, Pivot = Origin.Right, Position = new Vector2(-at - 6, 0), Size = new Vector2(COL_METER - 12, 4) });
        at += COL_METER;
        Number(UIRichText.Literal(HexHarnessStats.Tokens(plain)), COL_BASE, dim: true);
        Number(UIRichText.Literal(HexHarnessStats.Tokens(said)), COL_BACK);
        Number(kept is { } hits ? hits.ToString("N0", CultureInfo.InvariantCulture) : string.Empty, COL_CACHED, dim: true);
        Number(calls.ToString("N0", CultureInfo.InvariantCulture), COL_CALLS);
        return row;
    }

    // The colour of the agent system an MCP client's name belongs to, by its profile's client names
    private Vector4 AgentTintOfClient(string client)
    {
        foreach (HexAgentProfile profile in HexAgentProfiles.All)
        {
            if (profile.Clients.Any(name => client.Contains(name, StringComparison.OrdinalIgnoreCase)))
                return AgentTint(profile.Id, Skin);
        }

        return Skin?.Palette.DimText ?? new Vector4(0.6f, 0.6f, 0.6f, 1.0f);
    }
}
