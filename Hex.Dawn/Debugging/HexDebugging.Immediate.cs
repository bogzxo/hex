using System.Collections.Concurrent;

using Horizon.UI.Components;
using Horizon.UI.Scripting;

namespace Hex.Dawn;

// What Evaluate Expression came to, kept, Visual Studio's QuickWatch and Immediate window in a section of the Debug side.
// It said what it came to along the bottom for a moment and that was it, and something with members had nothing to open.
// Each one is a row opened out like a variable while it's stopped where it was evaluated, the newest at the top, and what
// it came to stays as text once it goes on. And the scope a file's expressions are evaluated in, read off its text.
internal sealed partial class HexDebugging
{
    // An expression evaluated, what it came to once it has, the stop it was evaluated at and its row
    private sealed class Evaluated(string expression, int stop)
    {
        public string Expression { get; } = expression;
        public int Stop { get; } = stop;
        public DebugEvaluation? Result { get; set; }
        public UITreeItem? Row { get; set; }
        public bool Stale { get; set; }
    }

    // The newest first, as many as are kept
    private readonly List<Evaluated> immediate = [];
    private const int IMMEDIATE_KEPT = 20;

    // The session's C# evaluator, made with the session, which remembers the names it found
    private HexDebugEvaluator? evaluator;

    /// <summary>An expression evaluated where it's stopped, into the Immediate section and said along the bottom.</summary>
    internal void EvaluateIntoImmediate(string expression)
    {
        if (expression.Length == 0)
            return;
        if (!DebugStopped || debugging is not { } session || stoppedFrames.Count == 0)
        {
            context.Notices.Say(context.Strings.Said("debugging.evaluates_when_stopped"));
            return;
        }

        DebugFrame frame = stoppedFrames[stoppedFrame];
        var entry = new Evaluated(expression, stops);
        immediate.Insert(0, entry);
        if (immediate.Count > IMMEDIATE_KEPT)
            immediate.RemoveAt(immediate.Count - 1);
        context.Panels.ShowSide(HexSide.Debug);
        context.Panels.RefreshSide(HexSide.Debug);

        _ = Task.Run(async () =>
        {
            DebugEvaluation evaluated = await Evaluate(session, expression, frame.Id, frame.File, "repl");
            debugWork.Enqueue(() =>
            {
                entry.Result = evaluated;
                entry.Row = evaluated.Value is { } value && entry.Stop == stops && ReferenceEquals(debugging, session)
                    ? variables.Items(session, session.Variables)(value)
                    : null;

                // What's said along the bottom goes in the output as well
                if (evaluated.Value is { } said)
                    context.Notices.Say(context.Strings.Said("debugging.evaluated", ("expression", expression), ("value", said.Value)));
                else
                    context.Notices.Say(context.Strings.Said("debugging.could_not_evaluate", ("expression", expression), ("why", evaluated.Error ?? context.Strings.Said("debugging.not_here"))), error: true);
                context.Panels.RefreshSide(HexSide.Debug);
            });
        });
    }

    /// <summary>The Immediate section, each expression evaluated and what it came to, null while nothing has been.</summary>
    private UITreeItem? ImmediateSection()
    {
        if (immediate.Count == 0)
            return null;

        var section = new UITreeItem(context.Strings.Said("debugging.immediate"), "debug-console")
        {
            Expandable = true,
            Expanded = true,
            Children = [],
            Tag = new SideTag(Menu: _ => ImmediateMenu(null))
        };

        foreach (Evaluated entry in immediate)
        {
            section.Children.Add(ImmediateRow(entry));
        }

        return section;
    }

