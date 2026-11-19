using Horizon.HIDL;
using Horizon.HIDL.Runtime;

namespace Hex.Core;

/// <summary>
/// A program a language server runs on or is installed with (Node and npm, a .NET SDK, a JDK), from language_servers.hor's
/// tools. Written once and named by the servers that need it.
/// </summary>
/// <param name="Name">Its key in the file, node, dotnet.</param>
/// <param name="Title">What it's called on screen, "Node.js and npm".</param>
/// <param name="Command">The program that's looked for, this system's.</param>
/// <param name="Arguments">What makes it say its version.</param>
/// <param name="Min">The oldest version that will do, empty for any.</param>
/// <param name="Install">The command line that installs it on this system, empty for one that's downloaded by hand.</param>
/// <param name="Download">Where it's downloaded from by hand, empty for nowhere said.</param>
/// <param name="About">A sentence on what it's for.</param>
/// <param name="Needs">The tools it's installed with (cargo with rustup), empty for none.</param>
internal sealed record HexTool(string Name, string Title, string Command, string[] Arguments, string Min, string Install, string Download, string About, string[] Needs);

/// <summary>Which of the three a step is: what the machine needs, the server, or what the project needs.</summary>
internal enum SetupStepKind { Environment, Server, Project }

/// <summary>Where a step stands, each said on screen as an icon and a word.</summary>
internal enum SetupState
{
    /// <summary>Being looked for.</summary>
    Checking,
    /// <summary>There, and new enough.</summary>
    Ready,
    /// <summary>Not there, with a command that gets it.</summary>
    Needed,
    /// <summary>Not there, and not needed, it adds something.</summary>
    Optional,
    /// <summary>There, older than it should be.</summary>
    Outdated,
    /// <summary>Its command is running.</summary>
    Running,
    /// <summary>Waiting for its turn behind another step's command.</summary>
    Queued,
    /// <summary>Its command ended badly.</summary>
    Failed,
    /// <summary>Waiting for a step above it.</summary>
    Blocked,
    /// <summary>Not there, and downloaded by hand.</summary>
    Manual
}

/// <summary>
/// One thing to do (or done) for a language: a tool, the server, or something of the project, where it stands, and how
/// it's done, the command that's shown and run, or the page it's downloaded from.
/// </summary>
/// <param name="Key">tool:node, server:csharp, project:csharp:dotnet-restore.</param>
/// <param name="Detail">The version and where it is for one that's there, why for one that isn't.</param>
/// <param name="Command">The command line that does it, null for none.</param>
/// <param name="Download">The page it's downloaded from by hand, null for none.</param>
/// <param name="BlockedBy">What it waits for, the title of that step, null for nothing.</param>
/// <param name="Folder">Where its command runs, null for anywhere (an install).</param>
/// <param name="Name">The tool's, the server's or the project check's own name.</param>
internal sealed record SetupStep(string Key, SetupStepKind Kind, string Name, string Title, string About, SetupState State, string Detail,
    string? Command, string? Download, bool Optional, string? BlockedBy, string? Folder = null)
{
    /// <summary>Whether it still wants doing, for counting what's left: not there and not optional.</summary>
    public bool Left => !Optional && State is not SetupState.Ready;

    /// <summary>Whether its command can be run now.</summary>
    public bool Runnable => Command is { Length: > 0 } && State is SetupState.Needed or SetupState.Optional or SetupState.Outdated or SetupState.Failed;
}

/// <summary>What was found of a program on this machine, null path for not there.</summary>
internal sealed record SetupFound(string? Path, string? Version);

/// <summary>
/// What a project check found in the folder a server runs in. Applies false for a folder it isn't about (no CMakeLists.txt
/// for compile-commands), which has no step.
/// </summary>
internal sealed record ProjectFact(string Check, string Title, string About, bool Applies, bool Done, string Detail, string? Command, string Folder, bool Optional = false);

