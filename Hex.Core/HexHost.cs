namespace Hex.Core;

/// <summary>
/// Where Hex is running and what that place lets it do. On a desktop it's everything, the folder its Assets are in is
/// the exe's and it can start any program it likes. On a phone or a tablet (Hex.Android) its files are unpacked into the
/// app's own folder and there are no other programs to start, so no shells, language servers, git, kernels or agents.
/// The head sets this up first thing, before the editor is made, and everything that would start a program or reach
/// another Hex asks it, and says why not when it can't, rather than failing somewhere deep down or pretending.
/// </summary>
public static class HexHost
{
    /// <summary>The folder Hex's own files are in (Assets, shaders, the built in extensions), the exe's on a desktop.</summary>
    public static string ProgramFolder { get; set; } = AppContext.BaseDirectory;

    /// <summary>What the place Hex runs on is called where a person reads it, "Android" on a tablet.</summary>
    public static string Name { get; set; } = "desktop";

    /// <summary>
    /// Whether Hex can start other programs at all, shells, language servers, git, compilers, kernels and agents. A tablet
    /// can, its own /system/bin/sh and toybox are there, it's the programs themselves that mostly aren't.
    /// </summary>
    public static bool Processes { get; set; } = true;

    /// <summary>
    /// Whether a program Hex downloads (a language server, a debugger, a runtime) can be run once it's downloaded.
    /// Android won't run anything an app wrote itself (no executing out of its own folders since Android 10) and the
    /// builds there are for desktop Linux anyway, so on a tablet nothing is downloaded to be run.
    /// </summary>
    public static bool RunsDownloads { get; set; } = true;

    /// <summary>Whether another Hex (or an MCP client) can reach this one over a named pipe.</summary>
    public static bool Pipes { get; set; } = true;

    /// <summary>Whether the desktop's own file dialogs are there (Windows' common item dialog, a portal on Linux).</summary>
    public static bool NativeDialogs { get; set; } = true;

    /// <summary>
    /// Picks a file to open when there are no native dialogs, the system's document picker on Android. The answer comes
    /// later, on whatever thread the host likes, so it's handed to the callback, with null when nothing was picked.
    /// Null when the host has no picker at all.
    /// </summary>
    public static Action<Action<string?>>? PickFile { get; set; }

    /// <summary>Picks a folder to open as the workspace when there are no native dialogs, see <see cref="PickFile"/>.</summary>
    public static Action<Action<string?>>? PickFolder { get; set; }

    /// <summary>
    /// Something the host has to tell whoever is using Hex, a document that couldn't be written back. From any thread,
    /// Dawn shows it as a notice on its next tick. Null until Dawn is up.
    /// </summary>
    public static Action<string>? Report { get; set; }

    /// <summary>
    /// Helper method to say why something can't be done here, for a toast or a status line. Null when it can, so a
    /// caller writes <c>if (HexHost.NoProcesses("git") is { } why) { say(why); return; }</c>.
    /// </summary>
    public static string? NoProcesses(string what) =>
        Processes ? null : $"{what} needs other programs to run, and Hex on {Name} can't start any.";

    /// <summary>Why a program can't be downloaded to be run here, null when it can, see <see cref="RunsDownloads"/>.</summary>
    public static string? NoDownloads(string what) =>
        RunsDownloads ? null : $"{what} would have to be downloaded and run, and Hex on {Name} can't run a program it downloaded. Android only runs the programs it ships with.";
}
