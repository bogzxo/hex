using System.Numerics;

using Hex.Core.Projects;

using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The launch profile beside Debug or Release, Visual Studio's dropdown on its Start button. The profiles of the startup
// project's launchSettings.json, the one F5, Ctrl+F5 and the project's Run and Debug start its program with (DotnetLaunch,
// the one place both go to), kept with the workspace. Its arguments are edited in the project's Properties, which makes
// the file when there isn't one.
internal sealed partial class DawnScene
{
    // The profiles of the launchSettings.json read last, and when it was written, so the button doesn't read it every time
    private (string Settings, DateTime Written, List<DotnetLaunchProfile> Profiles)? launchRead;

    private void WireLaunchProfiles() => layout.Get<Button>("launch_profile_button").OnPressed = ShowLaunchProfiles;

    /// <summary>The profiles of a project, read again only when its launchSettings.json was written since.</summary>
    private List<DotnetLaunchProfile> LaunchProfilesOf(string project)
    {
        string settings = DotnetLaunch.SettingsOf(project);
        DateTime written = File.Exists(settings) ? File.GetLastWriteTimeUtc(settings) : DateTime.MinValue;
        if (launchRead is { } read && PlatformPaths.Same(read.Settings, settings) && read.Written == written)
            return read.Profiles;

        List<DotnetLaunchProfile> profiles = written == DateTime.MinValue ? [] : DotnetLaunch.Profiles(project);
        launchRead = (settings, written, profiles);
        return profiles;
    }

    /// <summary>The button saying the profile of the project picked to run, there only while a .NET project is picked.</summary>
    private void ShowLaunchProfile(HexRunChoice? chosen, bool tools)
    {
        var button = layout.Get<Button>("launch_profile_button");
        if (tools || chosen?.Project is not { } project || !project.EndsWith("proj", StringComparison.OrdinalIgnoreCase))
        {
            button.Visible = false;
            return;
        }

        List<DotnetLaunchProfile> profiles = LaunchProfilesOf(project);
        string? picked = Context.Workspace.LaunchProfileOf(project);
        DotnetLaunchProfile? profile = profiles.Find(each => each.Name == picked) ?? profiles.FirstOrDefault();
        button.Visible = true;
        button.Label = profile?.Name ?? Said("launch_profile.none");
        button.Tooltip = profile is null ? Said("launch_profile.tip_none", ("name", Path.GetFileNameWithoutExtension(project)))
            : Said("launch_profile.tip_profile", ("name", profile.Name), ("arguments", profile.CommandLine.Length > 0 ? profile.CommandLine : Said("launch_profile.no_arguments")));
    }

    /// <summary>The profiles under the button, the one in use ticked, and the project's Properties to edit them.</summary>
    private void ShowLaunchProfiles()
    {
        if (tasks.ChosenRun()?.Project is not { } project)
            return;

        List<DotnetLaunchProfile> profiles = LaunchProfilesOf(project);
        string? picked = Context.Workspace.LaunchProfileOf(project);
        string? inUse = (profiles.Find(each => each.Name == picked) ?? profiles.FirstOrDefault())?.Name;
        var menu = new Menu(Said("launch_profile.title"));
        foreach (DotnetLaunchProfile profile in profiles)
        {
            string name = profile.Name;
            menu.Add(profile.CommandLine.Length > 0 ? $"{name}   {profile.CommandLine}" : name, () =>
            {
                Context.Workspace.PickLaunchProfile(project, name);
                Say(Said("launch_profile.chosen", ("name", name)));
            }).IsChecked = () => name == inUse;
        }

        menu.AddSeparator();
        menu.Add(Said("launch_profile.edit"), () => ShowPropertiesOf([project]), icon: "settings");
        UIComponent button = layout.Get<Button>("launch_profile_button");
        Context.Windows.MenuAt(menu, chrome.ToWorld(new Vector2(button.Bounds.Min.X, button.Bounds.Min.Y)));
    }
}
