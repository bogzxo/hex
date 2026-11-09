namespace Hex.Core.Projects;

// A property of the project itself changed, written into the .csproj the way Visual Studio's project properties write it
// (ProjectFileEdits.SetProperty), then the SDK asked again what the project says now.
internal sealed partial class DotnetProjects
{
    /// <summary>A property of a project set, or taken off with null, off the editor's thread, and what the project evaluates to after.</summary>
    public async Task<DotnetEvaluation> SetProperty(string project, string name, string? value, CancellationToken cancel = default)
    {
        project = Path.GetFullPath(project);
        var (original, encoding) = ReadProject(project);
        string text = ProjectFileEdits.SetProperty(original, name, value);
        return text == original ? await Evaluate(project, cancel) : await Write(project, text, encoding, cancel);
    }
}
