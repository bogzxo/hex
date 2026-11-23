namespace Hex.Core;

/// <summary>What the editor is doing, as far as Discord is concerned.</summary>
internal enum DiscordMode { Editing, Browsing, Terminal, Idle }

/// <summary>
/// What the editor tells the presence, taken once a second or so. <see cref="FilePath"/> is only ever read for its file
/// name and its extension, never shown.
/// </summary>
internal sealed record DiscordSnapshot(DiscordMode Mode, string? FilePath, string? WorkspaceName, long? WorkspaceOpenedUnix,
    bool FileIgnoredByGit, bool FileInPrivateWorktree, int LinkPeers);

/// <summary>What of the snapshot may be shown, from the settings.</summary>
internal sealed record DiscordPrivacy(bool ShowFile, bool ShowWorkspace, bool ShowLanguageIcon, IReadOnlyList<string> HideGlobs)
{
    public static DiscordPrivacy Of(HexSettings settings) =>
        new(settings.DiscordShowFile, settings.DiscordShowWorkspace, settings.DiscordShowLanguageIcon, settings.DiscordHideGlobs);
}

/// <summary>The picture keys Discord's art assets are uploaded under, by file extension.</summary>
internal static class DiscordLanguages
{
    /// <summary>The key of the fallback picture and of the small badge.</summary>
    public const string FALLBACK = "hex";

    private static readonly Dictionary<string, (string Key, string Name)> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = ("cs", "C#"), [".csx"] = ("cs", "C#"), [".ts"] = ("ts", "TypeScript"), [".tsx"] = ("ts", "TypeScript"),
        [".js"] = ("js", "JavaScript"), [".jsx"] = ("js", "JavaScript"), [".mjs"] = ("js", "JavaScript"),
        [".py"] = ("py", "Python"), [".md"] = ("md", "Markdown"), [".rs"] = ("rs", "Rust"), [".go"] = ("go", "Go"),
        [".java"] = ("java", "Java"), [".kt"] = ("kt", "Kotlin"), [".c"] = ("c", "C"), [".h"] = ("c", "C"),
        [".cpp"] = ("cpp", "C++"), [".cc"] = ("cpp", "C++"), [".hpp"] = ("cpp", "C++"), [".json"] = ("json", "JSON"),
        [".html"] = ("html", "HTML"), [".css"] = ("css", "CSS"), [".xml"] = ("xml", "XML"), [".yml"] = ("yaml", "YAML"),
        [".yaml"] = ("yaml", "YAML"), [".sh"] = ("sh", "Shell"), [".ps1"] = ("ps1", "PowerShell"), [".lua"] = ("lua", "Lua"),
        [".swift"] = ("swift", "Swift"), [".rb"] = ("rb", "Ruby"), [".php"] = ("php", "PHP"), [".sql"] = ("sql", "SQL"),
        [".hor"] = ("hor", "HIDL"), [".hidl"] = ("hor", "HIDL"), [".glsl"] = ("glsl", "GLSL"), [".toml"] = ("toml", "TOML")
    };

    /// <summary>Every key an art asset is wanted for, besides <see cref="FALLBACK"/>.</summary>
    public static IEnumerable<string> Keys => ByExtension.Values.Select(value => value.Key).Distinct();

    /// <summary>The picture key and the name of the language of an extension, the fallback and no name for an unknown one.</summary>
    public static (string Key, string? Name) Of(string extension) =>
        ByExtension.TryGetValue(extension, out var known) ? (known.Key, known.Name) : (FALLBACK, null);
}

/// <summary>
/// What is said on Discord, made from the editor's state and the privacy settings. The rules: a file's name only, never a
/// path; no name at all for a file git ignores, one in a private Link worktree or one a hide glob matches (the language is
/// not told either, it'd say what kind of secret); no workspace or file when their switches are off.
/// </summary>
internal static class DiscordActivityBuilder
{
    public static DiscordActivity Build(DiscordSnapshot snapshot, DiscordPrivacy privacy, string version)
    {
        string details;
        string largeImage = DiscordLanguages.FALLBACK;
        string? largeText = "Hex";

        switch (snapshot.Mode)
        {
            case DiscordMode.Editing when snapshot.FilePath is { Length: > 0 } path:
                bool hidden = snapshot.FileIgnoredByGit || snapshot.FileInPrivateWorktree || MatchesHide(path, privacy.HideGlobs);
                bool named = privacy.ShowFile && !hidden;
                details = named ? $"Editing {Path.GetFileName(path)}" : "Editing a file";
                if (!hidden && privacy.ShowLanguageIcon && DiscordLanguages.Of(Path.GetExtension(path)) is { Name: not null } language)
                    (largeImage, largeText) = (language.Key, language.Name);
                break;
            case DiscordMode.Editing:
            case DiscordMode.Browsing:
                details = "Browsing files";
                break;
            case DiscordMode.Terminal:
                details = "In the terminal";
                break;
            default:
                details = "Idle";
                break;
        }

        string? state = null;
        if (privacy.ShowWorkspace && snapshot.WorkspaceName is { Length: > 0 } workspace)
        {
            state = $"in {workspace}";
            if (snapshot.LinkPeers > 0)
                state += snapshot.LinkPeers == 1 ? " with 1 other" : $" with {snapshot.LinkPeers} others";
        }

        return new DiscordActivity(details, state, snapshot.WorkspaceOpenedUnix, largeImage, largeText, DiscordLanguages.FALLBACK,
            $"Hex {version}", "Get Hex", HexDiscord.Website);
    }

    /// <summary>Whether a file's full path matches one of the patterns that are never shown.</summary>
    public static bool MatchesHide(string path, IReadOnlyList<string> patterns)
    {
        if (patterns.Count == 0)
            return false;

        string slashed = path.Replace('\\', '/');
        foreach (HexGlob glob in HexGlob.All(patterns))
        {
            if (glob.Matches(slashed, false))
                return true;
        }

        return false;
    }
}
