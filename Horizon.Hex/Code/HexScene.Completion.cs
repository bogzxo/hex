using System.Numerics;
using System.Text;

using Horizon.Content;
using Horizon.HIDL;
using Horizon.HIDL.Lexing;
using Horizon.HIDL.Parsing;
using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Horizon.Hex;

// Completing what is typed by what it is being typed into, and going from a name to where it is declared (F12) and to
// everywhere it is used (Shift+F12).
internal sealed partial class HexScene
{
    /* Completing what is typed, by what it is being typed into */

    // Every name and key of every file of the workspace, made the first time anything is completed and again once
    // something changes the files (null until then)
    private HashSet<string>? vocabulary;

    private HashSet<string> Vocabulary()
    {
        if (vocabulary is not null)
            return vocabulary;

        vocabulary = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in AllWorkspaceFiles().Take(2000))
        {
            string source;
            try
            {
                source = File.ReadAllText(file);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (Token token in Lexer.Tokenize(source, lenient: true))
            {
                if (token.Type == TokenType.Identifier && token.Value.Length > 1)
                    vocabulary.Add(token.Value);
            }
        }

        return vocabulary;
    }

    /// <summary>
    /// What there is to write at the caret of a document. A key of the game's language inside a text that starts with an
    /// @, one of the words a property takes inside its text (an anchor, a fill), a kind of component after compositor.,
    /// the properties of the component being made inside its braces, and otherwise the language's words with every name
    /// of the workspace.
    /// </summary>
    private UICompletionRequest? Completions(CodeEditor editor)
    {
        UITextDocument doc = editor.Document;
        UITextPosition caret = doc.Caret;
        string line = doc.LineAt(caret.Line);
        string before = line[..caret.Column];

        // Inside a text, the quotes before the caret on its line odd in number
        int quote = before.LastIndexOf('"');
        if (quote >= 0 && before.Count(character => character == '"') % 2 == 1)
        {
            string inside = before[(quote + 1)..];

            if (inside.StartsWith('@'))
            {
                var keys = Localisation.Keys.Order(StringComparer.Ordinal)
                    .Select(key => new UICompletion(key, Shorten(Localisation.Get(key), 40)))
                    .ToList();
                return new UICompletionRequest(quote + 2, keys);
            }

            // The words of a property that is one of a few, after its name and a colon
            string head = before[..quote].TrimEnd();
            if (head.EndsWith(':'))
            {
                string property = TrailingWord(head[..^1].TrimEnd());
                if (Choices.TryGetValue(property, out string[]? words))
                    return new UICompletionRequest(quote + 1, [.. words.Select(word => new UICompletion(word, property))]);
            }

            return null;
        }

        string word = doc.WordBeforeCaret();
        int start = caret.Column - word.Length;
        string ahead = before[..start];

        // A kind of component, after compositor.
        if (ahead.EndsWith("compositor.", StringComparison.Ordinal))
            return new UICompletionRequest(start, [.. UIModule.Kinds.Order(StringComparer.Ordinal).Select(kind => new UICompletion(kind, Shorten(Describe(kind), 40), kind + "({ "))]);

        // A property of the component being made, where a key goes inside its braces
        if (ComponentBeingMade(editor.Text, doc, start) is { } kind && UIModule.CreateDetached(kind) is { } made)
        {
            string trimmed = ahead.TrimEnd();
            if (trimmed.Length == 0 || trimmed.EndsWith('{') || trimmed.EndsWith(','))
            {
                var properties = made.Object.Properties.Keys
                    .Where(name => name != "parent")
                    .Prepend("parent")
                    .Select(name => new UICompletion(name, Shorten(Horizon.UI.Scripting.UIPropertyHelp.Describe(kind, name), 48), name + ": "))
                    .ToList();
                return new UICompletionRequest(start, properties);
            }
        }

        var items = new List<UICompletion>();
        var seen = new HashSet<string>();
        foreach (string keyword in HidlKeywords)
        {
            if (seen.Add(keyword)) items.Add(new UICompletion(keyword, "keyword"));
        }

        foreach (Token token in Lexer.Tokenize(editor.Text, lenient: true))
        {
            if (token.Type == TokenType.Identifier && token.Value.Length > 1 && seen.Add(token.Value))
                items.Add(new UICompletion(token.Value, "in this file"));
        }

        foreach (string known in Vocabulary().Order(StringComparer.Ordinal))
        {
            if (seen.Add(known))
                items.Add(new UICompletion(known, "in the workspace"));
        }

        return new UICompletionRequest(start, items);
    }

    private static readonly string[] HidlKeywords = ["let", "const", "func", "if", "else", "while", "for", "in", "return", "break", "continue", "true", "false", "null", "vec", "include", "compositor"];

    private static string TrailingWord(string text)
    {
        int start = text.Length;
        while (start > 0 && UITextDocument.IsWordChar(text[start - 1])) start--;
        return text[start..];
    }

