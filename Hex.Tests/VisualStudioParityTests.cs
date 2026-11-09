using System.Text.Json.Nodes;

using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Scripting;

using Key = Horizon.Input.Key;

namespace Hex.Tests;

/// <summary>
/// The pieces of Visual Studio's ways that don't need a window. Its editing keys where they differ from VS Code's, a box
/// selection grown from the keyboard, the overloads a signatureHelp answer says, and the two ends of the calls a call
/// hierarchy answers with.
/// </summary>
public sealed class VisualStudioParityTests
{
    private static char Code(Key key, bool primary = false, bool shift = false, bool alt = false, bool visualStudio = false) =>
        UIKeyboard.Meaning(key, primary, shift, alt, mac: false, visualStudio).Code;

    [Fact]
    public void VisualStudioKeysMoveOnlyWhatDiffers()
    {
        Assert.Equal(UIKeyboard.DUPLICATE, Code(Key.D, primary: true, visualStudio: true));
        Assert.Equal(UIKeyboard.NEXT_OCCURRENCE, Code(Key.D, primary: true));
        Assert.Equal(UIKeyboard.BOX_DOWN, Code(Key.Down, shift: true, alt: true, visualStudio: true));
        Assert.Equal(UIKeyboard.BOX_LEFT, Code(Key.Left, shift: true, alt: true, visualStudio: true));
        Assert.Equal(UIKeyboard.COPY_LINES_DOWN, Code(Key.Down, shift: true, alt: true));
        Assert.Equal(UIKeyboard.NEXT_OCCURRENCE, Code(Key.Period, shift: true, alt: true, visualStudio: true));
        Assert.Equal(UIKeyboard.SELECT_ALL_OCCURRENCES, Code(Key.Semicolon, shift: true, alt: true, visualStudio: true));

        // The rest are the same either way
        Assert.Equal(UIKeyboard.MOVE_LINES_DOWN, Code(Key.Down, alt: true, visualStudio: true));
        Assert.Equal(UIKeyboard.CARET_BELOW, Code(Key.Down, primary: true, alt: true, visualStudio: true));
        Assert.Equal(UIKeyboard.UNDO, Code(Key.Z, primary: true, visualStudio: true));
        Assert.Equal(UIKeyboard.PARAMETER_INFO, Code(Key.Space, primary: true, shift: true));
        Assert.Equal(UIKeyboard.COMPLETE, Code(Key.Space, primary: true));
    }

    [Fact]
    public void BoxGrowsDownAndAcrossKeepingItsColumn()
    {
        var document = new UITextDocument();
        document.Replace("abcdef\nab\nabcdef\nabcdef");
        document.MoveTo(new UITextPosition(0, 1));

        document.ExtendBox(1, 0);
        document.ExtendBox(1, 0);
        document.ExtendBox(0, 1);
        document.ExtendBox(0, 1);

        // Three lines, a caret on each, columns 1 to 3 where the line's long enough and 1 to 2 on the short one
        Assert.Equal(3, document.CaretCount);
        Assert.Equal("bc\nb\nbc", document.SelectedTextOfEvery());

        // Back up a line shrinks it, the column it had is kept
        document.ExtendBox(-1, 0);
        Assert.Equal(2, document.CaretCount);
        Assert.Equal("bc\nb", document.SelectedTextOfEvery());

        // Anything else putting the caret somewhere starts a new one from there
        document.MoveTo(new UITextPosition(3, 0));
        document.ExtendBox(-1, 0);
        Assert.Equal(2, document.CaretCount);
        Assert.Equal(new UITextPosition(2, 0), document.Caret);
        Assert.Equal(new UITextPosition(3, 0), document.ExtraCarets[0].Caret);
    }

    [Fact]
    public void SignatureHelpSaysEveryOverload()
    {
        var answer = JsonNode.Parse("""
            {
              "activeSignature": 1,
              "activeParameter": 1,
              "signatures": [
                { "label": "void Write(string text)", "parameters": [ { "label": "string text" } ] },
                { "label": "void Write(string format, object arg)", "parameters": [ { "label": [11, 24] }, { "label": [26, 36] } ] },
                { "label": "void Write(int number)", "parameters": [ { "label": "int number" } ] }
              ]
            }
            """);

        UISignature said = LanguageServer.SignatureOf(answer)!.Value;
        Assert.Equal("void Write(string format, object arg)", said.Text);
        Assert.Equal("object arg", said.Text.Substring(said.Start, said.Length));
        Assert.Equal(1, said.Active);
        Assert.Equal(3, said.Overloads!.Count);

        // Each overload with the argument picked out in its own label, none for one that hasn't that many
        Assert.Equal((0, 0), (said.Overloads[0].Start, said.Overloads[0].Length));
        Assert.Equal("void Write(int number)", said.Overloads[2].Text);

        // One signature has no overloads to go through
        UISignature one = LanguageServer.SignatureOf(JsonNode.Parse("""{ "signatures": [ { "label": "f(x)" } ] }"""))!.Value;
        Assert.Null(one.Overloads);
        Assert.Null(LanguageServer.SignatureOf(JsonNode.Parse("""{ "signatures": [] }""")));
    }

    [Fact]
    public void CallsSayTheirOtherEndAndWhereTheCallsAre()
    {
        const string ITEM = """
            { "name": "{0}", "detail": "Demo.Loop", "kind": 6, "uri": "{1}",
              "range": { "start": { "line": 3, "character": 4 }, "end": { "line": 9, "character": 5 } },
              "selectionRange": { "start": { "line": 3, "character": 16 }, "end": { "line": 3, "character": 24 } } }
            """;
        string Item(string name, string file) => ITEM.Replace("{0}", name).Replace("{1}", new Uri(Path.Combine(Path.GetTempPath(), "hex-call-hierarchy", file)).AbsoluteUri);

        JsonNode? incoming = JsonNode.Parse($$"""
            [ { "from": {{Item("Simulate", "Loop.cs")}},
                "fromRanges": [ { "start": { "line": 12, "character": 8 }, "end": { "line": 12, "character": 16 } },
                                { "start": { "line": 20, "character": 8 }, "end": { "line": 20, "character": 16 } } ] } ]
            """);
        var callers = LanguageServer.CallsOf(incoming, "from", null);
        LanguageServerCall caller = Assert.Single(callers);
        Assert.Equal("Simulate", caller.Other.Name);
        Assert.Equal("Demo.Loop", caller.Other.Detail);
        Assert.Equal(3, caller.Other.Declared.Line);
        Assert.Equal(16, caller.Other.Declared.Character);
        Assert.Equal([12, 20], caller.Sites.Select(site => site.Line));
        Assert.All(caller.Sites, site => Assert.Equal("Loop.cs", Path.GetFileName(site.Path)));

        // What's called has its calls in the file of the one calling it
        JsonNode? outgoing = JsonNode.Parse($$"""
            [ { "to": {{Item("WaitUntil", "Clock.cs")}},
                "fromRanges": [ { "start": { "line": 5, "character": 8 }, "end": { "line": 5, "character": 17 } } ] } ]
            """);
        LanguageServerCall called = Assert.Single(LanguageServer.CallsOf(outgoing, "to", Path.Combine(Path.GetTempPath(), "hex-call-hierarchy", "Loop.cs")));
        Assert.Equal("Clock.cs", Path.GetFileName(called.Other.Declared.Path));
        Assert.Equal("Loop.cs", Path.GetFileName(Assert.Single(called.Sites).Path));
        Assert.Empty(LanguageServer.CallsOf(JsonNode.Parse("null"), "from", null));
    }
}
