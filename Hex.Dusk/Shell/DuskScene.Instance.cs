using Horizon.UI.Components;

namespace Hex.Dusk;

// One Dusk at a time. Another start of it (Hex.exe --dusk, from a shell, from Explorer's Open in terminal, a task of
// Dawn's that runs in Dusk) tells this one through Hex.Shell's HexInstance, "tab|folder|profile|command line", and
// leaves, and the folder opens here as a tab with the command line typed into it, the window brought to the front.
internal sealed partial class DuskScene
{
    private Hex.Shell.HexInstance? instance;

    private void UpdateInstance()
    {
        while (instance is not null && instance.TryTake(out string line))
            TakeInstanceLine(line);
    }

    /// <summary>What another start of Dusk said, "tab|folder|profile|command line" or "toggle" (the drop down, hex --dusk --toggle).</summary>
    private void TakeInstanceLine(string line)
    {
        // Four at most, the command line last keeps the bars it has (a pipe)
        string[] parts = line.Split('|', 4);
        if (parts is ["toggle"])
        {
            ToggleQuake();
            return;
        }

        if (parts is not ["tab", ..])
            return;

        string? folder = parts.Length > 1 && Directory.Exists(parts[1]) ? parts[1] : null;
        TerminalProfile? profile = parts.Length > 2 && parts[2].Length > 0 ? host.ProfileCalled(parts[2]) : null;
        TerminalTab tab = panel.NewTab(profile ?? host.UsualProfile, folder);
        if (parts.Length > 3 && parts[3].Length > 0)
            tab.Views.FirstOrDefault()?.SendWhenReady(parts[3] + "\r");
        Engine.WindowManager.BringToFront();
    }
}
