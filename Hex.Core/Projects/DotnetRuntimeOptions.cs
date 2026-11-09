namespace Hex.Core.Projects;

/// <summary>Native SDK runtime selection, forwarded unchanged to builds and property-only queries.</summary>
internal sealed record DotnetRuntimeOptions(string? RuntimeIdentifier = null, string? Architecture = null, string? OS = null, bool? SelfContained = null)
{
    public bool Any => RuntimeIdentifier is not null || Architecture is not null || OS is not null || SelfContained is not null;

    public void AddArguments(List<string> arguments)
    {
        if (RuntimeIdentifier is not null) arguments.AddRange(["--runtime", RuntimeIdentifier]);
        if (Architecture is not null) arguments.AddRange(["--arch", Architecture]);
        if (OS is not null) arguments.AddRange(["--os", OS]);
        if (SelfContained is not null) arguments.AddRange(["--self-contained", SelfContained.Value ? "true" : "false"]);
    }
}
