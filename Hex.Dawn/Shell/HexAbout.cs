namespace Hex.Dawn;

/// <summary>What Hex says about itself.</summary>
internal static class HexAbout
{
    /// <summary>Hex's version, three numbers of it.</summary>
    public static string Version => System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
}
