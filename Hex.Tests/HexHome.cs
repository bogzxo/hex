namespace Hex.Tests;

/// <summary>
/// The tests that set HEX_HOME for the process, which moves Hex's data folder, and the ones that read where that folder
/// is, one after the other. The data folder's test read the root before a door test set HEX_HOME and its places after,
/// and found them under another root.
/// </summary>
[CollectionDefinition(NAME)]
public sealed class HexHome
{
    public const string NAME = "Hex's home";
}
