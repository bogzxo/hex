using System.Runtime.InteropServices;

namespace Hex.Shell;

/// <summary>Which of Hex's two modes a process is, Dawn the editor or Dusk the terminal.</summary>
public enum HexMode
{
    Dawn,
    Dusk
}

/// <summary>
/// Who the process is to Windows. Dawn and Dusk are one exe, and Windows would group their windows under one taskbar
/// button and pin them as one, so each says an AppUserModelID of its own before its window is made, and they are two
/// programs to the taskbar.
/// </summary>
public static partial class HexIdentity
{
    /// <summary>The icon of the windows of both modes, Hex's own, Dusk is no other program for having a terminal in it.</summary>
    public const string ICON = "Assets/hex_icon.png";

    /// <summary>The mode the process was started in, Dawn until <see cref="Claim"/> says otherwise.</summary>
    public static HexMode Mode { get; private set; } = HexMode.Dawn;

    /// <summary>The id Windows knows a mode by.</summary>
    public static string AppIdOf(HexMode mode) => mode == HexMode.Dusk ? "bogzxo.Hex.Dusk" : "bogzxo.Hex.Dawn";

    /// <summary>Says which mode this is, to the program and to Windows. Before the window is made.</summary>
    public static void Claim(HexMode mode)
    {
        Mode = mode;
        if (OperatingSystem.IsWindows())
            SetCurrentProcessExplicitAppUserModelID(AppIdOf(mode));
    }

    /// <summary>The mode the command line asks for, Dusk for --dusk or an exe called dusk, Dawn otherwise.</summary>
    public static HexMode ModeOf(string[] args, string? processPath) =>
        args.Contains("--dusk", StringComparer.OrdinalIgnoreCase)
        || processPath is not null && Path.GetFileNameWithoutExtension(processPath).Equals("dusk", StringComparison.OrdinalIgnoreCase)
            ? HexMode.Dusk
            : HexMode.Dawn;

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetCurrentProcessExplicitAppUserModelID(string id);
}
