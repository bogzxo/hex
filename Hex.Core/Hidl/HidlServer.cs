using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Horizon.Content;
using Horizon.HIDL.Lexing;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Scripting;

namespace Hex.Core;

/// <summary>
/// HIDL's language server, the protocol VS Code and every other editor speaks, over a pair of streams (Hex's stdin and
/// stdout when it is started with --lsp). What it knows it knows the way Hex's own editor does (HidlAnalysis): what the
/// parser didn't like, where running a file went wrong and the lints of what it came to, completion, hovers, where a
/// name is declared and the outline of a file. Files are synced whole. Requests are answered on the connection's thread,
/// the checks run on one of their own a moment after the typing stops, so a request never waits for a run.
/// </summary>
internal sealed partial class HidlServer : IDisposable
{
    // How long a file has to be left alone after a change before it is checked again
    private const int CHECK_AFTER_MS = 300;

    // Hex's texts, what the lints say, next to Hex wherever it's started from
    private const string STRINGS_FILE = "Assets/hex/localisation/en-gb.hor";

    private readonly JsonRpcConnection connection;
    private readonly ConcurrentDictionary<string, (string Text, int Version)> open = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> changed = new(StringComparer.Ordinal);
    private readonly ManualResetEventSlim done = new();
    private readonly AutoResetEvent wake = new(false);
    private readonly Thread checker;
    private readonly IReadOnlyDictionary<string, string> strings;
    private List<string> roots = [];
    private HashSet<string>? names;
    private volatile bool shuttingDown;

    /// <summary>Whether the client said shutdown before exit, which is what makes the exit a clean one.</summary>
    public bool ShutDownCleanly { get; private set; }

    public HidlServer(Stream input, Stream output)
    {
        strings = ReadStrings();
        HidlAnalysis.KnowKinds();

        connection = new JsonRpcConnection(input, output) { OnRequest = Answer, Closed = _ => Stop() };
        connection.OnNotification("initialized", _ => { });
        connection.OnNotification("exit", _ => Stop());
        connection.OnNotification("textDocument/didOpen", Opened);
        connection.OnNotification("textDocument/didChange", Changed);
        connection.OnNotification("textDocument/didClose", ClosedDocument);
        connection.OnNotification("workspace/didChangeWatchedFiles", _ => names = null);

        checker = new Thread(Check) { IsBackground = true, Name = "hidl checks" };
        checker.Start();
    }

    /// <summary>Serves until the client says exit or goes away.</summary>
    public void Wait() => done.Wait();

    /// <summary>
    /// Hex as a language server on its stdin and stdout, until the client is done with it. What the protocol wants is
    /// 0 after a shutdown and 1 for an exit without one.
    /// </summary>
    public static int Serve()
    {
        using Stream input = Console.OpenStandardInput();
        using Stream output = Console.OpenStandardOutput();

        // Stdout is the protocol's, anything written to the console (the engine's log) goes to stderr instead
        Console.SetOut(Console.Error);
        using var server = new HidlServer(input, output);
        server.Wait();
        return server.ShutDownCleanly ? 0 : 1;
    }

    private void Stop()
    {
        shuttingDown = true;
        wake.Set();
        done.Set();
    }

    public void Dispose()
    {
        Stop();
        connection.Dispose();
    }

    /* Requests */

    private JsonNode? Answer(string method, JsonNode? parameters)
    {
        switch (method)
        {
            case "initialize":
                return Initialize(parameters);

            case "shutdown":
                ShutDownCleanly = true;
                shuttingDown = true;
                wake.Set();
                return null;

            case "textDocument/completion":
                return Completion(parameters);

            case "textDocument/hover":
                return Hover(parameters);

            case "textDocument/definition":
                return Definition(parameters);

            case "textDocument/documentSymbol":
                return Symbols(parameters);

            default:
                return null;
        }
    }

