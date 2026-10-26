using Horizon.HIDL;
using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;

namespace Hex.Tests;

/// <summary>
/// A hex object for an extension of the catalog to run against, with a file of lines on screen, a caret and a selection
/// that what it's asked to do really changes (hex.replace_text, hex.select), the way Dawn would. What it said, the menus
/// it put up and the problems it found are written down, a prompt is answered with <see cref="Answer"/> straight away.
/// Lines and columns from 1, the way an extension has them.
/// </summary>
internal sealed class FakeHex
{
    public List<string> Lines = [string.Empty];
    public (int Line, int Column) Caret = (1, 1), Anchor = (1, 1);
    public string File = "C:/work/file.txt";
    public string Answer = string.Empty;
    public readonly Dictionary<string, IRuntimeValue> Settings = [];
    public readonly Dictionary<string, (IRuntimeValue Function, string Key)> Commands = [];
    public readonly List<string> Said = [];
    public readonly List<string> Problems = [];
    public ListValue? Menu;
    public ObjectValue? OwnSnippets;
    public HIDLRuntime Runtime { get; private set; } = null!;
    public ObjectValue Object { get; }

    public string Text
    {
        get => string.Join('\n', Lines);
        set
        {
            Lines = [.. value.Split('\n')];
            Caret = Anchor = (1, 1);
        }
    }

    /// <param name="old">A hex from before hex.selection, hex.select and hex.lines.</param>
    public FakeHex(bool old = false)
    {
        var members = new List<(string, IRuntimeValue)>
        {
            ("caret", Natives.Function("caret", _ => Natives.Object(("line", Number(Caret.Line)), ("column", Number(Caret.Column)),
                ("text", new StringValue(Lines[Caret.Line - 1])), ("selected", new StringValue(Selected()))))),
            ("replace_text", Natives.Function("replace_text", args =>
            {
                var from = PlaceOf(args.Object(0));
                var to = PlaceOf(args.Object(1));
                if (Compare(to, from) < 0)
                    (from, to) = (to, from);
                string written = args.Text(2);
                int caret = args.Has(3) && args[3] is NumberValue at ? (int)at.Value : written.Length;
                Replace(from, to, written, caret);
                return Values.Null;
            })),
            ("setting", Natives.Function("setting", args => Settings.TryGetValue(args.Text(0), out IRuntimeValue? told) ? told : Values.Null)),
            ("command", Natives.Action("command", args => Commands[args.Text(0)] = (args.Callable(1), args.Text(2, string.Empty)))),
            ("say", Natives.Action("say", args => Said.Add(args.Text(0)))),
            ("menu", Natives.Action("menu", args => Menu = (ListValue)args[0])),
            ("prompt", Natives.Action("prompt", args => Runtime.Invoke(args.Callable(1), Object!, new StringValue(Answer)))),
            ("file", Natives.Function("file", _ => new StringValue(File))),
            ("read", Natives.Function("read", _ => new StringValue(Text))),
            ("exists", Natives.Function("exists", _ => Values.False)),
            ("write", Natives.Action("write", _ => { })),
            ("open", Natives.Action("open", _ => { })),
            ("try_load", Natives.Function("try_load", _ => Natives.Object(("values", OwnSnippets is { } own ? Natives.Object(("snippets", own)) : Values.Null), ("error", Values.Null)))),
            ("problems", Natives.Function("problems", args =>
            {
                Problems.Clear();
                foreach (IRuntimeValue problem in ((ListValue)args[1]).Items)
                    Problems.Add(Values.Text(((ObjectValue)problem).Properties["line"]) + ": " + Values.Text(((ObjectValue)problem).Properties["message"]));
                return Values.Null;
            }))
        };

        if (!old)
        {
            members.Add(("selection", Natives.Function("selection", _ =>
            {
                var (from, to) = Compare(Caret, Anchor) <= 0 ? (Caret, Anchor) : (Anchor, Caret);
                return Natives.Object(("from", Place(from)), ("to", Place(to)), ("caret", Place(Caret)), ("text", new StringValue(Selected())));
            })));
            members.Add(("select", Natives.Function("select", args =>
            {
                var from = PlaceOf(args.Object(0));
                Anchor = from;
                Caret = args.Has(1) && args[1] is ObjectValue end ? PlaceOf(end) : from;
                return Values.Null;
            })));
            members.Add(("lines", Natives.Function("lines", args =>
            {
                int first = Math.Clamp(args.Has(0) ? args.Integer(0) : 1, 1, Lines.Count);
                int last = Math.Clamp(args.Has(1) ? args.Integer(1) : Lines.Count, first, Lines.Count);
                return Natives.List(Lines.GetRange(first - 1, last - first + 1));
            })));
        }

        Object = Natives.Object([.. members]);
    }

