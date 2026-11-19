using System.Numerics;
using System.Runtime.CompilerServices;
using Hex.Core;
using Hex.Shell;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Scripting;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private enum SpellingMode { Prose, Markdown, Comments, Markup }
    private sealed record SpellingTarget(CodeEditor Editor, int Version, SpellingMode Mode, string Language, int First, int Last);
    private const int SpellingBatch = 48, MaxMarkdownLines = 16_000;
    private SpellingTarget? spellingTarget;
    private SpellingResult? spellingResult;
    private CancellationTokenSource? spellingWork;
    private long spellingAfter;
    private string? unavailableSpelling;
    private readonly ConditionalWeakTable<CodeEditor, HashSet<string>> ignoredSpelling = new();
    private HashSet<string>? personalSpelling;

    private void UpdateSpelling()
    {
        if (!settings.SpellCheck || !OperatingSystem.IsWindows())
        {
            if (spellingTarget is not null) ResetSpelling(null);
            return;
        }

        var (editor, mode) = SpellingSurface();
        if (editor is null || !editor.VisibleInHierarchy || editor.CodeBounds.IsEmpty)
        {
            if (spellingTarget is not null) ResetSpelling(null);
            return;
        }
        var text = editor.Document;
        var area = editor.CodeBounds;
        int top = editor.PositionAt(new Vector2(area.Min.X, area.Max.Y - 1)).Line;
        int bottom = editor.PositionAt(new Vector2(area.Min.X, area.Min.Y + 1)).Line;
        int first = Math.Max(0, top / SpellingBatch * SpellingBatch - SpellingBatch / 2);
        int last = Math.Min(text.LineCount - 1, Math.Max(top + SpellingBatch * 2, bottom + SpellingBatch / 2));
        // Quantize both ends so scrolling a single line doesn't restart the check.
        last = Math.Min(text.LineCount - 1, (last / SpellingBatch + 1) * SpellingBatch - 1);
        if (mode == SpellingMode.Markdown && text.LineCount > MaxMarkdownLines)
        {
            if (spellingTarget is not null) ResetSpelling(null);
            return;
        }

        if (spellingTarget is not { } prior || prior.Editor != editor || prior.Version != text.Version
            || prior.Mode != mode || prior.Language != settings.SpellCheckLanguage || prior.First != first || prior.Last != last)
        {
            ResetSpelling(new(editor, text.Version, mode, settings.SpellCheckLanguage, first, last));
            Engine.WindowManager.RequestTickIn(0.25f);
            return;
        }
        if (spellingWork is not null || spellingResult is not null)
            return;
        long remaining = spellingAfter - Environment.TickCount64;
        if (remaining > 0)
        {
            Engine.WindowManager.RequestTickIn(remaining / 1000.0f);
            return;
        }
        if (mode is SpellingMode.Comments or SpellingMode.Markup && !editor.SyntaxReady)
        {
            Engine.WindowManager.RequestTickIn(0.1f);
            return;
        }

        string? markdown = mode == SpellingMode.Markdown ? text.Text : null;
        SpellingLine[] snapshot = markdown is null ? SpellingLines(prior) : [];
        var work = new CancellationTokenSource();
        spellingWork = work;
        CancellationToken cancel = work.Token;
        InBackground(Said("spelling.checking"), () =>
        {
            var lines = markdown is null
                ? snapshot.Select(line => line with { Text = HexSpelling.Mask(line.Text, inlineCode: true) }).ToArray()
                : HexSpelling.MarkdownLines(markdown, first, last, cancel).ToArray();
            return OperatingSystem.IsWindows() ? WindowsSpelling.Check(prior.Language, lines, cancel) : new SpellingResult(null, []);
        }, result =>
        {
            work.Dispose();
            if (!ReferenceEquals(spellingWork, work))
                return;
            spellingWork = null;
            if (spellingTarget != prior || editor.Document.Version != prior.Version)
                return;
            spellingResult = result;
            if (result.Language is null && unavailableSpelling != prior.Language)
            {
                unavailableSpelling = prior.Language;
                Say(Said("spelling.unavailable"));
            }
            else if (result.Language is not null)
                unavailableSpelling = null;
            PublishSpelling(prior, result);
        }, quiet: true);
    }

    private (CodeEditor? Editor, SpellingMode Mode) SpellingSurface()
    {
        if (agentsInput is not null && compositor.Focus == agentsInput)
            return (agentsInput, SpellingMode.Markdown);
        if (compositor.Focus == commitMessage)
            return (commitMessage, SpellingMode.Prose);
        if (document?.Editor is not { } editor || Face == HexFace.Noon)
            return (null, default);
        // Asked every tick, so the extension is looked at where it is, not lowered into a string of its own each time
        ReadOnlySpan<char> extension = Path.GetExtension((document.Path ?? string.Empty).AsSpan());
        if (IsAny(extension, ".md", ".markdown", ".mdown"))
            return (editor, SpellingMode.Markdown);
        if (IsAny(extension, ".txt", ".text"))
            return (editor, SpellingMode.Prose);
        if (editor.Highlighter is null)
            return (null, default);
        // A page's words are written for people as much as a comment is, only its comments were checked
        return IsAny(extension, ".html", ".htm", ".xhtml") ? (editor, SpellingMode.Markup) : (editor, SpellingMode.Comments);

        static bool IsAny(ReadOnlySpan<char> extension, params ReadOnlySpan<string> names)
        {
            foreach (string name in names)
                if (extension.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
    }

    private void ResetSpelling(SpellingTarget? target)
    {
        if (spellingTarget is { } old)
            old.Editor.SetSpellingMarks(-1, []);
        spellingWork?.Cancel();
        spellingWork = null;
        spellingResult = null;
        spellingTarget = target;
        spellingAfter = Environment.TickCount64 + 250;
    }

    private static SpellingLine[] SpellingLines(SpellingTarget target)
    {
        if (target.Mode == SpellingMode.Markup)
            return MarkupSpellingLines(target);

        var lines = new List<SpellingLine>();
        for (int line = target.First; line <= target.Last; line++)
        {
            string text = target.Editor.Document.LineAt(line);
            if (text.Length is 0 or > HexSpelling.MaxLineLength)
                continue;
            if (target.Mode == SpellingMode.Prose)
            {
                lines.Add(new(line, text));
                continue;
            }
            char[]? prose = null;
            foreach (SyntaxSpan span in target.Editor.CurrentSyntaxOf(line))
            {
                if (span.Kind != SyntaxKind.Comment || span.Start < 0 || span.Length <= 0 || span.Start > text.Length - span.Length)
                    continue;
                prose ??= new string(' ', text.Length).ToCharArray();
                text.AsSpan(span.Start, span.Length).CopyTo(prose.AsSpan(span.Start));
            }
            if (prose is not null)
                lines.Add(new(line, new string(prose)));
        }
        return [.. lines];
    }

    /// <summary>
    /// What of a page is written for people: the words between its tags and its comments, and inside a style or a script
    /// only their comments (by their colours, the code itself is no prose). Never a tag, its attributes and their values
    /// or an entity. Read from the top, a tag or a comment can start on a line above the ones on screen.
    /// </summary>
    private static SpellingLine[] MarkupSpellingLines(SpellingTarget target)
    {
        var lines = new List<SpellingLine>();
        bool inTag = false, inComment = false;
        char quote = '\0';
        string? raw = null;
        string? opening = null;
        for (int line = 0; line <= target.Last; line++)
        {
            string text = target.Editor.Document.LineAt(line);
            bool wanted = line >= target.First && text.Length is > 0 and <= HexSpelling.MaxLineLength;
            char[]? prose = wanted ? new string(' ', text.Length).ToCharArray() : null;
            bool any = false;

            // A style's or a script's comments, from the colours, while the rest of it is skipped below
            if (prose is not null && raw is not null)
                foreach (SyntaxSpan span in target.Editor.CurrentSyntaxOf(line))
                    if (span.Kind == SyntaxKind.Comment && span.Start >= 0 && span.Length > 0 && span.Start <= text.Length - span.Length)
                    {
                        text.AsSpan(span.Start, span.Length).CopyTo(prose.AsSpan(span.Start));
                        any = true;
                    }

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (raw is not null)
                {
                    // Code until the tag that ends it
                    if (c == '<' && text.AsSpan(i).StartsWith("</" + raw, StringComparison.OrdinalIgnoreCase))
                    {
                        i += 1 + raw.Length;
                        raw = null;
                        inTag = true;
                    }
                    continue;
                }

                if (inComment)
                {
                    if (text.AsSpan(i).StartsWith("-->", StringComparison.Ordinal))
                    {
                        inComment = false;
                        i += 2;
                        continue;
                    }
                    if (prose is not null) { prose[i] = c; any = true; }
                    continue;
                }

                if (inTag)
                {
                    if (quote != '\0')
                    {
                        if (c == quote) quote = '\0';
                        continue;
                    }
                    if (c is '"' or '\'')
                        quote = c;
                    else if (c == '>')
                    {
                        inTag = false;
                        // A style or a script, what's inside it is code until it ends
                        if (opening is "style" or "script")
                            raw = opening;
                        opening = null;
                    }
                    continue;
                }

                if (c == '<')
                {
                    if (text.AsSpan(i).StartsWith("<!--", StringComparison.Ordinal))
                    {
                        inComment = true;
                        i += 3;
                        continue;
                    }
                    if (i + 1 < text.Length && (char.IsLetter(text[i + 1]) || text[i + 1] is '/' or '!' or '?'))
                    {
                        inTag = true;
                        int name = i + 1, end = name;
                        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '-'))
                            end++;
                        opening = text[name..end].ToLowerInvariant();
                        continue;
                    }
                }

                // An entity, &amp; or &#8212;, is no word
                if (c == '&')
                {
                    int semi = text.IndexOf(';', i);
                    if (semi > i && semi - i <= 10)
                    {
                        i = semi;
                        continue;
                    }
                }

                if (prose is not null) { prose[i] = c; any = true; }
            }

            if (prose is not null && any)
                lines.Add(new(line, new string(prose)));
        }

        return [.. lines];
    }

    private static string SpellingDictionaryPath => HexFiles.Kept("spelling-words.tsv");

    private HashSet<string> PersonalSpelling()
    {
        if (personalSpelling is not null)
            return personalSpelling;
        personalSpelling = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!options.SelfTest && File.Exists(SpellingDictionaryPath))
                personalSpelling.UnionWith(File.ReadLines(SpellingDictionaryPath));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return personalSpelling;
    }

    private static string SpellKey(string language, string word) => language + "\t" + word;

    private bool SpellingAccepted(CodeEditor editor, string language, string word)
    {
        string key = SpellKey(language, word);
        return PersonalSpelling().Contains(key)
            || ignoredSpelling.TryGetValue(editor, out var ignored) && ignored.Contains(key);
    }

    private void PublishSpelling(SpellingTarget target, SpellingResult result)
    {
        var marks = new List<UISpellingMark>();
        if (result.Language is { } language)
            foreach (var issue in result.Issues)
                if (!SpellingAccepted(target.Editor, language, issue.Word))
                    marks.Add(new(issue.Line, issue.Column, issue.Length));

        target.Editor.SetSpellingMarks(target.Version, marks);
    }

    private void AddSpellingMenu(CodeEditor editor, UITextPosition at, Menu menu)
    {
        if (!settings.SpellCheck || spellingTarget is not { } target || target.Editor != editor
            || editor.Document.Version != target.Version || spellingResult is not { Language: { } language } result)
            return;
        foreach (SpellingIssue issue in result.Issues)
        {
            if (issue.Line != at.Line || at.Column < issue.Column || at.Column > issue.Column + issue.Length
                || SpellingAccepted(editor, language, issue.Word))
                continue;
            var spelling = menu.AddMenu(Said("spelling.menu"));
            var start = new UITextPosition(issue.Line, issue.Column);
            var end = new UITextPosition(issue.Line, issue.Column + issue.Length);
            foreach (string suggestion in issue.Suggestions)
                spelling.Add(suggestion, () =>
                {
                    if (!editor.ReadOnly && editor.Document.Version == target.Version
                        && editor.Document.TextBetween(start, end) == issue.Word)
                        editor.Document.ApplyEdits([new UITextEdit(start, end, suggestion)]);
                });
            if (editor != agentsInput && editor != commitMessage)
                spelling.Add(Said("spelling.ignore"), () =>
                {
                    ignoredSpelling.GetValue(editor, _ => new(StringComparer.OrdinalIgnoreCase)).Add(SpellKey(language, issue.Word));
                    if (spellingTarget == target) PublishSpelling(target, result);
                });
            spelling.Add(Said("spelling.add"), () =>
            {
                string key = SpellKey(language, issue.Word);
                if (key.IndexOfAny(['\r', '\n']) >= 0 || PersonalSpelling().Contains(key))
                    return;
                try
                {
                    if (!options.SelfTest)
                    {
                        string path = SpellingDictionaryPath;
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        File.AppendAllText(path, key + Environment.NewLine);
                    }
                    PersonalSpelling().Add(key);
                    if (spellingTarget == target) PublishSpelling(target, result);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    Say(Said("spelling.save_failed"), error: true);
                }
            });
            break;
        }
    }

    private void ShowSpellingMenu(CodeEditor editor, Vector2 point)
    {
        var menu = new Menu("spelling");
        AddSpellingMenu(editor, editor.PositionAt(point), menu);
        if (menu.Items.Count > 0)
            ContextMenu.ShowAt(chrome, compositor.Pointer.Position, menu);
    }
}
