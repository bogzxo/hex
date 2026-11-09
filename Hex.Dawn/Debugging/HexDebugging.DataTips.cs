using System.Collections.Concurrent;
using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Scripting;

namespace Hex.Dawn;

// Visual Studio's data tips. The pointer resting on a name while it's stopped says what it is, a moment after it stops
// moving so a sweep across the code doesn't ask about every word on the way. A local, or a member of one, is read out of
// the frame's variables kept from the first tip of the stop, which the debugger answers at once, everything else goes
// through the evaluator. A value with nothing inside it is said in the hover, one with members (an object, an array, a
// list) comes up in a little window of its own at the name, opened out a level, its rows opening further the way the
// Variables do, with a pin that keeps it up and Add Watch. It was two seconds of the evaluator looking through every
// using for a name that was a local, and a line of text with nothing to open.
internal sealed partial class HexDebugging
{
    // How long the pointer rests before a name is asked about, and how long it's off the name and the tip before the tip
    // goes
    private const int TIP_REST_MS = 120, TIP_LINGER_MS = 350;

    // How big the window of a tip with members is, in the chrome's units
    private const float TIP_WIDTH = 420.0f, TIP_HEIGHT = 250.0f;

    // The last data tip said and the one being asked, by the expression, the frame and the stop
    private (string Expression, int Frame, int Stop, string? Said) tipped;
    private (string? Expression, int Frame, int Stop) tipping;

    // Counted up for every name asked about, a rest that ends after another name was asked says nothing
    private int tipAsked;
    private int tipFrameGeneration;

    // The frame's variables by name and the variables inside each reference asked so far, kept for the stop they're of
    private (int Frame, int Stop) localsFor = (-1, -1);
    private Task<IReadOnlyList<DebugVariable>>? locals;
    private readonly ConcurrentDictionary<int, Task<IReadOnlyList<DebugVariable>>> insides = new();

    // The tip with members up now, what it's of, where its name is, and whether it's pinned
    private WindowPanel? tipWindow;
    private TreeView? tipTree;
    private string tipExpression = string.Empty;
    private UIRect tipName;
    private CodeEditor? tipEditor;
    private int tipVersion;
    private bool tipPinned;

    // When the pointer went off the tip and its name, in the tick count's milliseconds, 0 while it's on them
    private long tipAwaySince;

    /// <summary>
    /// A data tip, what the name under the pointer comes to while it's stopped, before the language server's word on it.
    /// Null while it's asked and for anything the debugger can't say, the server's hover is shown then. Empty for a value
    /// with members, which has its window instead.
    /// </summary>
    private string? DataTip(HexDocument of, CodeEditor editor, UITextPosition at)
    {
        if (!DebugStopped || debugging is not { } session || stoppedFrames.Count == 0 || (!SupportsDebugging(of.Path) && !IsCurrentDebuggerSource(of)) || at.Line >= editor.Document.LineCount)
            return null;

        string expression = ExpressionAt(editor.Document.LineAt(at.Line), at.Column);
        if (expression.Length == 0)
            return null;

        int frame = stoppedFrames[stoppedFrame].Id;
        if (tipped.Said is not null && (tipped.Expression, tipped.Frame, tipped.Stop) == (expression, frame, stops))
            return tipped.Said.Length > 0 ? tipped.Said : tipWindow is not null && tipExpression == expression ? string.Empty : null;

        if (tipping != (expression, frame, stops))
        {
            tipping = (expression, frame, stops);
            int asked = Interlocked.Increment(ref tipAsked);
            int stop = stops;
            string? file = stoppedFrames[stoppedFrame].File;
            int column = Math.Max(0, editor.Document.LineAt(at.Line).LastIndexOf(expression, Math.Min(at.Column + expression.Length, editor.Document.LineAt(at.Line).Length - 1), StringComparison.Ordinal));
            (Vector2 Start, Vector2 End) name = (editor.PlaceOf(new UITextPosition(at.Line, column)), editor.PlaceOf(new UITextPosition(at.Line, column + expression.Length)));
            Task<IReadOnlyList<DebugVariable>> frameLocals = LocalsOf(session, frame);
            _ = Task.Run(async () =>
            {
                await Task.Delay(TIP_REST_MS);
                if (Volatile.Read(ref tipAsked) != asked)
                    return;

                DebugVariable? value = await FromLocals(session, expression, frameLocals) ?? (await Evaluate(session, expression, frame, file, "hover")).Value;
                debugWork.Enqueue(() =>
                {
                    if (stop != stops || !DebugStopped || !ReferenceEquals(debugging, session) || Volatile.Read(ref tipAsked) != asked)
                        return;

                    // A function comes to its address, which says less than the server's word on what it does
                    bool tells = value is { } said && said.Type?.Contains('(') != true;
                    bool opens = tells && value!.Value.Reference > 0;
                    tipped = (expression, frame, stop, tells && !opens ? TipOf(value!.Value with { Name = expression }) : string.Empty);
                    if (opens)
                        OpenTip(session, editor, value!.Value with { Name = expression }, name.Start, name.End);
                    if (tells)
                        editor.AskAgain();
                });
            });
        }

        return null;
    }

