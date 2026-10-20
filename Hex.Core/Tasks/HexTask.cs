using Horizon.HIDL;

namespace Hex.Core;

/// <summary>
/// One task of a workspace's tasks.hor, a command line run in a terminal tab of its own, VS Code's tasks.json without the
/// json. What a compiler says in it goes in the Problems panel the way it does from any terminal, the host's matchers see
/// to that.
/// </summary>
internal sealed class HexTask
{
    [HidlComment("The command line, run by the usual shell.")]
    public string Command { get; set; } = string.Empty;

    [HidlComment("Where it runs, from the workspace's folder. The folder itself when it doesn't say.")]
    public string Folder { get; set; } = ".";

    [HidlComment("build, test or run. Ctrl+Shift+B runs the build one, F5 on a file that isn't HIDL the run one.")]
    public string Group { get; set; } = string.Empty;

    [HidlComment("Whether it is the one of its group run without asking, the first of a group is when none says.")]
    public bool Default { get; set; }

    /// <summary>What it is called, its key in the file.</summary>
    [HidlIgnore]
    public string Name { get; set; } = string.Empty;

    /// <summary>The folder of the tasks.hor it came out of.</summary>
    [HidlIgnore]
    public string Root { get; set; } = string.Empty;

    /// <summary>Where it runs, the folder made whole.</summary>
    public string WorkingFolder() => Path.GetFullPath(Path.Combine(Root, Folder));
}
