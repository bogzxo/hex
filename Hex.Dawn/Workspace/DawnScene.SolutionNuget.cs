using Hex.Core.Projects;

using Horizon.UI.Components;

namespace Hex.Dawn;

// Manage NuGet Packages on a project's row, a list of what it takes that becomes nuget.org's search as soon as something
// is typed (NugetSearch, off the editor's thread). A package picked has its versions to pick from, the one it's at
// ticked, and Remove when it's taken. Installing, updating and removing are dotnet add and remove package, which knows
// central package management, and the Dependencies are read again after.
internal sealed partial class DawnScene
{
    // How long the search waits for the next key before it asks nuget.org, so a name typed quickly is one search
    private const int NUGET_PAUSE = 300;

    // How many versions of a package are offered, the newest
    private const int NUGET_VERSIONS = 40;

    /// <summary>Manage NuGet Packages, the packages a project takes, and nuget.org searched for what's typed.</summary>
    private void ManageNuget(SolutionProject project)
    {
        if (!EnsureSolutionDependencies(project, () => ManageNuget(project)))
            return;
        var installed = new List<UIChoice>();
        foreach (SolutionPackage package in project.Packages)
        {
            SolutionPackage taken = package;
            installed.Add(new UIChoice(package.Name, () => LookUpPackage(project, taken.Name)) { Icon = "package", Detail = Said("nuget.installed", ("version", package.Version)) });
        }

        Dictionary<string, string> versions = project.Packages.ToDictionary(package => package.Name, package => package.Version, StringComparer.OrdinalIgnoreCase);
        string searching = Said("nuget.not_reached", ("why", "{why}"));
        ChooseFrom(Said("nuget.title", ("name", project.Name)), Said(installed.Count > 0 ? "nuget.text" : "nuget.text_none", ("name", project.Name)), installed,
            searched: prefix => prefix != '\0' ? null : (typed, cancel) =>
            {
                if (typed.Trim().Length == 0 || cancel.WaitHandle.WaitOne(NUGET_PAUSE))
                    return [];

                try
                {
                    return [.. NugetSearch.Search(typed, cancel).GetAwaiter().GetResult().Select(found => new UIChoice(found.Id, () => PickPackageVersion(project, found))
                    {
                        Icon = "package",
                        Detail = (versions.TryGetValue(found.Id, out string? at) ? $"{at} > {found.Version}" : found.Version) + "   " + NugetSearch.Short(found.Downloads) + "   " + Shorten(found.Description)
                    })];
                }
                catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException or InvalidDataException or TaskCanceledException && !cancel.IsCancellationRequested)
                {
                    return [new UIChoice(searching.Replace("{why}", e.Message, StringComparison.Ordinal), () => { }) { Icon = "warning" }];
                }
            });
    }

    // A package's description on one line, short enough to sit after its name
    private static string Shorten(string description)
    {
        string line = description.ReplaceLineEndings(" ").Trim();
        return line.Length > 90 ? line[..87].TrimEnd() + "..." : line;
    }

    /// <summary>A package the project takes looked up on nuget.org off the editor's thread, for its versions.</summary>
    private void LookUpPackage(SolutionProject project, string id)
    {
        Context.Work.InBackground(Said("nuget.looking_up", ("name", id)), () =>
        {
            try
            {
                return (Found: NugetSearch.Find(id).GetAwaiter().GetResult(), Why: (string?)null);
            }
            catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException or InvalidDataException or TaskCanceledException)
            {
                return (Found: (NugetPackage?)null, Why: e.Message);
            }
        }, done =>
        {
            if (done.Found is { } found)
                PickPackageVersion(project, found);
            else if (done.Why is { } why)
                Say(Said("nuget.not_reached", ("why", why)), error: true);
            else
                PickPackageVersion(project, new NugetPackage(id, string.Empty, string.Empty, 0, []));
        }, quiet: true);
    }

    /// <summary>The versions of a package to install or move to, the one taken ticked, and Remove when the project takes it.</summary>
    private void PickPackageVersion(SolutionProject project, NugetPackage package)
    {
        string? at = project.Packages.FirstOrDefault(taken => taken.Name.Equals(package.Id, StringComparison.OrdinalIgnoreCase))?.Version;
        var choices = new List<UIChoice>();
        foreach (string version in package.Versions.Take(NUGET_VERSIONS))
        {
            string picked = version;
            bool current = at is not null && version.Equals(at, StringComparison.OrdinalIgnoreCase);
            choices.Add(new UIChoice(version, () =>
            {
                if (!current)
                    InstallPackage(project, package.Id, picked, at is null);
            })
            {
                Icon = current ? "check" : "versions",
                Detail = current ? Said("nuget.installed_here") : version == package.Version ? Said("nuget.latest") : string.Empty
            });
        }

        if (at is not null)
        {
            choices.Add(new UIChoice(Said("nuget.remove", ("name", package.Id)), () => RemovePackage(project, new SolutionPackage(package.Id, at))) { Icon = "trash" });
        }

        ChooseFrom(Said("nuget.versions_title", ("name", package.Id)), at is null ? Said("nuget.versions_text", ("name", project.Name)) : Said("nuget.versions_text_at", ("name", project.Name), ("version", at)), choices);
    }

    /// <summary>A package a project takes taken out, dotnet remove package.</summary>
    private void RemovePackage(SolutionProject project, SolutionPackage package) =>
        RunDotnet(project, Said("solution.removing", ("name", package.Name)), Said("solution.removed", ("name", package.Name), ("project", project.Name)),
            "remove", project.Path, "package", package.Name);

    // A package installed at a version, or the one the project takes moved to it
    private void InstallPackage(SolutionProject project, string id, string version, bool fresh) =>
        RunDotnet(project, Said(fresh ? "nuget.installing" : "nuget.updating", ("name", id), ("version", version)),
            Said(fresh ? "nuget.installed_done" : "nuget.updated_done", ("name", id), ("version", version), ("project", project.Name)),
            "add", project.Path, "package", id, "--version", version);
}
