using System.Numerics;
using System.Text.Json.Nodes;

using Horizon.Graphics;

using Key = Silk.NET.Input.Key;

namespace Hex.Dawn;

// The tools that look at the window and use it the way somebody with a mouse and a keyboard would. A screenshot, the list
// of what's on screen with ids to point at, a click, typing, a key, the wheel and resting the pointer on something. The
// pointer is the agent's own (HexAgentPointer), the keys go in where the real ones do, so the editor can't tell them apart.
internal sealed partial class DawnScene
{
    private HexAgentPointer? agentPointerMade;
    private HexAgentPointer agentPointer => agentPointerMade ??= new HexAgentPointer([compositor, stage]);

    // The arguments that say what to point at, shared by click, hover and scroll
    private static IEnumerable<(string, JsonObject)> TargetArguments() =>
    [
        ("id", McpSchema.String("An id from hex_ui_tree (c12, r40, s3, m1, i4).")),
        ("text", McpSchema.String("Visible text to find and point at, exact matches and buttons first.")),
        ("x", McpSchema.Number("Window pixels from the left, as in a screenshot at scale 1.")),
        ("y", McpSchema.Number("Window pixels from the top."))
    ];

    private IEnumerable<HexMcpTool> ScreenTools() =>
    [
        new(new McpTool("hex_screenshot", "Screenshot",
            "Take a PNG screenshot of the Hex window (optionally a region, optionally scaled down). Coordinates are window pixels, the same ones hex_click takes.",
            McpSchema.Object([
                ("x", McpSchema.Number("Left of the region in window pixels.")),
                ("y", McpSchema.Number("Top of the region in window pixels.")),
                ("width", McpSchema.Number("Width of the region.")),
                ("height", McpSchema.Number("Height of the region.")),
                ("scale", McpSchema.Number("Shrink the picture by this much, 0.1 to 1, 1 as it comes."))]), ReadOnly: true),
            Screenshot, Timeout: 20.0f),

        new(new McpTool("hex_ui_tree", "UI tree",
            "List what is on screen (components, tree rows, status bar items, menus) with kind, text, bounds in window pixels and ids for hex_click.",
            McpSchema.Object([
                ("interactive", McpSchema.Flag("Only what can be clicked or typed into, flat. Much shorter.")),
                ("find", McpSchema.String("Only things whose text or name contains this.")),
                ("root", McpSchema.String("Only what is inside this id.")),
                ("max_depth", McpSchema.Integer("How deep to go, everything by default.")),
                ("layouts", McpSchema.Flag("Also the components of the layout open in the designer's canvas.")),
                ("hidden", McpSchema.Flag("Also what is hidden right now."))]), ReadOnly: true),
            UiTree),

        new(new McpTool("hex_click", "Click",
            "Click something by id from hex_ui_tree, by its visible text, or at x,y window pixels. Left by default, right for a context menu, or a double click.",
            McpSchema.Object([.. TargetArguments(),
                ("button", McpSchema.OneOf("Which button.", "left", "right")),
                ("double", McpSchema.Flag("A double click."))])),
            Click),

        new(new McpTool("hex_type", "Type",
            "Type text into whatever has the keyboard (click a box or the code first), as if typed. A new line is Enter.",
            McpSchema.Object([("text", McpSchema.String("What to type."))], "text")),
            TypeText, Timeout: 60.0f),

        new(new McpTool("hex_key", "Press keys",
            "Press a key or a chord like Ctrl+Shift+P, Enter, Escape, F12, Alt+Up. Chords one after another are separated by spaces (Ctrl+K Ctrl+S).",
            McpSchema.Object([("keys", McpSchema.String("The keys, the way a menu writes them."))], "keys")),
            PressKeys),

        new(new McpTool("hex_scroll", "Scroll",
            "Turn the mouse wheel over something (by id, text or x,y, else where the pointer is). Positive scrolls up, negative down.",
            McpSchema.Object([.. TargetArguments(), ("amount", McpSchema.Number("Notches of the wheel, -3 by default."))])),
            Scroll),

        new(new McpTool("hex_hover", "Hover",
            "Rest the pointer on something (by id, text or x,y) for a while and say what is under it and any tooltip that came up.",
            McpSchema.Object([.. TargetArguments(), ("seconds", McpSchema.Number("How long to rest there, 1.2 by default."))])),
            Hover)
    ];

