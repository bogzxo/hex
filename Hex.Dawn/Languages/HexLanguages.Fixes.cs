using System.Text.RegularExpressions;

using Horizon.UI.Scripting;

namespace Hex.Dawn;

// The fixes a compiler's own words make plain, offered with a server's on Ctrl+. dmd says "perhaps `import std.math;` is
// needed?" and "did you mean function `total`?" and code-d does nothing with either, so the import goes in under the
// others and the name is swapped for the one it meant. Read off what's wrong on the lines asked about, any language
// whose compiler says it that way.
internal sealed partial class HexLanguages
{
    private static readonly Regex NeedsImport = new(@"perhaps `(?<import>import [\w.]+;)` is needed", RegexOptions.Compiled);
    private static readonly Regex MeantName = new(@"did you mean (?:\w+ )?`(?<name>[\w.]+)`", RegexOptions.Compiled);
    private static readonly Regex ImportLine = new(@"^\s*(?:public\s+|static\s+)*import\s", RegexOptions.Compiled);
    private static readonly Regex ModuleLine = new(@"^\s*module\s", RegexOptions.Compiled);

    /// <summary>The fixes what's wrong on some lines of a document says itself, the ones it would pick first.</summary>
    private List<LanguageServerAction> CompilerFixes(HexDocument of, int fromLine, int toLine)
    {
        var fixes = new List<LanguageServerAction>();
        if (of.Path is not { } path)
            return fixes;

        UITextDocument text = of.Editor.Document;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (UICodeDiagnostic wrong in of.Editor.Diagnostics)
        {
            if (wrong.Line < fromLine || wrong.Line > toLine)
                continue;

            if (NeedsImport.Match(wrong.Message) is { Success: true } import && seen.Add(import.Groups["import"].Value))
            {
                string line = import.Groups["import"].Value;
                int under = ImportGoesAt(text);
                fixes.Add(Fix(context.Strings.Said("code_server.add_import", ("import", line)), path, new LanguageServerEdit(under, 0, under, 0, line + "\n")));
            }
            else if (MeantName.Match(wrong.Message) is { Success: true } meant && wrong.Line < text.LineCount)
            {
                string name = meant.Groups["name"].Value;
                string said = text.LineAt(wrong.Line);
                int end = wrong.Column;
                while (end < said.Length && (char.IsLetterOrDigit(said[end]) || said[end] == '_'))
                    end++;
                if (end > wrong.Column && seen.Add(wrong.Line + ":" + name))
                    fixes.Add(Fix(context.Strings.Said("code_server.change_name", ("from", said[wrong.Column..end]), ("to", name)), path, new LanguageServerEdit(wrong.Line, wrong.Column, wrong.Line, end, name)));
            }
        }

        return fixes;
    }

    // Helper method for a fix of one edit in one file
    private static LanguageServerAction Fix(string title, string path, LanguageServerEdit edit) =>
        new(title, true, new Dictionary<string, IReadOnlyList<LanguageServerEdit>> { [path] = [edit] }, null);

    /// <summary>The line an import goes in at, under the last import at the top, or under the module's name, or the top.</summary>
    private static int ImportGoesAt(UITextDocument text)
    {
        int after = -1;
        for (int line = 0; line < text.LineCount; line++)
        {
            string said = text.LineAt(line);
            if (ImportLine.IsMatch(said) || (after < 0 && ModuleLine.IsMatch(said)))
                after = line;
            else if (said.Contains('{'))
                break;
        }

        return after + 1;
    }
}
