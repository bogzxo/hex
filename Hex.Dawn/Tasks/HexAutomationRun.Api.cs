using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

using Horizon.HIDL;
using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;

namespace Hex.Dawn;

// The hex an automation is handed. The extensions' own (HexExtensions.Api), every function of it done on the editor's
// thread with the automation's waiting, less what hands a function back to be called later (a command, a view, a menu,
// a dialog's answer), which has no extension to call. What an automation has that an extension hasn't is here, a program
// run and waited for (hex.run), lines printed, a question asked and its answer waited for, a text matched, a pause, and
// whether it was stopped. See docs/extensions.md.
internal sealed partial class HexAutomationRun
{
    // What an automation's hex doesn't have of an extension's, each hands back a function for later or is an extension's own
    private static readonly HashSet<string> NotForAutomations = new(StringComparer.Ordinal)
    {
        "command", "view", "panel", "window", "menu", "pick_file", "pick_folder", "watch", "setting", "set_setting", "title_note", "ask", "prompt"
    };

    // How long a program hex.run starts is waited for when the automation doesn't say, ten minutes
    private const float RUN_SECONDS = 600.0f;

    /// <summary>The hex of this run, the extensions' handed over to the editor's thread and the automation's own.</summary>
    private ObjectValue HexFor(ObjectValue editorHex)
    {
        var members = new Dictionary<string, IRuntimeValue>();
        foreach (var (name, value) in editorHex.Properties)
        {
            if (!NotForAutomations.Contains(name))
                members[name] = Marshalled(value, 0);
        }

        foreach (var (name, value) in Own())
            members[name] = value;
        return new ObjectValue(members);
    }

    /// <summary>
    /// A value of the editor's for this thread, a function called on the editor's thread, a value the host owns read and
    /// written there, and an object with either in it made again with them handed over too (what hex.document hands back).
    /// </summary>
    private IRuntimeValue Marshalled(IRuntimeValue value, int depth) => value switch
    {
        NativeFunctionValue native => new NativeFunctionValue((args, scope) => Marshalled(OnEditor(() => native.Callback(args, scope)), depth + 1)),
        NativeValue owned => new NativeValue(() => Marshalled(OnEditor(owned.AccessorCallback), depth + 1), set => OnEditor(() =>
        {
            owned.MutatorCallback(set);
            return true;
        })),
        ObjectValue made when depth < 4 && made.Properties.Values.Any(inside => inside is NativeFunctionValue or NativeValue) =>
            new ObjectValue(made.Properties.ToDictionary(pair => pair.Key, pair => Marshalled(pair.Value, depth + 1))),
        _ => value
    };