    private UITreeItem ImmediateRow(Evaluated entry)
    {
        // Gone on since it was evaluated, what it came to stays as text with nothing to open, its members are gone
        if (entry.Row is { } row && !entry.Stale && (entry.Stop != stops || !DebugStopped))
        {
            entry.Stale = true;
            entry.Row = new UITreeItem(row.Text, "symbol-variable") { Color = context.Panels.Dim, Detail = row.Detail, Tooltip = row.Tooltip };
        }

        if (entry.Row is null)
        {
            string why = entry.Result is { Error: var error } ? error ?? context.Strings.Said("debugging.not_here") : context.Strings.Said("debugging.asking");
            entry.Row = new UITreeItem(entry.Result is null ? entry.Expression : $"{entry.Expression} = ?", "symbol-variable")
            {
                Color = entry.Result is null ? context.Panels.Dim : context.Panels.Error,
                Detail = why,
                Tooltip = why
            };
        }

        entry.Row.Tag = entry.Row.Tag is SideTag tag ? tag with { Menu = _ => ImmediateMenu(entry) } : new SideTag(Menu: _ => ImmediateMenu(entry));
        return entry.Row;
    }

    // A right click on an evaluated row or the section, its expression watched, it taken away or all of them
    private void ImmediateMenu(Evaluated? entry)
    {
        var menu = new Menu("immediate");
        if (entry is not null)
        {
            menu.Add(context.Strings.Said("debugging.evaluate_again"), () => EvaluateIntoImmediate(entry.Expression), icon: "refresh").IsEnabled = () => DebugStopped;
            menu.Add(context.Strings.Said("debugging.add_to_watch"), () => Watch(entry.Expression), icon: "eye");
            menu.Add(context.Strings.Said("debugging.remove"), () =>
            {
                immediate.Remove(entry);
                context.Panels.RefreshSide(HexSide.Debug);
            }, icon: "close");
            menu.AddSeparator();
        }

        menu.Add(context.Strings.Said("debugging.clear_immediate"), () =>
        {
            immediate.Clear();
            context.Panels.RefreshSide(HexSide.Debug);
        }, icon: "clear-all");
        context.Windows.Menu(menu);
    }

    // An expression watched, once
    private void Watch(string expression) => WatchExpression(expression);

    /* The scope of a file */

    // What each file's names could mean, by its path, kept while the session is with when the file was written
    private readonly ConcurrentDictionary<string, (DateTime Written, HexDebugScope Scope)> scopes = new(StringComparer.OrdinalIgnoreCase);

    // The global usings of each project, by its folder
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> globalUsings = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The scope of the file a frame is in, read off its text (and kept until it's written again), empty for none.</summary>
    private HexDebugScope ScopeOf(string? file)
    {
        if (file is null || !IsCSharp(file))
            return HexDebugScope.Empty;

        try
        {
            DateTime written = File.GetLastWriteTimeUtc(file);
            if (scopes.TryGetValue(file, out var kept) && kept.Written == written)
                return kept.Scope;

            HexDebugScope scope = HexDebugScope.Of(File.ReadAllText(file), GlobalUsingsOf(file));
            scopes[file] = (written, scope);
            return scope;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return HexDebugScope.Empty;
        }
    }

    /// <summary>
    /// The global usings of the project a file is in, out of the file the SDK writes them to (obj/.../X.GlobalUsings.g.cs),
    /// the SDK's implicit ones when it isn't there.
    /// </summary>
    private IReadOnlyList<string> GlobalUsingsOf(string file)
    {
        string? folder = Path.GetDirectoryName(Path.GetFullPath(file));
        for (int up = 0; folder is not null && up < 8; folder = Path.GetDirectoryName(folder), up++)
        {
            string? project = Directory.EnumerateFiles(folder, "*.csproj").FirstOrDefault();
            if (project is null)
                continue;

            return globalUsings.GetOrAdd(folder, _ =>
            {
                string obj = Path.Combine(folder, "obj");
                string name = Path.GetFileNameWithoutExtension(project) + ".GlobalUsings.g.cs";
                string? generated = Directory.Exists(obj)
                    ? Directory.EnumerateFiles(obj, name, SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
                    : null;
                return generated is not null ? HexDebugScope.GlobalUsingsIn(File.ReadAllText(generated)) : HexDebugScope.IMPLICIT;
            });
        }

        return HexDebugScope.IMPLICIT;
    }
}
