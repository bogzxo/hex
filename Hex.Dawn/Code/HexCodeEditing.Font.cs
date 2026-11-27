using Horizon.UI.Drawing;

namespace Hex.Dawn;

// The font the code is written in. Hack unless the settings say otherwise, a font made for code, while the menus and
// the panels keep the skin's Cascadia Mono. A font is a folder of Assets/fonts with a .ttf of the same name in it.
internal sealed partial class HexCodeEditing
{
    internal const string HACK = "hack";

    // How many pixels to the em the font is made at, what the flat skin makes Cascadia Mono at, so the code comes out
    // about as big as it was in that
    internal const int CODE_FONT_EM = 96;

    internal UIFont? codeFont;

    /// <summary>The font the settings say made (the first time) and handed to every editor of code, null for the skin's.</summary>
    internal void ApplyCodeFont()
    {
        UIFont? resolved = ResolveConfiguredCodeFont(
            settings.CodeFont,
            ResolveCodeFontPath,
            static path => UIFont.Shared(Path.GetDirectoryName(path)!, Path.GetFileName(path), CODE_FONT_EM),
            missingName => context.Notices.Say(context.Strings.Said("code.no_font", ("name", missingName)), true));

        codeFont = resolved;
        foreach (HexDocument open in documents)
            open.Editor.Font = codeFont;
    }

    internal static UIFont? ResolveConfiguredCodeFont(
        string configuredName,
        Func<string, string?> resolvePath,
        Func<string, UIFont?> loadShared,
        Action<string>? onMissingOrInvalid = null)
    {
        string? path = configuredName.Length > 0 ? resolvePath(configuredName) : null;
        if (configuredName.Length > 0 && path is null)
        {
            onMissingOrInvalid?.Invoke(configuredName);
            path = resolvePath(HACK) ?? FontFile(HACK);
        }

        UIFont? resolved = path is not null ? loadShared(path) : null;
        if (resolved is { IsLoaded: false })
        {
            if (configuredName.Length > 0)
                onMissingOrInvalid?.Invoke(configuredName);
            string? hackPath = resolvePath(HACK) ?? FontFile(HACK);
            resolved = hackPath is not null && !PlatformPaths.Same(hackPath, path ?? string.Empty)
                ? loadShared(hackPath)
                : null;
            if (resolved is { IsLoaded: false })
                resolved = null;
        }

        return resolved;
    }

    /// <summary>Resolves a configured code font name or path, preferring bundled fonts and falling back to platform system monospace fonts.</summary>
    internal static string? ResolveCodeFontPath(string name) =>
        TryResolveFontPath(name, Hex.Core.HexHost.ProgramFolder, OperatingSystem.IsMacOS(), File.Exists,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>
    /// Pure font path resolver: checks bundled <c>Assets/fonts/&lt;name&gt;/&lt;name&gt;.ttf</c> first, then direct
    /// <c>.ttf</c>/<c>.otf</c>/<c>.ttc</c> file paths, then macOS system monospace font aliases when <paramref name="isMac"/> is set.
    /// </summary>
    internal static string? TryResolveFontPath(string name, string programFolder, bool isMac, Func<string, bool> fileExists, string? userHome = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        string trimmed = name.Trim();

        // 1. Bundled font under Assets/fonts/<name>/<name>.ttf
        if (!Path.IsPathRooted(trimmed) && trimmed.IndexOfAny(['/', '\\']) < 0)
        {
            string bundled = Path.Combine(programFolder, "Assets", "fonts", trimmed, trimmed + ".ttf");
            if (fileExists(bundled))
                return bundled;
        }

        // 2. Direct file path (.ttf, .otf, .ttc), with optional ~/ expansion
        string direct = trimmed;
        if (direct.Length >= 2 && direct[0] == '~' && (direct[1] == '/' || direct[1] == '\\') && !string.IsNullOrEmpty(userHome))
            direct = userHome.TrimEnd('/', '\\') + direct[1..];

        if (IsSupportedFontExtension(direct) && fileExists(direct))
            return direct;

        // 3. macOS system monospace fonts by friendly name or alias
        if (isMac)
        {
            string normalized = trimmed.ToLowerInvariant();
            switch (normalized)
            {
                case "sf mono":
                case "sf_mono":
                case "sfmono":
                case "sfnsmono":
                case "sf-mono":
                    if (fileExists("/System/Library/Fonts/SFNSMono.ttf"))
                        return "/System/Library/Fonts/SFNSMono.ttf";
                    if (fileExists("/Library/Fonts/SF-Mono-Regular.otf"))
                        return "/Library/Fonts/SF-Mono-Regular.otf";
                    if (!string.IsNullOrEmpty(userHome))
                    {
                        string userSfMono = userHome.TrimEnd('/', '\\') + "/Library/Fonts/SF-Mono-Regular.otf";
                        if (fileExists(userSfMono))
                            return userSfMono;
                    }
                    break;

                case "menlo":
                    if (fileExists("/System/Library/Fonts/Menlo.ttc"))
                        return "/System/Library/Fonts/Menlo.ttc";
                    break;

                case "monaco":
                    if (fileExists("/System/Library/Fonts/Monaco.ttf"))
                        return "/System/Library/Fonts/Monaco.ttf";
                    break;

                case "andale mono":
                case "andale_mono":
                case "andalemono":
                case "andale-mono":
                    if (fileExists("/System/Library/Fonts/Supplemental/Andale Mono.ttf"))
                        return "/System/Library/Fonts/Supplemental/Andale Mono.ttf";
                    break;
            }
        }

        return null;
    }

    private static bool IsSupportedFontExtension(string path)
    {
        ReadOnlySpan<char> extension = Path.GetExtension(path.AsSpan());
        return extension.Equals(".ttf", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".otf", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ttc", StringComparison.OrdinalIgnoreCase);
    }

    // Where a font's file is, Assets/fonts/name/name.ttf
    private static string FontFile(string name) => Path.Combine(Hex.Core.HexHost.ProgramFolder, "Assets", "fonts", name, name + ".ttf");

    /// <summary>The code in Hack, or in the skin's font, kept in the settings.</summary>
    internal void ToggleCodeFont()
    {
        settings.CodeFont = settings.CodeFont.Length > 0 ? string.Empty : HACK;
        ApplyCodeFont();
        context.Settings.Save();
        context.Notices.Say(settings.CodeFont.Length > 0 ? "the code is in Hack" : "the code is in the skin's font");
    }
}
