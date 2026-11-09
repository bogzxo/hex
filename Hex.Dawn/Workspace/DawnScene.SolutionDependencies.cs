using Hex.Core.Projects;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private readonly Dictionary<SolutionProject, Action> afterDependencies = [];

    private bool EnsureSolutionDependencies(SolutionProject project, Action? ready = null)
    {
        string configuration = Configuration;
        if (project.DependencyConfiguration == configuration)
        {
            if (project.DependencyError is null)
                return true;
            if (ready is null)
                return false;
            // An explicit command retries a failed SDK query; painting never retries it.
            project.DependencyConfiguration = null;
            project.DependencyError = null;
        }

        if (ready is not null)
            afterDependencies[project] = ready;
        if (project.DependenciesLoading)
            return false;

        project.DependenciesLoading = true;
        int generation = project.EvaluationGeneration;
        Context.Work.InBackground(Said("file_properties.evaluating"), () =>
        {
            try
            {
                var result = DotnetSolutionTree.EvaluateDependencies(project.Path, configuration).GetAwaiter().GetResult();
                return (result.References, result.Packages, result.Files, Error: (string?)null);
            }
            catch (Exception e)
            {
                return (References: (IReadOnlyList<string>)[], Packages: (IReadOnlyList<SolutionPackage>)[], Files: (IReadOnlyList<SolutionProjectFile>)[], Error: (string?)e.Message);
            }
        }, result =>
        {
            project.DependenciesLoading = false;
            if (project.EvaluationGeneration != generation)
            {
                treeDirty = true;
                return;
            }
            project.DependencyConfiguration = configuration;
            project.DependencyError = result.Error;
            project.References = result.References;
            project.Packages = result.Packages;
            project.Files = result.Files;
            // An old workspace/configuration may finish after the user has moved on.
            if (solution?.Projects.Contains(project) != true)
            {
                afterDependencies.Remove(project);
                return;
            }
            treeDirty = true;
            if (Configuration != configuration)
                return;
            afterDependencies.Remove(project, out Action? action);
            if (result.Error is null)
                action?.Invoke();
            else if (action is not null)
                Say(Said("file_properties.not_evaluated", ("why", result.Error!)), error: true);
        }, quiet: true);
        return false;
    }
}