    private JsonNode Initialize(JsonNode? parameters)
    {
        // The folders of the workspace, where a name is looked for when the file doesn't declare it
        var folders = new List<string>();
        if (parameters?["workspaceFolders"] is JsonArray workspace)
        {
            foreach (JsonNode? folder in workspace)
            {
                if (folder?["uri"]?.GetValue<string>() is { } uri && PathOf(uri) is { } path)
                    folders.Add(path);
            }
        }
        else if (parameters?["rootUri"]?.GetValue<string>() is { } root && PathOf(root) is { } path)
        {
            folders.Add(path);
        }

        roots = folders;

        return new JsonObject
        {
            ["capabilities"] = new JsonObject
            {
                // Whole files, opened, changed and closed
                ["textDocumentSync"] = new JsonObject { ["openClose"] = true, ["change"] = 1 },
                ["completionProvider"] = new JsonObject { ["triggerCharacters"] = new JsonArray(".", "\"", "@") },
                ["hoverProvider"] = true,
                ["definitionProvider"] = true,
                ["documentSymbolProvider"] = true
            },
            ["serverInfo"] = new JsonObject { ["name"] = "hidl", ["version"] = typeof(HidlServer).Assembly.GetName().Version?.ToString(3) ?? "0" }
        };
    }

    private JsonNode? Completion(JsonNode? parameters)
    {
        if (At(parameters) is not var (uri, line, column) || !open.TryGetValue(uri, out var file))
            return null;

        if (HidlAnalysis.Complete(file.Text, line, column, Names()) is not { } offered)
            return null;

        var items = new JsonArray();
        foreach (UICompletion item in offered.Items)
        {
            items.Add((JsonNode)new JsonObject
            {
                ["label"] = item.Text,
                ["detail"] = item.Detail,
                ["kind"] = KindOf(item.Detail),
                ["textEdit"] = new JsonObject
                {
                    ["range"] = Range(line, offered.Start, line, column),
                    ["newText"] = item.Insert ?? item.Text
                }
            });
        }

        return new JsonObject { ["isIncomplete"] = false, ["items"] = items };
    }

    // What the protocol calls a kind of completion, by what Hex says about it
    private static int KindOf(string detail) => detail switch
    {
        "keyword" => 14,
        "in this file" or "in the workspace" => 6,
        _ => 10
    };

    private JsonNode? Hover(JsonNode? parameters)
    {
        if (At(parameters) is not var (uri, line, column) || !open.TryGetValue(uri, out var file))
            return null;

        if (HidlAnalysis.Hover(file.Text, line, column, name => DocElsewhere(uri, name)) is not { } said)
            return null;

        return new JsonObject
        {
            ["contents"] = new JsonObject { ["kind"] = "markdown", ["value"] = Markdown(said) }
        };
    }

    /// <summary>Hex's markdown as everybody else's, without the icons Hex draws in it ($(name)).</summary>
    internal static string Markdown(string hex) => Icon().Replace(hex, string.Empty);

    [GeneratedRegex(@"\$\([a-z0-9-]+\)\s?")]
    private static partial Regex Icon();

    private JsonNode? Definition(JsonNode? parameters)
    {
        if (At(parameters) is not var (uri, line, column) || !open.TryGetValue(uri, out var file))
            return null;

        string current = HidlAnalysis.LineOf(file.Text, line);

        // A text naming a file, an include or a template, goes to the file
        foreach (Token token in Lexer.Tokenize(current, lenient: true))
        {
            if (token.Type != TokenType.TextLiteral || column < token.Col - 1 || column > token.Col - 1 + token.Length || token.Value.Length == 0)
                continue;

            if (PathOf(uri) is { } here && Path.Combine(Path.GetDirectoryName(here)!, token.Value) is var named && File.Exists(named))
                return Location(Path.GetFullPath(named), 0, 0, 0);
        }

        var (start, end) = HidlAnalysis.WordAt(current, column);
        if (end <= start)
            return null;

        string word = current[start..end];

        // In this file, then the other open ones, then the workspace
        if (HidlAnalysis.FindDeclaration(file.Text, word, line, column) is { } declared)
            return LocationOf(uri, declared, word.Length);

        foreach (var (other, opened) in open)
        {
            if (other != uri && HidlAnalysis.FindDeclaration(opened.Text, word) is { } there)
                return LocationOf(other, there, word.Length);
        }

        foreach (string path in WorkspaceFiles())
        {
            string source;
            try
            {
                source = File.ReadAllText(path);
            }
            catch (Exception)
            {
                continue;
            }

            if (source.Contains(word, StringComparison.Ordinal) && HidlAnalysis.FindDeclaration(source, word) is { } there)
                return Location(path, there.Line, there.Column, word.Length);
        }

        return null;
    }