    private IEnumerable<McpToolResult?> Screenshot(JsonObject arguments)
    {
        Task<ImagePixels> capture = Engine.CapturePixels();
        while (!capture.IsCompleted)
            yield return null;

        ImagePixels frame = capture.Result;
        int x = Math.Clamp(arguments.Integer("x") ?? 0, 0, frame.Width - 1), y = Math.Clamp(arguments.Integer("y") ?? 0, 0, frame.Height - 1);
        int width = Math.Clamp(arguments.Integer("width") ?? frame.Width - x, 1, frame.Width - x);
        int height = Math.Clamp(arguments.Integer("height") ?? frame.Height - y, 1, frame.Height - y);
        float scale = (float)Math.Clamp(arguments.Number("scale") ?? 1.0, 0.1, 1.0);

        // Cut, shrunk and made a PNG off the editor's thread, a big window is a few megabytes to go through
        Task<(byte[] Png, int Width, int Height)> made = Task.Run(() => HexPictures.Cut(frame, x, y, width, height, scale));
        while (!made.IsCompleted)
            yield return null;

        var (png, shownWidth, shownHeight) = made.Result;
        string region = x == 0 && y == 0 && width == frame.Width && height == frame.Height ? "the whole window" : $"the region at {x},{y} {width}x{height}";
        string scaled = scale < 1.0f ? $", shown at {scale:0.##} so divide its pixel positions by {scale:0.##} and add the region's corner for hex_click" : string.Empty;
        yield return McpToolResult.Image(png, $"Hex's window is {frame.Width}x{frame.Height} window pixels. This is {region}, {shownWidth}x{shownHeight}{scaled}.");
    }

    private IEnumerable<McpToolResult?> UiTree(JsonObject arguments)
    {
        List<HexScreenNode> nodes = McpScreen(arguments.Flag("layouts"), arguments.Flag("hidden"));

        if (arguments.String("root") is { } root)
        {
            int at = nodes.FindIndex(node => node.Id == root);
            if (at < 0)
            {
                yield return McpToolResult.Error($"{root} isn't on screen now, call hex_ui_tree without root and look again.");
                yield break;
            }

            int depth = nodes[at].Depth;
            int end = nodes.FindIndex(at + 1, node => node.Depth <= depth);
            nodes = nodes.GetRange(at, (end < 0 ? nodes.Count : end) - at);
        }

        if (arguments.String("find") is { Length: > 0 } find)
            nodes = [.. nodes.Where(node => node.Text.Contains(find, StringComparison.OrdinalIgnoreCase) || (node.Name?.Contains(find, StringComparison.OrdinalIgnoreCase) ?? false))];

        bool interactive = arguments.Flag("interactive") || arguments.String("find") is not null;
        string listed = McpDescribe(nodes, interactive, arguments.Integer("max_depth") ?? int.MaxValue, 600);
        string focus = compositor.Focus is { } focused ? $"{McpIdOf(focused, 'c')} {focused.GetType().Name}" : "nothing";
        yield return McpToolResult.Text($"Window {Engine.WindowManager.ViewportSize.X:0}x{Engine.WindowManager.ViewportSize.Y:0}, the keyboard is on {focus}. "
            + $"Each line is id, kind, #name, \"text\", [x,y widthxheight] in window pixels.\n{(listed.Length > 0 ? listed : "nothing matches\n")}");
    }

    /// <summary>
    /// Where the arguments point, in window pixels, with what it is in words. An id first, then text, then x and y. Null
    /// with the reason when they point at nothing.
    /// </summary>
    private Vector2? McpTarget(JsonObject arguments, out string described, out string? error, bool optional = false)
    {
        error = null;
        described = string.Empty;
        if (arguments.String("id") is { } id)
        {
            if (!mcpTargets.ContainsKey(id))
                McpScreen(layouts: true);
            if (mcpTargets.TryGetValue(id, out var target) && target() is { } point)
            {
                described = id;
                return point;
            }

            error = $"{id} isn't on screen now, call hex_ui_tree and look again.";
            return null;
        }

        if (arguments.String("text") is { } text)
        {
            if (McpFindText(text, out int others) is not { } found || found.Target() is not { } point)
            {
                error = $"Nothing on screen says \"{text}\". hex_ui_tree with find lists what's there.";
                return null;
            }

            described = $"{found.Id} {found.Kind} \"{found.Text}\"{(others > 0 ? $" (the best of {others + 1} matches)" : string.Empty)}";
            return point;
        }

        if (arguments.Number("x") is { } x && arguments.Number("y") is { } y)
        {
            described = $"{x:0},{y:0}";
            return new Vector2((float)x, (float)y);
        }

        if (!optional)
            error = "Say what to point at, an id, some text, or x and y.";
        return null;
    }

