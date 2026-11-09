using System.Globalization;

using Horizon.UI.Components;
using Horizon.UI.Scripting;

namespace Hex.Dawn;

// How a debugged program stopped on an exception and how it ended, the way Visual Studio says them. An exception it
// stopped on says its type and message along the bottom, its stack in the output, and has a section of its own at the
// top of the Debug side with $exception to open. The end always says the exit code, as the program had it where that can
// be read (netcoredbg says 0 for one an unhandled exception ended), and why it ended when the debugger went away. It
// said "Horizon.Testing ended" and nothing else, the pipe to netcoredbg broken by a line of the program's output.
internal sealed partial class HexDebugging
{
    // The .NET exception it's stopped on, null while it's stopped on anything else
    private DebugException? stoppedOn;

    // Whether Shift+F5 ended it, said as stopped rather than as ended
    private bool stoppingByHand;

    /// <summary>What it ended with said in the output and along the bottom, the exit code always.</summary>
    private void SayEnded(DebugEvent happened)
    {
        if (debugging?.TerminatesDebuggeeOnStop == false && stoppingByHand && happened.Text.Length == 0)
        {
            context.Notices.Say(context.Strings.Said("debugging.detached", ("process", debuggedProject)));
            return;
        }
        string project = Path.GetFileNameWithoutExtension(debuggedProject);
        string code = ExitCodeText(happened.ExitCode);
        bool known = happened.ExitCode != -1;
        string said = happened.Text is { Length: > 0 } why
            ? context.Strings.Said(known ? "debugging.ended_lost_code" : "debugging.ended_lost", ("project", project), ("why", why), ("code", code))
            : stoppingByHand
                ? context.Strings.Said(known ? "debugging.stopped_code" : "debugging.stopped_by_hand", ("project", project), ("code", code))
                : endedByException
                    ? context.Strings.Said("debugging.ended_exception", ("project", project), ("code", code))
                    : known
                        ? context.Strings.Said("debugging.ended_code", ("project", project), ("code", code))
                        : context.Strings.Said("debugging.ended_unknown", ("project", project));
        context.Notices.Say(said, error: happened.Text.Length > 0 || (known && happened.ExitCode != 0 && !stoppingByHand));
    }

    /// <summary>An exit code the way Visual Studio says it, the hex of it as well when it's an HRESULT or a negative one.</summary>
    internal static string ExitCodeText(int code) =>
        code < 0 || code > 0xFFFF
            ? $"{code.ToString(CultureInfo.InvariantCulture)} (0x{code:x8})"
            : code.ToString(CultureInfo.InvariantCulture);

    /// <summary>An exception it stopped on said in the output, its type, message and stack, the way Visual Studio's output has it.</summary>
    private void SayException(DebugException? thrown)
    {
        if (thrown is null)
            return;

        context.Notices.Note(context.Strings.Said(thrown.Unhandled ? "debugging.exception_unhandled" : "debugging.exception_thrown", ("type", thrown.Type), ("message", thrown.Message)), error: true);
        if (thrown.StackTrace is { Length: > 0 } stack)
        {
            foreach (string line in stack.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
                context.Notices.Note(line, error: true);
        }
    }

    /// <summary>The exception it's stopped on at the top of the Debug side, its type and message, and $exception to open.</summary>
    private UITreeItem ExceptionSection(DebugException thrown)
    {
        var section = new UITreeItem(context.Strings.Said(thrown.Unhandled ? "debugging.exception_unhandled_title" : "debugging.exception_title"), "error")
        {
            IconColor = context.Panels.Error,
            Expandable = true,
            Expanded = true,
            Children =
            [
                new UITreeItem(thrown.Type, "symbol-class") { Detail = thrown.Message, Tooltip = $"{thrown.Type}\n{thrown.Message}" }
            ]
        };

        if (thrown.StackTrace is { Length: > 0 } stack)
        {
            var trace = new UITreeItem(context.Strings.Said("debugging.exception_stack"), "debug-stackframe-dot") { Expandable = true, Children = [] };
            foreach (string line in stack.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
                trace.Children.Add(new UITreeItem(line.Trim()) { Color = context.Panels.Dim, Tooltip = line.Trim() });
            section.Children.Add(trace);
        }

        // $exception opened out like a variable, the debugger's own name for it
        if (!watches.Contains("$exception"))
            section.Children.Add(new UITreeItem(context.Strings.Said("debugging.exception_watch"), "eye") { Color = context.Panels.Dim, Tag = new SideTag(Pressed: () => Watch("$exception")) });
        return section;
    }
}