    private JsonNode? Symbols(JsonNode? parameters)
    {
        if (parameters?["textDocument"]?["uri"]?.GetValue<string>() is not { } uri || !open.TryGetValue(uri, out var file))
            return null;

        // The outline's entries, each to the end of its line, nested by how deep they are
        List<OutlineEntry> entries = HexOutline.Outline(file.Text, PathOf(uri));
        var top = new JsonArray();
        var parents = new Stack<(int Depth, JsonArray Children)>();
        parents.Push((-1, top));

        foreach (OutlineEntry entry in entries)
        {
            while (parents.Peek().Depth >= entry.Depth)
                parents.Pop();

            int length = Math.Max(entry.Column + entry.Name.Length, HidlAnalysis.LineOf(file.Text, entry.Line).Length);
            var children = new JsonArray();
            parents.Peek().Children.Add((JsonNode)new JsonObject
            {
                ["name"] = entry.Name,
                ["kind"] = SymbolKind(entry.Icon),
                ["range"] = Range(entry.Line, 0, entry.Line, length),
                ["selectionRange"] = Range(entry.Line, entry.Column, entry.Line, entry.Column + entry.Name.Length),
                ["children"] = children
            });
            parents.Push((entry.Depth, children));
        }

        return top;
    }

    // What the protocol calls a kind of symbol, by the outline's icon for it
    private static int SymbolKind(string icon) => icon switch
    {
        "symbol-method" => 12,
        "symbol-field" or "symbol-property" => 8,
        "symbol-key" => 20,
        "symbol-constant" => 14,
        "symbol-class" or "symbol-structure" => 23,
        _ => 13
    };

    /* Documents and their checks */

    private void Opened(JsonNode? parameters)
    {
        if (parameters?["textDocument"] is not JsonObject document || document["uri"]?.GetValue<string>() is not { } uri)
            return;

        open[uri] = (Normalized(document["text"]?.GetValue<string>() ?? string.Empty), document["version"]?.GetValue<int>() ?? 0);
        Changed(uri);
    }

    private void Changed(JsonNode? parameters)
    {
        if (parameters?["textDocument"]?["uri"]?.GetValue<string>() is not { } uri || parameters["contentChanges"] is not JsonArray changes || changes.Count == 0)
            return;

        // Whole files, so the last change is the file
        if (changes[^1]?["text"]?.GetValue<string>() is { } text)
        {
            open[uri] = (Normalized(text), parameters["textDocument"]?["version"]?.GetValue<int>() ?? 0);
            Changed(uri);
        }
    }

    private void ClosedDocument(JsonNode? parameters)
    {
        if (parameters?["textDocument"]?["uri"]?.GetValue<string>() is not { } uri)
            return;

        open.TryRemove(uri, out _);
        changed.TryRemove(uri, out _);
        Publish(uri, null, []);

        // A file that includes it reads it from the disk now
        foreach (string other in open.Keys)
            Changed(other);
    }

    private void Changed(string uri)
    {
        changed[uri] = Environment.TickCount64;

        // What includes it may have changed with it, open files are checked again as well
        foreach (string other in open.Keys)
        {
            if (other != uri)
                changed.TryAdd(other, Environment.TickCount64);
        }

        wake.Set();
    }

    private static string Normalized(string text) => text.Replace("\r\n", "\n");

    // The checking thread, a file at a time once it was left alone long enough
    private void Check()
    {
        while (!shuttingDown)
        {
            long now = Environment.TickCount64;
            int wait = Timeout.Infinite;
            foreach (var (uri, at) in changed)
            {
                long due = at + CHECK_AFTER_MS;
                if (due > now)
                {
                    wait = wait == Timeout.Infinite ? (int)(due - now) : Math.Min(wait, (int)(due - now));
                    continue;
                }

                if (!changed.TryRemove(new KeyValuePair<string, long>(uri, at)) || !open.TryGetValue(uri, out var file))
                    continue;

                List<UICodeDiagnostic> found;
                try
                {
                    found = HidlAnalysis.Check(file.Text, PathOf(uri), OpenTexts(), Said);
                }
                catch (Exception e)
                {
                    found = [new UICodeDiagnostic(0, 0, 1, $"HIDL's language server couldn't check this, {e.Message}")];
                }

                // Changed again while it was being checked, it's checked again
                if (open.TryGetValue(uri, out var still) && still.Version == file.Version)
                    Publish(uri, file.Version, found);
            }

            if (wait != Timeout.Infinite || changed.IsEmpty)
                wake.WaitOne(wait);
        }
    }

