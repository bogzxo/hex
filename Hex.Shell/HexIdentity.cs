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
    /// <summary>
    /// The icon of the windows of both modes, Hex's own, Dusk is no other program for having a terminal in it. The nightly
    /// build's is grey with a yellow H, so it's never taken for the stable one beside it, and a Debug build's all grey, the
    /// Hex being worked on told from the one it's worked on in.
    /// </summary>
    public static string ICON { get; } =
#if DEBUG
        "Assets/hex_icon_gray.png";
#else
        HexVersion.Dev ? "Assets/hex_icon_dev.png" : "Assets/hex_icon.png";
#endif

    /// <summary>
    /// What the window shows before Hex draws its first frame, the ground of its dark look, so a start or a wake goes from
    /// this straight to Hex rather than through the engine's picture.
    /// </summary>
    public static readonly System.Numerics.Vector3 LOADING_COLOR = new(0.094f, 0.090f, 0.122f);

    /// <summary>The mode the process was started in, Dawn until <see cref="Claim"/> says otherwise.</summary>
    public static HexMode Mode { get; private set; } = HexMode.Dawn;

    // Which build this is to the system, so a Debug Hex's windows (and a nightly's) are a program of their own beside the
    // installed one: Windows grouped the Hex being worked on with the one it's worked in under one taskbar button, which
    // of the two came up on a click a toss-up. Nothing for the stable build, whose ids pins and shortcuts already know
    private static string BuildSuffix { get; } =
#if DEBUG
        "Debug";
#else
        HexVersion.Dev ? "Dev" : string.Empty;
#endif

    /// <summary>The id Windows knows a mode by, a Debug or nightly build's its own.</summary>
    public static string AppIdOf(HexMode mode) =>
        (mode == HexMode.Dusk ? "bogzxo.Hex.Dusk" : "bogzxo.Hex.Dawn") + (BuildSuffix.Length > 0 ? "." + BuildSuffix : string.Empty);

    /// <summary>
    /// The id a mode's windows go by on Linux, X11's WM_CLASS and Wayland's app id (hex-dawn, hex-dusk), the desktop
    /// entry's StartupWMClass. A Debug or nightly build's has its own ending, so it isn't grouped with the installed Hex.
    /// </summary>
    public static string WindowIdOf(HexMode mode) =>
        (mode == HexMode.Dusk ? "hex-dusk" : "hex-dawn") + (BuildSuffix.Length > 0 ? "-" + BuildSuffix.ToLowerInvariant() : string.Empty);

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