    // The variables of a frame, asked for once a stop, the first scope's (its locals and arguments)
    private Task<IReadOnlyList<DebugVariable>> LocalsOf(DebugSession session, int frame)
    {
        if (localsFor == (frame, stops) && locals is not null)
            return locals;

        localsFor = (frame, stops);
        insides.Clear();
        return locals = Task.Run(async () =>
        {
            IReadOnlyList<DebugScope> scopes = await session.Scopes(frame);
            return scopes.Count > 0 ? await session.Variables(scopes[0].Reference) : (IReadOnlyList<DebugVariable>)[];
        });
    }

    /// <summary>
    /// A name, or a chain of members of one, read out of the frame's variables and what's inside them, null for one that
    /// isn't there (a type, a static, anything the evaluator has to work out). A name not among the locals is looked for
    /// in this, the way C# finds a field.
    /// </summary>
    private async Task<DebugVariable?> FromLocals(DebugSession session, string expression, Task<IReadOnlyList<DebugVariable>> frameLocals)
    {
        try
        {
            string[] names = expression.Split('.');
            IReadOnlyList<DebugVariable> at = await frameLocals;
            DebugVariable? found = Named(at, names[0]);
            if (found is null && names[0] != "this" && Named(at, "this") is { Reference: > 0 } self)
                found = Named(await InsideOf(session, self.Reference), names[0]);

            for (int i = 1; i < names.Length && found is { Reference: > 0 } outer; i++)
                found = Named(await InsideOf(session, outer.Reference), names[i]);
            return found;
        }
        catch (Exception)
        {
            // the debugger couldn't say, the evaluator's asked instead
            return null;
        }
    }

    private Task<IReadOnlyList<DebugVariable>> InsideOf(DebugSession session, int reference) =>
        insides.GetOrAdd(reference, _ => session.Variables(reference));

    private static DebugVariable? Named(IReadOnlyList<DebugVariable> variables, string name)
    {
        foreach (DebugVariable variable in variables)
        {
            if (variable.Name == name)
                return variable;
        }

        return null;
    }

    // A value as a data tip says it, the expression and what it is with its type under it
    private static string TipOf(DebugVariable value) =>
        value.Type is { Length: > 0 } type ? $"**{value.Name}** = {value.Value}\n\n{type}" : $"**{value.Name}** = {value.Value}";

    /* The window of a tip with members */

    /// <summary>
    /// A value with members shown in a tip of its own at its name, the value opened out a level, the rows opening further
    /// as the Variables do. Pinned it stays where it is until it's closed, and is evaluated again at every stop.
    /// </summary>
    private void OpenTip(DebugSession session, CodeEditor editor, DebugVariable value, Vector2 nameAt, Vector2 nameEnd)
    {
        if (tipPinned && tipWindow is not null)
            return;
        CloseTip();
        if (editor.Module is not { } module)
            return;

        UIModule chrome = context.Window.Chrome;
        Vector2 local = chrome.ToLocal(module.ToWorld(nameAt));
        float end = MathF.Max(local.X + 8.0f, chrome.ToLocal(module.ToWorld(nameEnd)).X);
        tipName = new UIRect(local - new Vector2(4.0f, 14.0f), new Vector2(end + 4.0f, local.Y + 14.0f));
        (tipEditor, tipVersion, tipExpression, tipPinned, tipAwaySince) = (editor, editor.Document.Version, value.Name, false, 0);

        var window = tipWindow = new WindowPanel(value.Name)
        {
            Icon = "symbol-variable",
            Anchor = Origin.Center,
            Pivot = Origin.TopLeft,
            Size = new Vector2(TIP_WIDTH, TIP_HEIGHT),
            MinSize = new Vector2(220.0f, 120.0f),
            Animated = false
        };
        context.Window.Dress(window);
        window.OnClosed = () =>
        {
            window.Parent?.Remove(window);
            if (ReferenceEquals(tipWindow, window))
                (tipWindow, tipTree, tipPinned) = (null, null, false);
        };
        window.AddTitleAction("pin", context.Strings.Said("debugging.pin_tip"), () => tipPinned = true);
        window.AddTitleAction("eye", context.Strings.Said("debugging.add_to_watch"), () => Watch(tipExpression));

        var tree = tipTree = window.Add(new TreeView
        {
            Anchor = Origin.TopLeft,
            Pivot = Origin.TopLeft,
            Size = new Vector2(TIP_WIDTH - 16.0f, TIP_HEIGHT - WindowPanel.TITLE_HEIGHT - 16.0f),
            Position = new Vector2(8.0f, -8.0f)
        });
        tree.OnExpanding = item => (item.Tag as SideTag)?.Expanding?.Invoke(item);
        tree.OnItemContextMenu = (item, _) => TipRowMenu(item);
        tree.Items = [TipRoot(session, value)];

        // Its top left just under the name, kept on the screen
        UIRect screen = chrome.Root.Bounds;
        Vector2 corner = new(Math.Clamp(local.X, screen.Min.X, screen.Max.X - TIP_WIDTH), MathF.Max(local.Y - 14.0f, screen.Min.Y + TIP_HEIGHT));
        window.Position = corner - screen.Center;
        chrome.Root.Add(window);
    }