/// <summary>
/// A language's steps worked out from what's on the machine, what's running and what the project has. Nothing in here
/// looks at the machine itself, HexLanguages.Setup finds things and hands them over, so every state is tested on its own.
/// </summary>
internal static class HexServerSetup
{
    /// <summary>Reads the tools of language_servers.hor, by name. None when the file has none.</summary>
    public static Dictionary<string, HexTool> ReadTools(string text)
    {
        var found = new Dictionary<string, HexTool>(StringComparer.OrdinalIgnoreCase);
        HIDLRuntime runtime = new() { Output = null };
        var (success, _) = runtime.Evaluate(text);
        if (!success || runtime.UserScope.Lookup("tools") is not ObjectValue tools)
            return found;

        foreach (var (name, value) in tools.Properties)
        {
            if (value is not ObjectValue tool || Text(tool, "command") is not { Length: > 0 } command)
                continue;
            found[name] = new HexTool(name, Text(tool, "name") is { Length: > 0 } title ? title : name, command, Texts(tool, "args") ?? [],
                Text(tool, "min"), Text(tool, "install"), Text(tool, "download"), Text(tool, "about"), Texts(tool, "needs") ?? []);
        }

        return found;
    }

    private static string Text(ObjectValue of, string key) => of.Properties.GetValueOrDefault(key) switch
    {
        StringValue text => text.Value,
        ObjectValue bySystem => bySystem.Properties.GetValueOrDefault(HexServerCatalog.SystemName) is StringValue mine ? mine.Value : string.Empty,
        _ => string.Empty
    };

    private static string[]? Texts(ObjectValue of, string key) =>
        of.Properties.GetValueOrDefault(key) is ListValue list ? [.. list.Items.OfType<StringValue>().Select(item => item.Value)] : null;

    /// <summary>
    /// A language's steps in the order they're done: the tools (those the needed ones are installed with first, the
    /// optional ones last), the server, then what the project needs.
    /// </summary>
    /// <param name="tool">What was found of a tool by its name, null while it's being looked for.</param>
    /// <param name="server">What was found of the server, null while it's being looked for.</param>
    /// <param name="newer">A newer version of the server that's out, null for none known.</param>
    /// <param name="running">What's going on with a step's command by its key, Running, Queued or Failed, null for nothing.</param>
    public static List<SetupStep> StepsFor(HexServerEntry entry, IReadOnlyDictionary<string, HexTool> tools, Func<string, SetupFound?> tool,
        SetupFound? server, string? newer, Func<string, SetupState?> running, IReadOnlyList<ProjectFact> project, string releasesPage = "")
    {
        var steps = new List<SetupStep>();
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The tools, a tool's own needs ahead of it, the needed ones before the optional
        void AddTool(string name, bool optional)
        {
            if (!tools.TryGetValue(name, out HexTool? of) || !added.Add(name))
                return;
            foreach (string need in of.Needs)
                AddTool(need, optional);
            steps.Add(ToolStep(of, tool(name), optional, steps, running));
        }

        foreach (string name in entry.Tools ?? [])
            AddTool(name, optional: false);
        foreach (string name in entry.ToolsOptional ?? [])
            AddTool(name, optional: true);

        steps.Add(ServerStep(entry, server, newer, steps, running, releasesPage));

        foreach (ProjectFact fact in project.Where(fact => fact.Applies))
            steps.Add(ProjectStep(entry, fact, steps, running));

        return steps;
    }

    // The first step a step waits for, a needed tool that isn't ready yet, null for none
    private static string? Waiting(IEnumerable<SetupStep> before) =>
        before.FirstOrDefault(step => step.Kind == SetupStepKind.Environment && !step.Optional && step.State is not SetupState.Ready)?.Title;

    private static SetupStep ToolStep(HexTool tool, SetupFound? found, bool optional, List<SetupStep> before, Func<string, SetupState?> running)
    {
        string key = "tool:" + tool.Name;
        string? command = tool.Install.Length > 0 ? tool.Install : null;
        string? download = tool.Download.Length > 0 ? tool.Download : null;
        SetupState state;
        string detail;

        if (found is null)
        {
            state = SetupState.Checking;
            detail = string.Empty;
        }
        else if (found.Path is null)
        {
            string? waits = tool.Needs.Length > 0 ? before.FirstOrDefault(step => tool.Needs.Contains(step.Name, StringComparer.OrdinalIgnoreCase) && step.State is not SetupState.Ready)?.Title : null;
            state = waits is not null ? SetupState.Blocked : command is null ? SetupState.Manual : optional ? SetupState.Optional : SetupState.Needed;
            detail = waits ?? string.Empty;
            return Overridden(new SetupStep(key, SetupStepKind.Environment, tool.Name, tool.Title, tool.About, state, detail, command, download, optional, waits), running);
        }
        else if (tool.Min.Length > 0 && found.Version is { } version && HexServerCatalog.IsNewer(tool.Min, version) && !HexServerCatalog.Same(tool.Min, version))
        {
            state = command is null ? SetupState.Manual : SetupState.Outdated;
            detail = version;
        }
        else
        {
            state = SetupState.Ready;
            detail = found.Version ?? string.Empty;
        }

        return Overridden(new SetupStep(key, SetupStepKind.Environment, tool.Name, tool.Title, tool.About, state, detail, command, download, optional, null), running);
    }