    /// <summary>What an automation's hex has of its own.</summary>
    private (string, IRuntimeValue)[] Own() =>
    [
        ("name", new StringValue(automation.Title)),
        ("folder", new StringValue(automation.Root)),
        ("run", Natives.Function("run", RunProgram)),
        ("print", Natives.Function("print", args =>
        {
            var parts = new List<string>();
            foreach (IRuntimeValue part in args.All)
                parts.Add(Values.Text(part));
            string line = string.Join(' ', parts);
            Print(line);
            return new StringValue(line);
        })),
        ("progress", Natives.Action("progress", args =>
        {
            if (args.Has(1))
                work.Report(args.Number(0), args.Text(1));
            else
                work.Report(args.Number(0));
        })),
        ("ask", Natives.Function("ask", args =>
        {
            ObjectValue asked = args.Object(0);
            string[] buttons = asked.Properties.TryGetValue("buttons", out IRuntimeValue? listed) && listed is ListValue list
                ? [.. list.Items.Select(item => item is StringValue text ? text.Value : Values.Text(item))]
                : ["OK", "Cancel"];
            var answered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            post(() => context.Windows.Ask(HexExtensions.Text(asked, "title") ?? automation.Title, HexExtensions.Text(asked, "text") ?? string.Empty,
                buttons, label => answered.TrySetResult(label)));
            return Then(args, new StringValue(Waited(answered.Task)));
        })),
        ("prompt", Natives.Function("prompt", args =>
        {
            ObjectValue asked = args.Object(0);
            var typed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            post(() => context.Windows.Prompt(HexExtensions.Text(asked, "title") ?? automation.Title, HexExtensions.Text(asked, "text") ?? string.Empty,
                HexExtensions.Text(asked, "value") ?? string.Empty, HexExtensions.Text(asked, "button") ?? context.Strings.Said("dusk_plugins.ok"),
                HexExtensions.Text(asked, "placeholder") ?? string.Empty, text => typed.TrySetResult(text)));
            return Then(args, new StringValue(Waited(typed.Task)));
        })),
        ("matches", Natives.Function("matches", args =>
        {
            try
            {
                return Values.Bool(Regex.IsMatch(args.Text(0), args.Text(1), RegexOptions.IgnoreCase | RegexOptions.Multiline, TimeSpan.FromSeconds(1)));
            }
            catch (ArgumentException e)
            {
                throw args.Wrong($"{args.Text(1)} isn't a pattern, {e.Message}");
            }
        })),
        ("sleep", Natives.Action("sleep", args =>
        {
            work.Token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(Math.Max(0.0f, args.Number(0))));
            work.ThrowIfCancelled();
        })),
        ("cancelled", Natives.Function("cancelled", _ => Values.Bool(work.Token.IsCancellationRequested)))
    ];

    // An answer handed to the function after it as well when there is one, the way an extension's ask has it, then back
    private IRuntimeValue Then(Arguments args, IRuntimeValue answer)
    {
        if (args.Has(1) && Values.IsFunction(args[1]))
            runtime.Invoke(args[1], hex, answer);
        return answer;
    }

    /// <summary>
    /// hex.run(program, [arguments], folder, { timeout, quiet }), a program run and waited for, what it came to handed back
    /// as { exit, output }, what it wrote and said wrong in one. The folder is from the tasks.hor's, the automation's own
    /// when it doesn't say. Every line it writes is on the automation's card as it comes unless it's quiet. It's killed
    /// when the automation is stopped and after the timeout, ten minutes when it doesn't say. Git is told not to ask for
    /// a password on a terminal there isn't.
    /// </summary>
    private IRuntimeValue RunProgram(Arguments args)
    {
        string program = args.Text(0);
        string[] arguments = args.Has(1) ? [.. args.List(1).Items.Select(item => item is StringValue text ? text.Value : Values.Text(item))] : [];
        string folder = args.Has(2) ? Path.GetFullPath(Path.Combine(automation.Root, args.Text(2))) : automation.WorkingFolder();
        ObjectValue options = args.Has(3) ? args.Object(3) : new ObjectValue();
        float seconds = options.Properties.TryGetValue("timeout", out IRuntimeValue? limit) && limit is NumberValue given ? given.Value : RUN_SECONDS;
        bool quiet = options.Properties.TryGetValue("quiet", out IRuntimeValue? hush) && hush is BooleanValue { Value: true };
        if (!Directory.Exists(folder))
            throw args.Wrong($"there's no folder {folder} to run {program} in.");

        var start = new ProcessStartInfo(program)
        {
            WorkingDirectory = folder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);

        var said = new StringBuilder();
        using var process = new Process { StartInfo = start };
        void Wrote(string? line)
        {
            if (line is null)
                return;
            lock (said)
                said.Append(line).Append('\n');
            if (!quiet)
                work.Write(line);
        }

        process.OutputDataReceived += (_, e) => Wrote(e.Data);
        process.ErrorDataReceived += (_, e) => Wrote(e.Data);
        try
        {
            process.Start();
        }
        catch (Exception e)
        {
            throw args.Wrong($"{program} didn't start, {e.Message}");
        }

        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var took = Stopwatch.StartNew();
        while (!process.WaitForExit(100))
        {
            if (!work.Token.IsCancellationRequested && took.Elapsed.TotalSeconds < seconds)
                continue;

            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // It ended by itself in the meantime
            }

            process.WaitForExit(2000);
            work.ThrowIfCancelled();
            throw args.Wrong($"{program} went on for more than {seconds:0} seconds and was stopped.");
        }

        // What it wrote last is only all in once the streams are done with, which waiting with no limit waits for
        process.WaitForExit();
        string output;
        lock (said)
            output = said.ToString().TrimEnd('\n', '\r');
        return Natives.Object(("exit", new NumberValue(process.ExitCode)), ("output", new StringValue(output)));
    }
}