    /// <summary>An extension of the catalog loaded against this hex and started.</summary>
    public static FakeHex Load(string id, bool old = false, Action<FakeHex>? before = null)
    {
        var hex = new FakeHex(old);
        before?.Invoke(hex);
        string folder = Path.Combine(Catalog.Folder, id);
        hex.Runtime = new HIDLRuntime { BaseDirectory = folder, Output = null };
        hex.Runtime.UserScope.DeclareSystem("hex", hex.Object);
        hex.Runtime.RunFile(Path.Combine(folder, "extension.hor"));
        if (hex.Runtime.UserScope.Variables.TryGetValue("on_start", out IRuntimeValue? start))
            hex.Runtime.Invoke(start, hex.Object);
        return hex;
    }

    /// <summary>One of the extension's commands run, by its name.</summary>
    public void Run(string command) => Runtime.Invoke(Commands[command].Function, Object);

    /// <summary>One of the extension's functions called, by its name.</summary>
    public IRuntimeValue Call(string function, params IRuntimeValue[] arguments) => Runtime.Invoke(Runtime.UserScope.Variables[function], arguments);

    /// <summary>An item of the menu it put up pressed, by the start of its label.</summary>
    public void Press(string label)
    {
        foreach (ObjectValue item in Menu!.Items.OfType<ObjectValue>())
        {
            if (Values.Text(item.Properties["label"]).StartsWith(label, StringComparison.Ordinal))
            {
                Runtime.Invoke(item.Properties["on_pressed"], Object);
                return;
            }
        }

        throw new InvalidOperationException($"nothing in the menu starts with {label}");
    }

    /// <summary>What's selected replaced by what somebody typed, the caret after it.</summary>
    public void Type(string typed)
    {
        var (from, to) = Compare(Caret, Anchor) <= 0 ? (Caret, Anchor) : (Anchor, Caret);
        Replace(from, to, typed, typed.Length);
    }

    public string Selected()
    {
        var (from, to) = Compare(Caret, Anchor) <= 0 ? (Caret, Anchor) : (Anchor, Caret);
        int start = OffsetOf(from), end = OffsetOf(to);
        return Text[start..end];
    }

    private void Replace((int Line, int Column) from, (int Line, int Column) to, string written, int caret)
    {
        string text = Text;
        int start = OffsetOf(from), end = OffsetOf(to);
        Lines = [.. (text[..start] + written + text[end..]).Split('\n')];
        Caret = Anchor = PlaceAt(start + Math.Clamp(caret, 0, written.Length));
    }

    private int OffsetOf((int Line, int Column) at)
    {
        int offset = 0;
        for (int line = 0; line < at.Line - 1; line++)
            offset += Lines[line].Length + 1;
        return offset + Math.Min(at.Column - 1, Lines[at.Line - 1].Length);
    }

    private (int, int) PlaceAt(int offset)
    {
        for (int line = 0; line < Lines.Count; line++)
        {
            if (offset <= Lines[line].Length)
                return (line + 1, offset + 1);
            offset -= Lines[line].Length + 1;
        }

        return (Lines.Count, Lines[^1].Length + 1);
    }

    private (int, int) PlaceOf(ObjectValue at)
    {
        int line = Math.Clamp((int)((NumberValue)at.Properties["line"]).Value, 1, Lines.Count);
        int column = Math.Clamp((int)((NumberValue)at.Properties["column"]).Value, 1, Lines[line - 1].Length + 1);
        return (line, column);
    }

    private static int Compare((int Line, int Column) a, (int Line, int Column) b) => a.Line != b.Line ? a.Line.CompareTo(b.Line) : a.Column.CompareTo(b.Column);

    private static NumberValue Number(int value) => new(value);

    private static ObjectValue Place((int Line, int Column) at) => Natives.Object(("line", Number(at.Line)), ("column", Number(at.Column)));
}
