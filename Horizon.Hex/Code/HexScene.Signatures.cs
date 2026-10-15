using System.Text;
using System.Text.RegularExpressions;

using Horizon.UI.Components;

namespace Horizon.Hex;

// Signature help, how the function being called at the caret is called, said over the call as it's typed with the
// argument the caret is on in bold (CodeEditor.SignatureSource). The language's own functions from a table here, the
// file's own from their func.
internal sealed partial class HexScene
{
    private static readonly Dictionary<string, string[]> LibraryCalls = new(StringComparer.Ordinal)
    {
        ["print"] = ["value", "..."],
        ["include"] = ["file"],
        ["keys"] = ["object"],
        ["values"] = ["object"],
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
    private static UISignature? SignatureOf(CodeEditor editor, string name, int argument)
    {
        string[]? parameters = LibraryCalls.GetValueOrDefault(name) ?? FileFunction(editor.Text, name);
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

    // The parameters of a func declared in the code, null for a name that isn't one
    private static string[]? FileFunction(string code, string name)
    {
        if (name.Contains('.'))
            return null;

        Match declared = Regex.Match(code, $@"\bfunc\s+{Regex.Escape(name)}\s*\(([^)]*)\)");
        if (!declared.Success)
            return null;

        string inside = declared.Groups[1].Value.Trim();
        return inside.Length == 0 ? [] : [.. inside.Split(',').Select(parameter => parameter.Trim())];
    }
}