    private static SetupStep ServerStep(HexServerEntry entry, SetupFound? found, string? newer, List<SetupStep> before, Func<string, SetupState?> running, string releasesPage)
    {
        string key = "server:" + entry.Name;
        string? download = releasesPage.Length > 0 ? releasesPage : null;
        if (found is null)
            return Overridden(new SetupStep(key, SetupStepKind.Server, entry.Name, entry.Command, entry.About, SetupState.Checking, string.Empty, NonEmpty(entry.Install), download, false, null), running);

        if (found.Path is null)
        {
            string? waits = Waiting(before);
            SetupState state = waits is not null ? SetupState.Blocked : entry.Install.Length == 0 ? SetupState.Manual : SetupState.Needed;
            return Overridden(new SetupStep(key, SetupStepKind.Server, entry.Name, entry.Command, entry.About, state, waits ?? string.Empty, NonEmpty(entry.Install), download, false, waits), running);
        }

        if (newer is not null)
            return Overridden(new SetupStep(key, SetupStepKind.Server, entry.Name, entry.Command, entry.About, SetupState.Outdated,
                found.Version ?? string.Empty, NonEmpty(entry.Update), download, false, null), running);

        string detail = found.Version is { } version ? $"{version}  {found.Path}" : found.Path;
        return Overridden(new SetupStep(key, SetupStepKind.Server, entry.Name, entry.Command, entry.About, SetupState.Ready, detail, NonEmpty(entry.Update), download, false, null), running);
    }

    private static SetupStep ProjectStep(HexServerEntry entry, ProjectFact fact, List<SetupStep> before, Func<string, SetupState?> running)
    {
        string key = $"project:{entry.Name}:{fact.Check}";
        if (fact.Done)
            return Overridden(new SetupStep(key, SetupStepKind.Project, fact.Check, fact.Title, fact.About, SetupState.Ready, fact.Detail, fact.Command, null, fact.Optional, null, fact.Folder), running);

        // What a project's command runs with is a tool of the language's (dotnet restore, cmake), it waits for those
        string? waits = Waiting(before);
        SetupState state = waits is not null ? SetupState.Blocked : fact.Command is null ? SetupState.Manual : fact.Optional ? SetupState.Optional : SetupState.Needed;
        return Overridden(new SetupStep(key, SetupStepKind.Project, fact.Check, fact.Title, fact.About, state, waits ?? fact.Detail, fact.Command, null, fact.Optional, waits, fact.Folder), running);
    }

    // What's going on with a step's command wins over what was found of it, a step being installed is being installed
    private static SetupStep Overridden(SetupStep step, Func<string, SetupState?> running) =>
        running(step.Key) is { } going && (going != SetupState.Failed || step.State is not SetupState.Ready) ? step with { State = going } : step;

    private static string? NonEmpty(string text) => text.Length > 0 ? text : null;

    /// <summary>How many steps of a language still want doing, the optional ones not counted.</summary>
    public static int LeftOf(IEnumerable<SetupStep> steps) => steps.Count(step => step.Left);

    /// <summary>
    /// The steps that can be run now in the order they'd be done, for Set up everything. It goes as far as it can and stops
    /// at the first that needs somebody (a download), steps behind a blocked one waiting for it to be done.
    /// </summary>
    public static List<SetupStep> RunnableInOrder(IEnumerable<SetupStep> steps)
    {
        var going = new List<SetupStep>();
        foreach (SetupStep step in steps.Where(step => step.Left))
        {
            if (step.State == SetupState.Manual)
                break;
            if (step.Runnable)
                going.Add(step);
        }

        return going;
    }
}
