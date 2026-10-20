using Horizon.HIDL.Lexing;
using Horizon.HIDL.Parsing;
using Horizon.HIDL.Runtime;

namespace Hex.Dawn;

/// <summary>What a file of the workspace says about a name with a // @doc comment, and where.</summary>
internal readonly record struct WorkspaceDoc(string Doc, string File, int Line, int Column);

// Documentation, // @doc over a declaration, a key or a field (see the language's HIDLDocs), shown wherever the name is.
// The run of the file on screen knows its own and its includes', the file's text knows what it says this moment, and
// the workspace's index knows every other file's, so a name documented in one file reads its documentation in another.
internal sealed partial class HexScene
{
    // What the keys of the last run's values document, by the key's name, worked out once a run
    private HexValues? keyDocsOf;
    private Dictionary<string, string> keyDocs = [];

    /// <summary>
    /// What a name is documented as and where, the nearest first. Its declaration in the last run of the file (which
    /// includes what the file included), a key of what it came to, the file's text as it is now, then the workspace. Null
    /// for a name nothing documents.
    /// </summary>
    private (string Doc, string? Where)? DocOf(string name)
    {
        if (running.LastRun is { } ran && running.LastRunOf == document)
        {
            if (ran.Sources.TryGetDeclaration(name, out var declared) && declared.Doc is { } doc)
                return (doc, Elsewhere(declared.File, declared.Span.Line));

            if (KeyDocs(ran).TryGetValue(name, out string? keyDoc))
                return (keyDoc, null);
        }

        // What the text says right now, a doc typed a moment ago that the run hasn't caught up with
        foreach (HidlDoc doc in TextDocs(code.Text))
        {
            if (doc.Name == name)
                return (doc.Doc, null);
        }

        // The workspace's, gathered in the background the first time it is wanted
        if (docIndex is null)
            Vocabulary();

        if (docIndex?.TryGetValue(name, out WorkspaceDoc found) == true)
            return (found.Doc, Elsewhere(found.File, found.Line + 1));

        return null;
    }

    // What the text on screen documents, kept against the text it was read out of (the same string until it changes),
    // the hover asked again for every word the pointer rested on and lexed the whole file every time
    private string? textDocsOf;
    private List<HidlDoc> textDocs = [];

    private List<HidlDoc> TextDocs(string text)
    {
        if (!ReferenceEquals(text, textDocsOf))
        {
            textDocsOf = text;
            textDocs = HIDLDocs.Read(Lexer.Tokenize(text, lenient: true));
        }

        return textDocs;
    }

    /// <summary>Where something is if that isn't the file on screen, "greetings.hor line 9", null if it is.</summary>
    private string? Elsewhere(string? file, int line)
    {
        if (file is null || (document.Path is { } open && string.Equals(Path.GetFullPath(file), Path.GetFullPath(open), PlatformPaths.Comparison)))
            return null;

        return $"{Path.GetFileName(file)} line {line}";
    }

    /// <summary>Helper method for what the keys of a run's values document, by name, the first of a name that has any.</summary>
    private Dictionary<string, string> KeyDocs(HexValues ran)
    {
        if (ReferenceEquals(keyDocsOf, ran))
            return keyDocs;

        keyDocsOf = ran;
        keyDocs = new Dictionary<string, string>(StringComparer.Ordinal);
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);

        void Walk(IRuntimeValue value)
        {
            switch (value)
            {
                case ObjectValue obj when seen.Add(obj.Properties):
                    foreach (var (key, inner) in obj.Properties)
                    {
                        if (ran.Sources.TryGetKey(obj, key, out var at) && at.Doc is { } doc)
                            keyDocs.TryAdd(key, doc);
                        Walk(inner);
                    }
                    break;

                case ListValue list when seen.Add(list):
                    foreach (IRuntimeValue item in list.Items)
                        Walk(item);
                    break;
            }
        }

        foreach (var (_, value) in ran.Declared)
            Walk(value);

        return keyDocs;
    }

    /// <summary>A name's documentation as the end of a hint, nothing if it has none.</summary>
    private string DocHint(string name) => DocOf(name) is { } doc
        ? $"\n\n{UIRichText.Literal(doc.Doc)}{(doc.Where is { } where ? $"\n\n*{where}*" : string.Empty)}"
        : string.Empty;
}
