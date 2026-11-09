using Hex.Dawn;

namespace Hex.Tests;

public class SourceCacheTests
{
    [Fact]
    public void MetadataCacheSeparatesProjectsAndUrisAndCannotEscapeItsFolder()
    {
        string a = HexLanguages.SourceCachePath("project-a", "csharp:/a/namespace-one/Model.cs");
        string b = HexLanguages.SourceCachePath("project-a", "csharp:/a/namespace-two/Model.cs");
        string c = HexLanguages.SourceCachePath("project-b", "csharp:/a/namespace-one/Model.cs");
        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(a, HexLanguages.SourceCachePath("project-a", "csharp:/a/namespace-one/Model.cs"));
        Assert.Equal(HexLanguages.SourceCachePath("project-a", "csharp:/a folder/Model.cs"),
            HexLanguages.SourceCachePath("project-a", "csharp:/a%20folder/Model.cs"));
        string hostile = HexLanguages.SourceCachePath("project-a", "csharp:/a/%2e%2e%2f%2e%2e%5cModel.cs");
        Assert.StartsWith(Path.GetFullPath(HexLanguages.DecompiledFolder) + Path.DirectorySeparatorChar, Path.GetFullPath(hostile));
        Assert.Equal(".cs", Path.GetExtension(hostile));
        Assert.Equal(Path.GetDirectoryName(a), Path.GetDirectoryName(HexLanguages.SourceCachePath("project-a", "csharp:/a/namespace-one/Model.cs")));
    }
}
