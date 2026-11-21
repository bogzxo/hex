namespace Hex.Dawn;

// The lightbulb in the gutter on the caret's line when something's wrong there that has a fix, a press on it Ctrl+.'s
// menu (QuickFix). Nothing is asked on a line with nothing wrong, the lightbulb's never there. On one with something
// wrong the compiler's own words are looked at first (CompilerFixes, nothing asked), and the server is asked for its
// actions only once the caret has rested on the line a moment, once for that version of the file and that line: what
// it says is kept, a caret moved back and forth asks nothing again.
internal sealed partial class HexLanguages
{
    // How long the caret rests on a line with something wrong before the server's asked what it can fix there
    private const int LIGHTBULB_REST_MS = 350;

    // The most answers kept, the documents' versions and lines, the oldest let go of first
    private const int LIGHTBULB_KEPT = 64;

    private readonly record struct LightbulbKey(HexDocument Of, int Version, int Line);

    // What the server said there, whether it has a fix and whether it'd pick one itself; null while it's being asked
    private readonly Dictionary<LightbulbKey, (bool Fixes, bool Preferred)?> lightbulbs = [];
    private readonly Queue<LightbulbKey> lightbulbOrder = new();
    private LightbulbKey? lightbulbResting;
    private long lightbulbRestingSince;
    private Timer? lightbulbWaker;
    private CodeEditor? lightbulbShownIn;

    /// <summary>How many times the server's been asked for a line's actions for the lightbulb, for the self test.</summary>
    internal int LightbulbAsks { get; private set; }

    /// <summary>Every tick, the lightbulb on the caret's line of the document in front, or nowhere.</summary>
    private void UpdateLightbulb()
    {
        HexDocument of = context.Documents.Current;
        CodeEditor editor = of.Editor;
        if (!ReferenceEquals(lightbulbShownIn, editor))
        {
            // The editor that had it before, out of sight now, loses it
            if (lightbulbShownIn is { } before)
                before.ActionLine = -1;
            lightbulbShownIn = editor;
        }

        var (shown, preferred) = LightbulbFor(of);
        editor.OnActionPressed ??= QuickFix;
        editor.ActionLine = shown;
        editor.ActionPreferred = preferred;
    }

    // The line the lightbulb goes on and whether it's the server's own pick, -1 for none
    private (int Line, bool Preferred) LightbulbFor(HexDocument of)
    {
        CodeEditor editor = of.Editor;
        if (editor.ReadOnly || of.Path is null || of.DiffOf is not null || editor.Document.HasSelection)
            return (-1, false);

        int line = editor.Document.Caret.Line;
        bool wrong = false;
        IReadOnlyList<UICodeDiagnostic> diagnostics = editor.Diagnostics;
        for (int i = 0; i < diagnostics.Count && !wrong; i++)
            wrong = diagnostics[i].Line == line;
        if (!wrong)
        {
            lightbulbResting = null;
            return (-1, false);
        }

        var key = new LightbulbKey(of, editor.Document.Version, line);
        if (lightbulbs.TryGetValue(key, out var said))
            return said is { Fixes: true } fix ? (line, fix.Preferred) : (-1, false);
        if (CompilerFixes(of, line, line).Count > 0)
        {
            Keep(key, (true, true));
            return (line, true);
        }

        // Asked only once the caret's stayed and what was typed has gone to the server, the answer's for what it has
        if (lightbulbResting != key)
        {
            lightbulbResting = key;
            lightbulbRestingSince = Environment.TickCount64;
        }
        long rested = Environment.TickCount64 - lightbulbRestingSince;
        if (rested < LIGHTBULB_REST_MS || ChangePending(of) || !CanServe(of, "codeActionProvider"))
        {
            lightbulbWaker ??= new Timer(_ => context.Wake());
            lightbulbWaker.Change(Math.Max(LIGHTBULB_REST_MS - rested, 50), Timeout.Infinite);
            return (-1, false);
        }

        if (!Serves(of, "codeActionProvider", out LanguageServer server, out string path))
            return (-1, false);
        Keep(key, null);
        LightbulbAsks++;
        int length = editor.Document.LineAt(line).Length;
        When(server.Actions(path, line, 0, line, length), [], actions =>
        {
            bool fixes = false, pick = false;
            foreach (LanguageServerAction action in actions)
            {
                fixes |= action.Fixes;
                pick |= action.Preferred;
            }
            Keep(key, (fixes || pick, pick));
        });
        return (-1, false);
    }

    // An answer kept, the oldest let go of past the most kept
    private void Keep(LightbulbKey key, (bool Fixes, bool Preferred)? said)
    {
        if (!lightbulbs.ContainsKey(key))
            lightbulbOrder.Enqueue(key);
        lightbulbs[key] = said;
        while (lightbulbOrder.Count > LIGHTBULB_KEPT)
            lightbulbs.Remove(lightbulbOrder.Dequeue());
    }
}
