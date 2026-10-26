using System.Diagnostics.CodeAnalysis;

using Horizon.HIDL;
using Horizon.HIDL.Runtime;

namespace Hex.Core;

/// <summary>
/// One automation of a tasks.hor, a command of somebody's own in the command palette, Automation and its title. It is a
/// command line run in a terminal tab the way a task is, in the shell it says (bash is Git Bash on Windows), or HIDL that
/// drives Hex, a run function of the same file or a file of its own, handed the hex object extensions get and run off
/// the editor's thread for as long as it takes. They are the automations object of the file next to its tasks, each by
/// name with a title and a key, and a command, a run or a script. The tasks.hor of the workspace's folder has the ones
/// that push every repository of it.
/// </summary>
internal sealed class HexAutomation
{
    [HidlComment("What the command palette calls it, after the word Automation. Its key in the file when it doesn't say.")]
    public string Title { get; set; } = string.Empty;

    [HidlComment("A key it goes off on as well, written like Ctrl+Alt+P, none when empty.")]
    public string Key { get; set; } = string.Empty;

    [HidlComment("A command line run in a terminal tab of its own, the way a task is.")]
    public string Command { get; set; } = string.Empty;

    [HidlComment("The shell the command line runs in, bash (Git Bash on Windows), pwsh, powershell, cmd, the usual one when empty.")]
    public string Shell { get; set; } = string.Empty;

    [HidlComment("Where it runs, from the folder of this file. The folder itself when it doesn't say.")]
    public string Folder { get; set; } = ".";

    [HidlComment("A file of HIDL run with hex in it, from the folder of this file, its run(hex) called after when it has one.")]
    public string Script { get; set; } = string.Empty;

    /// <summary>What it is called, its key in the file.</summary>
    [HidlIgnore]
    public string Name { get; set; } = string.Empty;

    /// <summary>The folder of the tasks.hor it came out of.</summary>
    [HidlIgnore]
    public string Root { get; set; } = string.Empty;

    /// <summary>The tasks.hor it came out of.</summary>
    [HidlIgnore]
    public string File { get; set; } = string.Empty;

    /// <summary>Whether it has a run function of its own in the file.</summary>
    [HidlIgnore]
    public bool HasRun { get; set; }

    /// <summary>Whether it is HIDL rather than a command line.</summary>
    public bool IsHidl => Command.Length == 0;

    /// <summary>Where it runs, the folder made whole.</summary>
    public string WorkingFolder() => Path.GetFullPath(Path.Combine(Root, Folder));

    /// <summary>The file of HIDL it runs, made whole, empty for none.</summary>
    public string ScriptPath() => Script.Length == 0 ? string.Empty : Path.GetFullPath(Path.Combine(Root, Script));

    /// <summary>
    /// The automations of a file's automations object, in the order it has them. One with nothing to run (no command, no
    /// script and no run function) isn't one, and one with no title is called after its key, push_all is Push all.
    /// </summary>
    // HIDLSerializer reads an automation by its members, the trimmer of a published Hex has to be told to keep them
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors, typeof(HexAutomation))]
    public static List<HexAutomation> Read(ObjectValue said, string root, string file)
    {
        var automations = new List<HexAutomation>();
        foreach (var (name, value) in said.Properties)
        {
            if (value is not ObjectValue told)
                continue;

            var automation = new HexAutomation { Name = name, Root = root, File = file };
            HIDLSerializer.Populate(automation, told);
            automation.HasRun = told.Properties.TryGetValue("run", out IRuntimeValue? run) && Values.IsFunction(run);
            if (automation.Title.Trim().Length == 0)
                automation.Title = TitleOf(name);

            if (automation.Command.Length > 0 || automation.Script.Length > 0 || automation.HasRun)
                automations.Add(automation);
        }

        return automations;
    }

    /// <summary>A key of the file as a title, its underscores spaces and its first letter a capital.</summary>
    public static string TitleOf(string name)
    {
        string spaced = name.Replace('_', ' ').Trim();
        return spaced.Length == 0 ? name : char.ToUpperInvariant(spaced[0]) + spaced[1..];
    }
}
