using System.Text;

namespace Hex.Core;

internal sealed record PythonRunLine(string File, string Folder, string Interpreter, IReadOnlyList<string> Arguments, string? Module = null, IReadOnlyDictionary<string, string>? Environment = null)
{
    // Only a literal script invocation can be transferred to DAP without changing shell semantics.
    public static PythonRunLine? Of(string command, string folder)
    {
        command = command.Trim();
        if (command.StartsWith("& ", StringComparison.Ordinal)) command = command[2..].TrimStart();
        var words = new List<string>();
        var word = new StringBuilder();
        char quote = '\0';
        bool started = false;
        for (int i = 0; i < command.Length; i++)
        {
            char c = command[i];
            if (c is '\n' or '\r') return null;
            if (quote == '"' && c == '\\' && i + 1 < command.Length && command[i + 1] == '"') return null;
            if (quote != '\0')
            {
                if (c == quote)
                {
                    if (quote == '\'' && i + 1 < command.Length && command[i + 1] == '\'') { word.Append(c); i++; }
                    else quote = '\0';
                }
                else
                {
                    if (quote == '"' && c is '$' or '`' or '%' or '!') return null;
                    word.Append(c);
                }
            }
            else if (c is '\'' or '"') { quote = c; started = true; }
            else if (char.IsWhiteSpace(c))
            {
                if (started) { words.Add(word.ToString()); word.Clear(); started = false; }
            }
            else
            {
                if (c is '&' or '|' or ';' or '<' or '>' or '$' or '`' or '%' or '!' or '(' or ')' or '\n' or '\r') return null;
                word.Append(c); started = true;
            }
        }
        if (quote != '\0') return null;
        if (started) words.Add(word.ToString());
        if (words.Count < 2) return null;
        string name = Path.GetFileNameWithoutExtension(words[0]);
        if (!name.Equals("python", StringComparison.OrdinalIgnoreCase) && !name.Equals("python3", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            string interpreter = words[0].IndexOfAny(['/', '\\']) >= 0 ? Path.GetFullPath(Path.Combine(folder, words[0])) : words[0];
            if (words[1] == "-m")
            {
                if (words.Count < 3 || !words[2].Split('.').All(part => part.Length > 0 && (char.IsLetter(part[0]) || part[0] == '_') && part.All(c => char.IsLetterOrDigit(c) || c == '_'))) return null;
                return new(Path.GetFullPath(folder), folder, interpreter, words.Skip(3).ToArray(), words[2]);
            }
            if (!words[1].EndsWith(".py", StringComparison.OrdinalIgnoreCase)) return null;
            string file = Path.GetFullPath(Path.Combine(folder, words[1]));
            return System.IO.File.Exists(file) ? new(file, folder, interpreter, words.Skip(2).ToArray()) : null;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException) { return null; }
    }
}
