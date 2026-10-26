using Horizon.HIDL.Runtime;
using Horizon.UI.Components;

namespace Hex.Shell;

/// <summary>
/// The row that makes Dusk the system's terminal (<see cref="HexDefaultTerminal"/>), in both modes' settings with the
/// terminal's. Like the Explorer menu it's nothing in the settings file, what it says is whether the system has Dusk as
/// its terminal. What went wrong when it couldn't (no Windows Terminal to pass consoles on) is told to whoever made the
/// row, for a card. On macOS there's no row at all.
/// </summary>
public sealed class DefaultTerminalSource(Action<string>? problem = null) : ISettingsSource
{
    internal const string KEY = "default_terminal", GROUP = "Terminal · System";

    /// <summary>Dusk became the system's terminal (true) or stopped being it, for the Dusk that's running to start or stop taking consoles.</summary>
    public static event Action<bool>? Switched;

    public string Section => SettingsSections.EDITOR;

    public IReadOnlyList<UISetting> Settings { get; } = HexDefaultTerminal.Possible
        ? [new UISetting(KEY, HexText.Get("settings_catalog.default_terminal_title"), UIFormKind.Flag, HexText.Get(OperatingSystem.IsWindows()
            ? "settings_catalog.default_terminal_help_windows" : "settings_catalog.default_terminal_help_linux"), GROUP)]
        : [];

    public IRuntimeValue? Get(string key) => key == KEY ? Values.Bool(HexDefaultTerminal.Installed) : null;

    // What the system has is no setting of the file, so there is nothing it comes as for it to differ from
    public bool IsModified(string key) => false;

    public void Set(string key, IRuntimeValue value)
    {
        if (key != KEY || value is not BooleanValue on || on.Value == HexDefaultTerminal.Installed)
            return;

        if (HexDefaultTerminal.Set(on.Value) is { } why)
        {
            Console.WriteLine($"[Hex] The default terminal didn't change, {why}");
            problem?.Invoke(HexText.Get("settings_catalog.default_terminal_failed", ("why", why)));
            return;
        }

        Switched?.Invoke(on.Value);
    }

    public void Reset(string key) => Set(key, Values.Bool(false));
}