    /// <summary>
    /// Helper method to find which kind of component the code is in the middle of making at a place, the innermost
    /// compositor.kind({ that isn't closed yet, null for none.
    /// </summary>
    private static string? ComponentBeingMade(string text, UITextDocument doc, int column)
    {
        // Everything up to the place, by its offset in the text
        int offset = 0;
        for (int line = 0; line < doc.Caret.Line; line++)
            offset += doc.LineAt(line).Length + 1;
        offset += column;

        Token[] tokens = Lexer.Tokenize(text[..Math.Min(offset, text.Length)], lenient: true);
        var open = new Stack<string?>();

        // By the character and not the kind of token, the lexer's names for the brackets are the other way round
        for (int i = 0; i < tokens.Length; i++)
        {
            switch (tokens[i].Value)
            {
                case "{":
                    // compositor . kind ( {
                    bool made = i >= 4 && tokens[i - 1].Type == TokenType.OpenParenthesis && tokens[i - 2].Type == TokenType.Identifier
                        && tokens[i - 3].Type == TokenType.Dot && tokens[i - 4] is { Type: TokenType.Identifier, Value: "compositor" };
                    open.Push(made ? tokens[i - 2].Value : null);
                    break;

                case "}" when open.Count > 0:
                    open.Pop();
                    break;
            }
        }

        return open.Count > 0 ? open.Peek() : null;
    }

    /* Going to where something is */

    /// <summary>
    /// F12. Where what is at the caret is declared. A name goes to its let or const, in this file first and then in the
    /// workspace, a text naming a key of the game's language to the key in the language's file, a text naming a file
    /// (an include, a template) to the file.
    /// </summary>
    private void GoToDefinition()
    {
        UITextDocument doc = code.Document;
        UITextPosition caret = doc.Caret;
        string line = doc.LineAt(caret.Line);

        // A text the caret is in
        foreach (Token token in Lexer.Tokenize(line, lenient: true))
        {
            if (token.Type != TokenType.TextLiteral || caret.Column < token.Col - 1 || caret.Column > token.Col - 1 + token.Length)
                continue;

            if (Localisation.IsKey(token.Value))
            {
                GoToKey(token.Value[1..]);
                return;
            }

            string folder = document.Path is { } path ? Path.GetDirectoryName(Path.GetFullPath(path))! : browseDirectory;
            string named = Path.Combine(folder, token.Value);
            if (token.Value.Length > 0 && File.Exists(named))
            {
                OpenAt(named, 0, 0);
                return;
            }
        }

        var (start, end) = doc.WordAt(caret);
        if (end <= start)
        {
            Say("there is nothing at the caret to go to");
            return;
        }

        string word = line[start..end];

        // In this file
        if (FindDeclaration(code.Text, word) is { } here)
        {
            code.GoTo(here.Line, here.Column);
            Say($"{word} is declared on line {here.Line + 1}");
            return;
        }

        // In the workspace
        foreach (string file in AllWorkspaceFiles())
        {
            string source;
            try
            {
                source = File.ReadAllText(file);
            }
            catch (Exception)
            {
                continue;
            }

            if (!source.Contains(word, StringComparison.Ordinal) || FindDeclaration(source, word) is not { } there)
                continue;

            OpenAt(file, there.Line, there.Column);
            Say($"{word} is declared in {Relative(file)}, line {there.Line + 1}");
            return;
        }

        Say($"{word} isn't declared with a let anywhere in {DescribeRoots()}", error: true);
    }

    /// <summary>Where a name is declared with let or const in some code, by its tokens, null for nowhere.</summary>
    private static UITextPosition? FindDeclaration(string source, string name)
    {
        Token[] tokens = Lexer.Tokenize(source, lenient: true);
        for (int i = 0; i + 1 < tokens.Length; i++)
        {
            if (tokens[i].Type is TokenType.Let or TokenType.Const && tokens[i + 1] is { Type: TokenType.Identifier } declared && declared.Value == name)
                return new UITextPosition(declared.Line - 1, declared.Col - 1);
        }

        return null;
    }

    /// <summary>Opens the language file of the game at a key, the line of its last part under the ones before it.</summary>
    private void GoToKey(string key)
    {
        string file = Path.Combine(Localisation.Folder, Localisation.Language + ".hor");
        if (Localisation.Folder.Length == 0 || !File.Exists(file))
        {
            Say("no language is loaded to look the key up in", error: true);
            return;
        }

        string[] lines = ReadLines(file);
        string[] parts = key.Split('.');
        int line = 0, part = 0;

        for (; line < lines.Length && part < parts.Length; line++)
        {
            if (lines[line].TrimStart().StartsWith(parts[part] + ":", StringComparison.Ordinal))
            {
                part++;
                if (part == parts.Length)
                    break;
            }
        }

        OpenAt(file, Math.Min(line, lines.Length - 1), Math.Max(0, lines[Math.Min(line, lines.Length - 1)].Length - lines[Math.Min(line, lines.Length - 1)].TrimStart().Length));
        Say(part == parts.Length ? $"{key} in {Localisation.Language}" : $"{Localisation.Language} has no {key}, this is as far as it goes", error: part != parts.Length);
    }

    /// <summary>Shift+F12, every line of the workspace the word at the caret is on as a word of its own, listed on the left.</summary>
    private void FindReferences()
    {
        var (start, end) = code.Document.WordAt(code.Document.Caret);
        if (end <= start)
        {
            Say("there is no word at the caret to look for");
            return;
        }

        string word = code.Document.LineAt(code.Document.Caret.Line)[start..end];
        List<HexResult> found = FindEverywhere(word, wholeWord: true);
        ShowResults($"WHERE {word} IS ({found.Count})", found);
        Say($"{word} is on {found.Count} lines of {found.Select(result => result.Path).Distinct().Count()} files");
    }
}
