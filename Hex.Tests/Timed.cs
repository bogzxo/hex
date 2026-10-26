namespace Hex.Tests;

/// <summary>
/// Tests that time what they do, run on their own after the rest. A CI runner busy with every other class at once took
/// five times as long over the same work and failed a test that is well inside its time alone.
/// </summary>
[CollectionDefinition(NAME, DisableParallelization = true)]
public sealed class Timed
{
    public const string NAME = "timed";
}
