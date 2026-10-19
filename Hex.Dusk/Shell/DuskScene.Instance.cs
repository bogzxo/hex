using Horizon.UI.Components;

namespace Hex.Dusk;

// One Dusk at a time. Another start of it (Hex.exe --dusk, from a shell, from Explorer's Open in terminal) tells this one
// through Hex.Shell's HexInstance, "tab|folder|profile", and leaves, and the folder opens here as a tab, the window
// brought to the front.
internal sealed partial class DuskScene
{
    private Hex.Shell.HexInstance? instance;

    private void UpdateInstance()
    {
        while (instance is not null && instance.TryTake(out string line))
        {
            string[] parts = line.Split('|');
            if (parts is not ["tab", ..])
                continue;

            string? folder = parts.Length > 1 && Directory.Exists(parts[1]) ? parts[1] : null;
            TerminalProfile? profile = parts.Length > 2 && parts[2].Length > 0 ? host.ProfileCalled(parts[2]) : null;
            panel.NewTab(profile ?? host.UsualProfile, folder);
            Engine.WindowManager.BringToFront();
        }
    }
}
