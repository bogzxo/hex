using Horizon.HIDL.Runtime;
using Horizon.UI.Components;

namespace Hex.Shell;

/// <summary>
/// The rows that put Hex into Windows itself, Open in Hex on Explorer's right click (<see cref="HexExplorerMenu"/>) and
/// Hex's folder on the user's PATH (<see cref="HexUserPath"/>), in both modes' settings in a section of their own under
/// the editor (<see cref="GROUP"/>), they sat at the end of its files and had nothing to do with them. They're
/// nothing in the settings file, what they say is whether the registry has them, so a Hex that moved or a second copy
/// never shows a tick that isn't true. Off Windows there are no rows at all.
/// </summary>
public sealed class WindowsSource : ISettingsSource
{
    /// <summary>The section the rows are in, the editor's part of the window.</summary>
    public const string GROUP = "Editor · Windows";

    // A row by its key, whether Windows has it, how it's put in or taken out, and what it's called in the log
    private static readonly (string Key, Func<bool> Installed, Func<bool, string?> Set, string What)[] Rows =
    [
        ("explorer_menu", () => HexExplorerMenu.Installed, HexExplorerMenu.Set, "The Explorer menu"),
        ("user_path", () => HexUserPath.Installed, HexUserPath.Set, "The PATH")
    ];

    public string Section => SettingsSections.EDITOR;

    public IReadOnlyList<UISetting> Settings { get; } = HexExplorerMenu.Possible && HexUserPath.Possible
        ?
        [
            .. Rows.Select(row => new UISetting(row.Key, HexText.Get($"settings_catalog.{row.Key}_title"), UIFormKind.Flag,
                HexText.Get($"settings_catalog.{row.Key}_help"), GROUP))
        ]
        : [];

    public IRuntimeValue? Get(string key) => Find(key) is { } row ? Values.Bool(row.Installed()) : null;

    // What Windows has is no setting of the file, a tick there is no change from anything it comes as
    public bool IsModified(string key) => false;

    public void Set(string key, IRuntimeValue value)
    {
        if (Find(key) is { } row && value is BooleanValue on && on.Value != row.Installed() && row.Set(on.Value) is { } problem)
            Console.WriteLine($"[Hex] {row.What} didn't change, {problem}");
    }

    public void Reset(string key) => Set(key, Values.Bool(false));

    private static (string Key, Func<bool> Installed, Func<bool, string?> Set, string What)? Find(string key) =>
        Array.FindIndex(Rows, row => row.Key == key) is var at and >= 0 ? Rows[at] : null;
}