    private IEnumerable<McpToolResult?> Click(JsonObject arguments)
    {
        if (McpTarget(arguments, out string described, out string? error) is not { } window)
        {
            yield return McpToolResult.Error(error!);
            yield break;
        }

        bool right = arguments.String("button") == "right", twice = arguments.Flag("double");
        Vector2 at = McpWorld(window);
        agentPointer.Take(Engine.Input.Mouse.Position);

        // A move, a press, a let go, a tick each, the way a hand would, and the second of a double click straight after
        agentPointer.Set(at);
        yield return null;
        for (int press = 0; press < (twice ? 2 : 1); press++)
        {
            agentPointer.Set(at, down: !right, secondary: right);
            yield return null;
            agentPointer.Set(at);
            yield return null;
        }

        // What the click did is laid out a few ticks later, a list put together again, a dialog up
        for (int settle = 0; settle < 4; settle++)
            yield return null;

        string under = compositor.HoveredComponent is { } over ? $"{McpIdOf(over, 'c')} {over.GetType().Name}" : "nothing of Hex's";
        string focus = compositor.Focus is { } focused ? $"{McpIdOf(focused, 'c')} {focused.GetType().Name}" : "nothing";
        string dialog = chrome.Dialog is { IsOpen: true } up ? $" A dialog is up ({McpIdOf(up, 'c')}), it takes the pointer before anything under it." : string.Empty;
        yield return McpToolResult.Text($"{(twice ? "Double clicked" : right ? "Right clicked" : "Clicked")} {described} at {window.X:0},{window.Y:0}. "
            + $"The pointer is over {under}, the keyboard is on {focus}.{dialog}");
    }

    private IEnumerable<McpToolResult?> TypeText(JsonObject arguments)
    {
        string text = arguments.String("text") ?? string.Empty;
        if (compositor.Focus is not { } focused)
        {
            yield return McpToolResult.Error("Nothing has the keyboard, so typing would go nowhere. Click the code or a box first.");
            yield break;
        }

        // As much as the queue takes a tick, it's emptied by whatever has the keyboard every tick
        int at = 0;
        while (at < text.Length)
        {
            int room = UIKeyboard.Room;
            for (int i = 0; i < room && at < text.Length; i++, at++)
            {
                char typed = text[at];
                if (typed == '\r' && at + 1 < text.Length && text[at + 1] == '\n')
                    continue;
                UIKeyboard.Type(typed);
            }

            yield return null;
        }

        yield return null;
        yield return null;
        yield return McpToolResult.Text($"Typed {text.Length} characters into {McpIdOf(focused, 'c')} {focused.GetType().Name}.");
    }

    private IEnumerable<McpToolResult?> PressKeys(JsonObject arguments)
    {
        string keys = arguments.String("keys") ?? string.Empty;
        var strokes = new List<HexKeyStroke>();
        foreach (string written in keys.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (HexKeyStroke.Parse(written) is not { } stroke)
            {
                yield return McpToolResult.Error($"\"{written}\" isn't a key Hex knows. Write them like Ctrl+Shift+P, Enter, Escape, F5, Alt+Left, Ctrl+`.");
                yield break;
            }

            strokes.Add(stroke);
        }

        if (strokes.Count == 0)
        {
            yield return McpToolResult.Error("Say which keys.");
            yield break;
        }

        foreach (HexKeyStroke stroke in strokes)
        {
            // The program's shortcuts hear it on the next tick, then whatever has the keyboard gets what it types after
            // them, the order a real press goes in
            Engine.Input.Keyboard.Tap(stroke.Keys);
            yield return null;
            UIKeyboard.Press(stroke.Key, stroke.Control, stroke.Shift, stroke.Alt, stroke.Super);
            yield return null;
            yield return null;
        }

        for (int settle = 0; settle < 3; settle++)
            yield return null;

        string focus = compositor.Focus is { } focused ? $"{McpIdOf(focused, 'c')} {focused.GetType().Name}" : "nothing";
        string dialog = chrome.Dialog is { } up ? $" A dialog is up ({up.GetType().Name})." : string.Empty;
        yield return McpToolResult.Text($"Pressed {keys}. The keyboard is on {focus}.{dialog}");
    }

