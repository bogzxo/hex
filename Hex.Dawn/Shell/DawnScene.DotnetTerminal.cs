using Hex.Core.Projects;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void WireDotnetTerminal()
    {
        commands.Add("Run/check", Said("languages.dotnet_terminal"), OpenDotnetTerminal, icon: "terminal")
            .IsVisible = () => document.Path is {} path && Path.GetExtension(path).ToLowerInvariant() is ".cs" or ".csproj" or ".fs" or ".fsproj" or ".vb" or ".vbproj";
    }

    private void OpenDotnetTerminal()
    {
        if (document.Path is not {} file) return;
        string? project = Path.GetExtension(file).ToLowerInvariant() is ".csproj" or ".fsproj" or ".vbproj" ? file : DotnetProjects.ProjectOf(file);
        if (project is null || !File.Exists(project))
        {
            Say(Said("languages.dotnet_terminal_no_project"), error: true);
            return;
        }
        ShowPanel(CONSOLE_PAGE);
        terminalPanel.NewTab(terminals.UsualProfile, Path.GetDirectoryName(project)!);
    }
}
