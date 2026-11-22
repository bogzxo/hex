using Hex.Shell;

namespace Hex.Dawn;

/// <summary>Where a line of what's new takes whoever presses it.</summary>
internal enum HexNewsPlace
{
    // One setting, its row shown in the settings window
    Setting,

    // A section of the settings window
    Settings,

    // A view of the side bar
    Side,

    // The Problems page of the panel under the editor
    Problems,

    // The New Project window
    NewProject
}

/// <summary>
/// One thing a release brought, for the welcome page: what it is and where it's found. <paramref name="Text"/> and
/// <paramref name="Go"/> are keys under whats_new in the texts, the line itself and what its button says.
/// </summary>
/// <param name="Target">The setting's key or the section's name, for the two places that are in the settings.</param>
/// <param name="Side">The view of the side bar, for that place.</param>
internal sealed record HexNewsItem(string Text, string Go, string Icon, HexNewsPlace Place, string Target = "", HexSide Side = HexSide.Explorer);

/// <summary>
/// What each release of Hex brought that somebody would go looking for, a few lines each and never the whole of its
/// notes (Help, Release Notes has those). The welcome page lists the lines of the version that's running with a button
/// beside each that goes to the thing.
/// <para>
/// Releasing: move what is in <see cref="unreleased"/> into a list under the new version's number, beside the notes in
/// RELEASE_NOTES.md. A version with no list here shows only what is unreleased, and nothing at all when that's empty.
/// </para>
/// </summary>
internal static class HexWhatsNew
{
    // Built since the last release, in every build until a release takes them
    private static readonly HexNewsItem[] unreleased = [];

    private static readonly Dictionary<string, HexNewsItem[]> releases = new(StringComparer.Ordinal)
    {
        ["3.4.0"] =
        [
            new("linked_workspaces", "go_link", "link", HexNewsPlace.Setting, "link")
        ],
        ["3.3.0"] =
        [
            new("actions_menu", "go_actions", "lightbulb", HexNewsPlace.Problems),
            new("faces_smooth", "go_faces", "sparkle", HexNewsPlace.Setting, "ui_animation"),
            new("explorer_inplace", "go_explorer", "files", HexNewsPlace.Side, Side: HexSide.Explorer)
        ],
        ["3.2.0"] =
        [
            new("persistent_process", "go_persistent", "zap", HexNewsPlace.Setting, "persistent_process"),
            new("launch_faster", "go_launch", "dashboard", HexNewsPlace.Setting, "persistent_at_login")
        ],
        ["3.1.0"] =
        [
            new("agentic", "go_agents", "robot", HexNewsPlace.Side, Side: HexSide.Agents),
            new("agentic_review", "go_agents", "diff", HexNewsPlace.Side, Side: HexSide.Agents),
            new("agentic_family", "go_agents", "mail", HexNewsPlace.Side, Side: HexSide.Agents)
        ],
        ["3.0.0"] =
        [
            new("solstice", "go_profiles", "color-mode", HexNewsPlace.Settings, EditorSettingsCatalog.LOOKS),
            new("horizon_line", "go_tasks", "warning", HexNewsPlace.Problems),
            new("new_project", "go_new_project", "new-folder", HexNewsPlace.NewProject)
        ],
        ["2.9.1"] =
        [
            new("agents_markdown", "go_agents", "robot", HexNewsPlace.Side, Side: HexSide.Agents),
            new("middle_scroll", "go_motion", "settings-gear", HexNewsPlace.Setting, "middle_scroll_speed"),
            new("csharp_analyzers", "go_problems", "warning", HexNewsPlace.Problems)
        ]
    };

    /// <summary>The lines for a version by its three numbers, what's unreleased first.</summary>
    public static IReadOnlyList<HexNewsItem> For(string version) =>
        [.. unreleased.Concat(releases.GetValueOrDefault(version, [])).Where(item => !(HexAi.Off && item.Side == HexSide.Agents))];

    /// <summary>Every line there is, for the tests to check each one goes somewhere that exists.</summary>
    public static IEnumerable<HexNewsItem> All => unreleased.Concat(releases.Values.SelectMany(items => items));
}