    // The tip's row of the value itself, opened out with its members asked for at once
    private UITreeItem TipRoot(DebugSession session, DebugVariable value)
    {
        UITreeItem root = variables.Items(session, session.Variables)(value);
        root.Expanded = true;
        (root.Tag as SideTag)?.Expanding?.Invoke(root);
        return root;
    }

    // A right click on a row of a tip, its expression watched
    private void TipRowMenu(UITreeItem item)
    {
        string expression = TipPathOf(item);
        var menu = new Menu("data tip");
        menu.Add(context.Strings.Said("debugging.add_to_watch"), () => Watch(expression), icon: "eye");
        menu.Add(context.Strings.Said("debugging.pin_tip"), () => tipPinned = true, icon: "pin");
        context.Windows.Menu(menu);
    }

    /// <summary>The expression of a row of a tip, its name after its parents', an element's [i] straight after.</summary>
    internal static string TipPath(IReadOnlyList<string> names)
    {
        var path = new System.Text.StringBuilder(names.Count > 0 ? names[0] : string.Empty);
        for (int i = 1; i < names.Count; i++)
            path.Append(names[i].StartsWith('[') ? names[i] : "." + names[i]);
        return path.ToString();
    }

    private static string TipPathOf(UITreeItem item)
    {
        if (item.Tag is SideTag { DebugExpression: { Length: > 0 } expression }) return expression;
        var names = new List<string>();
        for (UITreeItem? at = item; at is not null; at = at.Parent)
        {
            int equals = at.Text.IndexOf(" = ", StringComparison.Ordinal);
            names.Insert(0, equals >= 0 ? at.Text[..equals] : at.Text);
        }

        return TipPath(names);
    }

    /// <summary>
    /// Every tick while a tip is up, gone once the pointer has been off both it and its name for a moment, the text
    /// changed under it, or it went on from the stop. A pinned one stays.
    /// </summary>
    private void KeepTip()
    {
        if (tipWindow is not { } window || tipPinned)
            return;

        UIModule chrome = context.Window.Chrome;
        Vector2 pointer = chrome.ToLocal(chrome.Compositor.Pointer.Position);
        bool over = window.Bounds.Shrink(new UIEdges(-6.0f)).Contains(pointer) || tipName.Contains(pointer);
        long now = Environment.TickCount64;
        if (over)
            tipAwaySince = 0;
        else if (tipAwaySince == 0)
            tipAwaySince = now;

        if ((tipAwaySince != 0 && now - tipAwaySince > TIP_LINGER_MS) || !DebugStopped || tipEditor?.Document.Version != tipVersion)
            CloseTip();
    }

    // The tip that's up taken down, a pinned one too
    private void CloseTip()
    {
        WindowPanel? window = tipWindow;
        (tipWindow, tipTree, tipPinned) = (null, null, false);
        window?.Parent?.Remove(window);
    }

    /// <summary>A pinned tip evaluated again where it's stopped now, its rows those of the new stop.</summary>
    private void RefreshPinnedTip()
    {
        if (!tipPinned || tipTree is not { } tree || debugging is not { } session || stoppedFrames.Count == 0)
            return;

        DebugFrame frame = stoppedFrames[stoppedFrame];
        string expression = tipExpression;
        int stop = stops;
        int generation = tipFrameGeneration;
        _ = Task.Run(async () =>
        {
            DebugEvaluation evaluated = await Evaluate(session, expression, frame.Id, frame.File, "hover");
            debugWork.Enqueue(() =>
            {
                if (stop != stops || !DebugStopped || !ReferenceEquals(debugging, session)
                    || generation != tipFrameGeneration || !ReferenceEquals(tipTree, tree))
                    return;
                tree.Items = evaluated.Value is { } value
                    ? [TipRoot(session, value with { Name = expression })]
                    : [new UITreeItem($"{expression} = ?", "symbol-variable") { Detail = evaluated.Error ?? context.Strings.Said("debugging.not_here"), Color = context.Panels.Dim }];
            });
        });
    }

    private void RetireFrameTips()
    {
        tipFrameGeneration++;
        Interlocked.Increment(ref tipAsked);
        tipped = default;
        tipping = default;
        locals = null;
        insides.Clear();
        if (!tipPinned) CloseTip();
    }
}
