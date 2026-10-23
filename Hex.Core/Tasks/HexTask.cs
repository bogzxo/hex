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

    [HidlComment("Another task of this file run first, this one only once that worked. The run task names the build one.")]
    public string DependsOn { get; set; } = string.Empty;

    [HidlComment("Where it runs, \"panel\" for a terminal tab under the editor, \"dusk\" for a Dusk window of its own.")]
    public string Terminal { get; set; } = PANEL;

    /// <summary>What <see cref="Terminal"/> says for a tab under the editor, and for a Dusk window.</summary>
    public const string PANEL = "panel", DUSK = "dusk";

    /// <summary>What it is called, its key in the file.</summary>
    [HidlIgnore]
    public string Name { get; set; } = string.Empty;

    /// <summary>The folder of the tasks.hor it came out of.</summary>
    [HidlIgnore]
    public string Root { get; set; } = string.Empty;

    /// <summary>Where it runs, the folder made whole.</summary>
    public string WorkingFolder() => Path.GetFullPath(Path.Combine(Root, Folder));
}