    private void Publish(string uri, int? version, List<UICodeDiagnostic> found)
    {
        if (connection.IsClosed)
            return;

        var diagnostics = new JsonArray();
        foreach (UICodeDiagnostic diagnostic in found)
        {
            diagnostics.Add((JsonNode)new JsonObject
            {
                ["range"] = Range(diagnostic.Line, diagnostic.Column, diagnostic.Line, diagnostic.Column + diagnostic.Length),
                ["severity"] = diagnostic.Warning ? 2 : 1,
                ["source"] = "hidl",
                ["message"] = diagnostic.Message
            });
        }

        var parameters = new JsonObject { ["uri"] = uri, ["diagnostics"] = diagnostics };
        if (version is { } known)
            parameters["version"] = known;

        try
        {
            connection.Notify("textDocument/publishDiagnostics", parameters);
        }
        catch (InvalidOperationException)
        {
            // gone while it was checking
        }
    }

    /// <summary>The open files by their full paths, what an include reads before the disk.</summary>
    private Dictionary<string, string> OpenTexts()
    {
        var texts = new Dictionary<string, string>(PlatformPaths.Comparer);
        foreach (var (uri, file) in open)
        {
            if (PathOf(uri) is { } path)
                texts[Path.GetFullPath(path)] = file.Text;
        }

        return texts;
    }

    /* The rest of the workspace */

    private IEnumerable<string> WorkspaceFiles() => roots.Count == 0 ? [] : HexSearch.FilesUnder(roots).Where(path => path.EndsWith(".hor", StringComparison.OrdinalIgnoreCase)).Take(2000);

    /// <summary>The names of the workspace's files, read once and again when the client says files changed.</summary>
    private HashSet<string> Names()
    {
        if (names is { } known)
            return known;

        var gathered = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in WorkspaceFiles())
        {
            try
            {
                foreach (Token token in Lexer.Tokenize(File.ReadAllText(path), lenient: true))
                {
                    if (token.Type == TokenType.Identifier && token.Value.Length > 1)
                        gathered.Add(token.Value);
                }
            }
            catch (Exception)
            {
                // a file that can't be read has no names
            }
        }

        return names = gathered;
    }

    /// <summary>What another open file documents a name as, and where.</summary>
    private HidlDocumented? DocElsewhere(string uri, string name)
    {
        if (open.TryGetValue(uri, out var file) && HidlAnalysis.TextDoc(file.Text, name) is { } here)
            return here;

        foreach (var (other, opened) in open)
        {
            if (other != uri && HidlAnalysis.TextDoc(opened.Text, name) is { } there)
                return there with { Where = PathOf(other) is { } path ? Path.GetFileName(path) : null };
        }

        return null;
    }

    /* Texts, places and paths */

    private IReadOnlyDictionary<string, string> ReadStrings()
    {
        try
        {
            return Localisation.ReadFile(Path.Combine(AppContext.BaseDirectory, STRINGS_FILE));
        }
        catch (Exception)
        {
            // The lints say their keys then, which is still better than nothing
            return new Dictionary<string, string>();
        }
    }

    private string Said(string key, params (string Name, string Value)[] filled)
    {
        if (!strings.TryGetValue(key, out string? text))
            return $"@{key}";

        foreach (var (name, value) in filled)
            text = text.Replace("{" + name + "}", value);

        return text;
    }

    private static (string Uri, int Line, int Column)? At(JsonNode? parameters)
    {
        if (parameters?["textDocument"]?["uri"]?.GetValue<string>() is not { } uri || parameters["position"] is not JsonObject position)
            return null;

        return (uri, position["line"]?.GetValue<int>() ?? 0, position["character"]?.GetValue<int>() ?? 0);
    }

    internal static string? PathOf(string uri)
    {
        try
        {
            var parsed = new Uri(uri);
            return parsed.IsFile ? parsed.LocalPath : null;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    private static JsonObject Range(int line, int column, int toLine, int toColumn) => new()
    {
        ["start"] = new JsonObject { ["line"] = line, ["character"] = column },
        ["end"] = new JsonObject { ["line"] = toLine, ["character"] = toColumn }
    };

    private static JsonObject LocationOf(string uri, (int Line, int Column) at, int length) => new()
    {
        ["uri"] = uri,
        ["range"] = Range(at.Line, at.Column, at.Line, at.Column + length)
    };

    private static JsonObject Location(string path, int line, int column, int length) => new()
    {
        ["uri"] = new Uri(path).AbsoluteUri,
        ["range"] = Range(line, column, line, column + length)
    };
}
