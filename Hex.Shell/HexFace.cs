namespace Hex.Shell;

/// <summary>
/// What the selector on the title bar shows, the faces of Hex. Dawn is the editor, Noon the agents working on the
/// project with you, Dusk the terminal. Dawn and Noon are the one scene (the editor's), Noon a view of it over the
/// whole body, since the agents, their tools and everything they touch live there. Dusk is a scene of its own, see
/// <see cref="HexMode"/>.
/// </summary>
public enum HexFace { Dawn, Noon, Dusk }

public static class HexFaces
{
    /// <summary>The scene a face is shown by.</summary>
    public static HexMode Mode(this HexFace face) => face == HexFace.Dusk ? HexMode.Dusk : HexMode.Dawn;

    /// <summary>The icon of a face on the selector.</summary>
    public static string Icon(this HexFace face) => face switch
    {
        HexFace.Dawn => "code",
        HexFace.Noon => "robot",
        _ => "terminal"
    };
}
