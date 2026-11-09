using System.Text.RegularExpressions;

using Horizon.HIDL;
using Horizon.HIDL.Runtime;

namespace Hex.Dawn;

// What a task that failed said it wanted, offered as a fix to its tasks.hor. dmd linking one file of a program that
// imports others of its own says "use `-i` to compile imports". Set Up F5 used to write dmd "app.d" with no -i, and every
// program of two files failed to link with three unresolved externals and not a word from Hex on why.
internal sealed partial class HexTasks
{
    // dmd's own hint after an undefined reference, ldc's is the same one
    private const string IMPORTS_HINT = "use `-i` to compile imports";

    // A D compiler at the start of a command line, quoted or not, with its path or not
    private static readonly Regex DCompiler = new(@"^\s*(?:""[^""]*?(?:dmd|ldmd2|ldc2)(?:\.exe)?""|[^\s""]*?(?:dmd|ldmd2|ldc2)(?:\.exe)?)(?=\s|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // -i on its own or with a pattern (-i=std), not -I's import folders
    private static readonly Regex CompilesImports = new(@"(?:^|\s)-i(?:=\S*)?(?=\s|$)", RegexOptions.Compiled);

    /// <summary>A D compile line with -i put in after its compiler, null when it isn't one or it has it already.</summary>
    internal static string? WithImports(string command)
    {
        Match compiler = DCompiler.Match(command);
        if (!compiler.Success || CompilesImports.IsMatch(command))
            return null;

        int at = compiler.Index + compiler.Length;
        return command[..at] + " -i" + command[at..];
    }

    /// <summary>
    /// The tasks of the same file a task runs first, the one it depends on, the one that one depends on and so on, in the
    /// order they run. The run task depended on the build and the build on build_solution, and F5 only ever ran the build.
    /// </summary>
    internal List<HexTask> DependenciesOf(HexTask task)
    {
        if (task.DependsOn.Length == 0 || TasksFileIn(task.Root) is not { } file)
            return [];

        List<HexTask> tasks = TasksOf(file, task.Root);
        return ChainOf(task, name => tasks.Find(other => other.Name == name));
    }

    /// <summary>
    /// What a task depends on all the way down, the deepest first, by a lookup of the tasks by name. A name it has seen
    /// already ends it there, so a loop of tasks that depend on each other runs each of them once and not for ever.
    /// </summary>
    internal static List<HexTask> ChainOf(HexTask task, Func<string, HexTask?> named)
    {
        var chain = new List<HexTask>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { task.Name };
        for (HexTask at = task; at.DependsOn.Length > 0 && seen.Add(at.DependsOn) && named(at.DependsOn) is { } first; at = first)
            chain.Insert(0, first);
        return chain;
    }

    /// <summary>
    /// A task failed, what it wrote looked at for a fix its tasks.hor can take. The task or the nearest it depends on
    /// that compiles the D gets -i on a card with a button that puts it in and runs the task again.
    /// </summary>
    private void OfferFix(HexTask task, string said)
    {
        if (!said.Contains(IMPORTS_HINT, StringComparison.Ordinal))
            return;

        HexTask? compiles = WithImports(task.Command) is not null ? task
            : DependenciesOf(task).LastOrDefault(first => WithImports(first.Command) is not null);
        if (compiles is null || TasksFileIn(compiles.Root) is not { } file)
            return;

        context.Notices.Notify(context.Strings.Said("tasks.needs_imports", ("name", compiles.Name)), error: true,
            (context.Strings.Said("tasks.add_imports"), () =>
            {
                if (AddImports(file, compiles))
                    Run(task);
            }));
    }

    /// <summary>
    /// -i put into a task's command in its tasks.hor, the rest of the file left as it was, comments and all. The file is
    /// opened instead when the command isn't written there the way HIDL writes it. Whether it went in.
    /// </summary>
    private bool AddImports(string file, HexTask compiles)
    {
        try
        {
            string was = HIDLWriter.Write(new StringValue(compiles.Command));
            string text = File.ReadAllText(file);
            if (WithImports(compiles.Command) is not { } command || !text.Contains(was, StringComparison.Ordinal))
            {
                context.Documents.Open(file);
                return false;
            }

            File.WriteAllText(file, text.Replace(was, HIDLWriter.Write(new StringValue(command)), StringComparison.Ordinal));
            tasksRead.Remove(file);
            context.Notices.Say(context.Strings.Said("tasks.added_imports", ("name", compiles.Name)));
            return true;
        }
        catch (IOException e)
        {
            context.Notices.Say(context.Strings.Said("tasks.file_not_written", ("file", TASKS_FILE), ("reason", e.Message)), error: true);
            return false;
        }
    }
}
