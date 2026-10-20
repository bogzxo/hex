using System.Diagnostics;

using Horizon.HIDL;
using Horizon.HIDL.Parsing;
using Horizon.HIDL.Runtime;

namespace Hex.Core;

/// <summary>What a file came to when it was run, or why it didn't.</summary>
/// <param name="Runtime">The runtime it ran in, what it declared is still in there for the console to use.</param>
/// <param name="Declared">What it declared, in the order it did.</param>
/// <param name="Error">What went wrong, null when nothing did.</param>
/// <param name="Milliseconds">How long the run took.</param>
/// <param name="Printed">What it printed while it ran.</param>
/// <param name="Sources">Where every value it made was written, see HIDLSourceMap.</param>
/// <param name="ErrorAt">Where what went wrong went wrong, nowhere if that isn't known.</param>
internal sealed record HexValues(HIDLRuntime Runtime, IReadOnlyList<(string Name, IRuntimeValue Value)> Declared, string? Error, double Milliseconds,
    IReadOnlyList<string> Printed, HIDLSourceMap Sources, SourceLocation ErrorAt);

/// <summary>
/// Runs a file of HIDL off the editor's thread, the way a game would load it, so what it comes to can be shown while it
/// is typed. One run at a time, a newer one cancels whatever is still going, and a run that goes on too long is stopped.
/// Also runs the lines of the console in what the last run left behind, the same way, one at a time.
/// </summary>
internal sealed class HexEvaluation
{
    // How long a run gets before it is stopped. Anything a game loads is done in a few milliseconds
    private const int TIME_LIMIT_MS = 1000;

    // A thread of its own with room for the interpreter, a deep file goes a long way down the stack
    private const int STACK_SIZE = 16 * 1024 * 1024;

    private CancellationTokenSource? running;
    private volatile HexValues? finished;
    private volatile string? answered;
    private volatile bool answering;

    /// <summary>Whether a line of the console is being run right now, in a runtime the editor may be showing the values of.</summary>
    public bool Answering => answering;

    /// <summary>
    /// Runs a file, includes looked for next to it and read out of <paramref name="open"/> first (what is open in the
    /// editor and maybe not saved). What it comes to turns up in <see cref="TakeFinished"/>.
    /// </summary>
    public void Run(string source, string? file, string? directory, IReadOnlyDictionary<string, string> open)
    {
        running?.Cancel();
        var cancel = new CancellationTokenSource();
        running = cancel;

        var thread = new Thread(() => Evaluate(source, file, directory, open, cancel), STACK_SIZE)
        {
            IsBackground = true,
            Name = "Hex evaluation"
        };
        thread.Start();
    }

    /// <summary>The last run that finished since this was last asked, null if none did.</summary>
    public HexValues? TakeFinished() => Interlocked.Exchange(ref finished, null);

    private void Evaluate(string source, string? file, string? directory, IReadOnlyDictionary<string, string> open, CancellationTokenSource cancel)
    {
        var printed = new List<string>();
        var sources = new HIDLSourceMap();
        var runtime = new HIDLRuntime
        {
            BaseDirectory = directory,
            Output = printed.Add,
            IncludeReader = (included, folder) => ReadInclude(included, folder, open),
            Sources = sources,
            SourceFile = file
        };

        runtime.Interpreter.Cancellation = cancel.Token;
        cancel.CancelAfter(TIME_LIMIT_MS);

        long started = Stopwatch.GetTimestamp();
        string? error = null;
        SourceLocation errorAt = default;
        try
        {
            runtime.Run(source);
        }
        catch (HidlRuntimeException e)
        {
            error = e.Reason;
            if (e.Line > 0)
                errorAt = new SourceLocation(e.File, new SourceSpan(e.Line, e.Column, 0, e.Length));
        }
        catch (ParseException e)
        {
            // The syntax check underlines these already
            error = e.Message;
        }
        catch (Exception e)
        {
            error = e.Message;
        }

        double took = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        // A newer run is on its way, nobody wants this one. One that ran out of time says so
        if (running != cancel)
            return;

        if (error is not null && took >= TIME_LIMIT_MS)
            error = "stopped after a second, it was still going";
        runtime.Interpreter.Cancellation = CancellationToken.None;

        var declared = new List<(string, IRuntimeValue)>();
        foreach (var (name, value) in runtime.UserScope.Variables)
            declared.Add((name, value));

        finished = new HexValues(runtime, declared, error, took, printed, sources, errorAt);
    }

    private static string ReadInclude(string file, string? folder, IReadOnlyDictionary<string, string> open)
    {
        string full = Path.GetFullPath(Path.IsPathRooted(file) || folder is null ? file : Path.Combine(folder, file));
        if (open.TryGetValue(full, out string? text))
            return text;

        return File.Exists(full) ? File.ReadAllText(full) : throw new HidlRuntimeException($"There is no '{file}' to include.");
    }

    /// <summary>
    /// Runs a line of the console in what a run left behind, off the editor's thread, after whatever of
    /// <paramref name="before"/> the runtime hasn't seen (what the console declared before the file was run again). What
    /// it came to turns up in <see cref="TakeAnswer"/>. False if the last line is still going.
    /// </summary>
    public bool Ask(HIDLRuntime runtime, IReadOnlyList<string> before, string line, Action<string> printed)
    {
        if (answering || answered is not null)
            return false;

        answering = true;
        var thread = new Thread(() =>
        {
            var cancel = new CancellationTokenSource(TIME_LIMIT_MS);
            Action<string>? output = runtime.Output;
            string? file = runtime.SourceFile;
            runtime.Output = printed;

            // A line of the console isn't a line of the file, an error in it says the line of the console
            runtime.SourceFile = null;
            runtime.Interpreter.Cancellation = cancel.Token;

            string said;
            try
            {
                foreach (string declared in before)
                {
                    try { runtime.Run(Finished(declared)); }
                    catch (Exception) { /* it was fine in the old one, the file may have changed under it since */ }
                }

                IRuntimeValue value = runtime.Run(Finished(line));
                said = Describe(value);
            }
            catch (Exception e)
            {
                said = "\u0001" + (cancel.IsCancellationRequested ? "stopped after a second, it was still going" : e.Message);
            }
            finally
            {
                runtime.Interpreter.Cancellation = CancellationToken.None;
                runtime.Output = output;
                runtime.SourceFile = file;
            }

            // Done before the answer is handed over, whoever takes it may want the runtime straight away
            answering = false;
            answered = said;
        }, STACK_SIZE)
        {
            IsBackground = true,
            Name = "Hex console"
        };
        thread.Start();
        return true;
    }

    /// <summary>
    /// What the last line of the console came to since this was last asked, null if it isn't done. An error starts with
    /// a \u0001, see <see cref="IsError"/>.
    /// </summary>
    public string? TakeAnswer() => Interlocked.Exchange(ref answered, null);

    /// <summary>A line as a statement, nobody types the semicolon at the end of a line of a console.</summary>
    private static string Finished(string line)
    {
        string trimmed = line.TrimEnd();
        return trimmed.EndsWith(';') || trimmed.EndsWith('}') ? trimmed : trimmed + ";";
    }

    public static bool IsError(string answer) => answer.Length > 0 && answer[0] == '\u0001';

    /// <summary>A value as the source that would make it, or what it is when there is no writing it.</summary>
    public static string Describe(IRuntimeValue value)
    {
        try
        {
            return HIDLWriter.CanWrite(value) ? HIDLWriter.Write(value) : Values.Text(value);
        }
        catch (Exception)
        {
            return Values.Text(value);
        }
    }
}
