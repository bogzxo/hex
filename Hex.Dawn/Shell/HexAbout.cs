namespace Hex.Dawn;

/// <summary>What Hex says about itself.</summary>
internal static class HexAbout
{
    /// <summary>Hex's version, three numbers of it.</summary>
    public static string Version => System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";

    /// <summary>Hex's version the way a person reads it, the number and what the release is called (2.8.0 thickboy).</summary>
    public static string Label => Hex.Shell.HexVersion.Label;
}
