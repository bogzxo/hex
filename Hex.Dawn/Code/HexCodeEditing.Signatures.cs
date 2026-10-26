using System.Text;

using Horizon.UI.Components;

namespace Hex.Dawn;

// Signature help, how the function being called at the caret is called, said over the call as it's typed with the
// argument the caret is on in bold (CodeEditor.SignatureSource). The language's own functions from a table here, the
// file's own from their func.
internal sealed partial class HexCodeEditing
{
    private static readonly Dictionary<string, string[]> LibraryCalls = new(StringComparer.Ordinal)
    {
        ["print"] = ["value", "..."],
        ["include"] = ["file"],
        ["keys"] = ["object"],
        ["values"] = ["object"],
        ["entries"] = ["object"],
        ["has"] = ["object or list", "key or item"],
        ["range"] = ["from", "to"],
        ["len"] = ["text, list or object"],
        ["text"] = ["value"],
        ["number"] = ["text"],
        ["format"] = ["template", "..."],
        ["typeof"] = ["value"],
        ["error"] = ["why"],
        ["defined"] = ["name"],
        ["definition"] = ["name"],
        ["vec"] = ["x", "y", "z", "w"],
        ["merge"] = ["a", "b"],
        ["math.floor"] = ["x"],
        ["math.ceil"] = ["x"],
        ["math.round"] = ["x", "digits"],
        ["math.abs"] = ["x"],
        ["math.min"] = ["a", "b"],
        ["math.max"] = ["a", "b"],
        ["math.clamp"] = ["x", "low", "high"],
        ["math.sqrt"] = ["x"],
        ["math.sin"] = ["angle"],
        ["math.cos"] = ["angle"],
        ["math.pick"] = ["list"]
    };

    /// <summary>A call's signature, the language's or one of the file's own funcs, the argument the caret is on picked out.</summary>
    internal static UISignature? SignatureOf(CodeEditor editor, string name, int argument)
    {
        string[]? parameters = LibraryCalls.GetValueOrDefault(name) ?? FileFunction(editor, name);
        if (parameters is null)
            return null;

        // One more argument than there are named, the last one when it takes any number ("...")
        if (argument >= parameters.Length && parameters is [.., "..."])
            argument = parameters.Length - 1;

        var text = new StringBuilder(name).Append('(');
        int start = 0, length = 0;
        for (int i = 0; i < parameters.Length; i++)
        {
            if (i > 0) text.Append(", ");
            if (i == argument)
            {
                start = text.Length;
                length = parameters[i].Length;
            }
            text.Append(parameters[i]);
        }

        return new UISignature(text.Append(')').ToString(), start, length);
    }

    // The parameters of a func declared in the code, or of a func or an arrow a let holds (let add = (a, b) => ...,
    // let twice = x => ...), defaults as they're written, null for a name that isn't one. A name with a dot in it is a
    // library's and one the file never says isn't the file's, both told without the whole text put together, which was a
    // megabyte or two of garbage for every key typed inside a call in a big file
    private static string[]? FileFunction(CodeEditor editor, string name) =>
        name.Contains('.') || !Mentions(editor.Document, name) ? null : HidlFunctions.Signature(editor.Text, name);

    // Whether any line of a text has a name in it
    private static bool Mentions(UITextDocument document, string name)
    {
        for (int i = 0; i < document.LineCount; i++)
        {
            if (document.LineAt(i).Contains(name, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