    private IEnumerable<McpToolResult?> Scroll(JsonObject arguments)
    {
        Vector2? window = McpTarget(arguments, out string described, out string? error, optional: true);
        if (error is not null)
        {
            yield return McpToolResult.Error(error);
            yield break;
        }

        agentPointer.Take(Engine.Input.Mouse.Position);
        if (window is { } point)
        {
            agentPointer.Set(McpWorld(point));
            yield return null;
        }

        float amount = (float)(arguments.Number("amount") ?? -3.0);
        compositor.Scroll(amount);
        for (int settle = 0; settle < 4; settle++)
            yield return null;

        yield return McpToolResult.Text($"Scrolled {amount:0.#} notches over {(described.Length > 0 ? described : "where the pointer was")}.");
    }

    private IEnumerable<McpToolResult?> Hover(JsonObject arguments)
    {
        if (McpTarget(arguments, out string described, out string? error) is not { } window)
        {
            yield return McpToolResult.Error(error!);
            yield break;
        }

        agentPointer.Take(Engine.Input.Mouse.Position);
        agentPointer.Set(McpWorld(window));
        long until = System.Environment.TickCount64 + (long)(Math.Clamp(arguments.Number("seconds") ?? 1.2, 0.1, 10.0) * 1000.0);
        while (System.Environment.TickCount64 < until)
            yield return null;

        string under = compositor.HoveredComponent is { } over ? $"{McpIdOf(over, 'c')} {over.GetType().Name} \"{McpTextOf(over)}\"" : "nothing of Hex's";
        string? tip = compositor.TooltipOf?.Tooltip ?? compositor.Modules.Select(module => module.TooltipText).FirstOrDefault(text => text is { Length: > 0 });
        yield return McpToolResult.Text($"Resting on {described}, over {under}. {(tip is { Length: > 0 } ? $"Its tooltip says \"{Plain(tip)}\"." : "No tooltip came up.")}");
    }
}

/// <summary>A key with what's held with it, read the way a menu writes it (Ctrl+Shift+P).</summary>
internal sealed record HexKeyStroke(Key Key, bool Control, bool Shift, bool Alt, bool Super)
{
    /// <summary>Every key of it, the ones held first, for the engine's keyboard.</summary>
    public Key[] Keys => [.. new[] { (Control, Key.ControlLeft), (Shift, Key.ShiftLeft), (Alt, Key.AltLeft), (Super, Key.SuperLeft) }.Where(held => held.Item1).Select(held => held.Item2), Key];

    public static HexKeyStroke? Parse(string written)
    {
        string[] parts = written.Length > 1 && written.EndsWith('+') ? [.. written[..^1].Split('+', StringSplitOptions.RemoveEmptyEntries), "+"] : written.Split('+', StringSplitOptions.RemoveEmptyEntries);
        bool control = false, shift = false, alt = false, super = false;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].Trim().ToLowerInvariant())
            {
                case "ctrl" or "control": control = true; break;
                case "shift": shift = true; break;
                case "alt" or "option": alt = true; break;
                case "cmd" or "command" or "super" or "win" or "meta": super = true; break;
                default: return null;
            }
        }

        return parts.Length > 0 && KeyOf(parts[^1].Trim()) is { } key ? new HexKeyStroke(key, control, shift, alt, super) : null;
    }

    private static Key? KeyOf(string name)
    {
        if (name.Length == 1)
        {
            char c = char.ToUpperInvariant(name[0]);
            if (c is >= 'A' and <= 'Z')
                return Key.A + (c - 'A');
            if (c is >= '0' and <= '9')
                return Key.Number0 + (c - '0');

            return c switch
            {
                '`' => Key.GraveAccent, '-' => Key.Minus, '=' or '+' => Key.Equal, '[' => Key.LeftBracket, ']' => Key.RightBracket,
                ';' => Key.Semicolon, '\'' => Key.Apostrophe, ',' => Key.Comma, '.' => Key.Period, '/' => Key.Slash, '\\' => Key.BackSlash,
                ' ' => Key.Space,
                _ => null
            };
        }

        return name.ToLowerInvariant() switch
        {
            "esc" => Key.Escape,
            "return" => Key.Enter,
            "del" => Key.Delete,
            "ins" => Key.Insert,
            "pgup" => Key.PageUp,
            "pgdn" or "pgdown" => Key.PageDown,
            "backtick" => Key.GraveAccent,
            "plus" => Key.Equal,

            // One held alone, Alt tapped by itself lights the menu bar
            "alt" or "option" => Key.AltLeft,
            "shift" => Key.ShiftLeft,
            "ctrl" or "control" => Key.ControlLeft,
            _ => Enum.TryParse(name, ignoreCase: true, out Key key) && key != Key.Unknown ? key : null
        };
    }
}
