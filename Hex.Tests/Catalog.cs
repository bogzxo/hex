namespace Hex.Tests;

/// <summary>
/// Where the hex-extensions catalog is checked out, the submodule at the top of Hex's repository, found by walking up from
/// the tests' own folder. A clone without its submodules says how to get them rather than failing somewhere odd.
/// </summary>
internal static class Catalog
{
    public static string Folder { get; } = Find();

    private static string Find()
    {
        for (string? at = AppContext.BaseDirectory; at is not null; at = Path.GetDirectoryName(at))
        {
            string inside = Path.Combine(at, "hex-extensions");
            if (Directory.Exists(inside) && Directory.EnumerateDirectories(inside).Any())
                return inside;
        }

        throw new DirectoryNotFoundException("hex-extensions isn't checked out, git submodule update --init hex-extensions");
    }
}
