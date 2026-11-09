using Hex.Dawn;
using Horizon.UI.Scripting;

namespace Hex.Tests;

public class HexDebugSourceTextTests
{
    [Theory]
    [InlineData("../../escape.py", null)]
    [InlineData("..\\..\\escape.py", null)]
    [InlineData("CON.cs", null)]
    [InlineData("<generated.py>", null)]
    [InlineData("", "C:\\remote\\generated.py")]
    [InlineData("../invalid:*?<>|name", null)]
    [InlineData("...", null)]
    public void CacheNamesCannotEscapeTheirOwnedDirectoryOrBecomeWindowsDevicePaths(string name, string? path)
    {
        string file = HexDebugSourceText.FileName(name, path, null, DebugSession.PYTHON);
        Assert.StartsWith("Source-", file);
        Assert.Equal(file, Path.GetFileName(file));
        Assert.DoesNotContain('/', file);
        Assert.DoesNotContain('\\', file);
        Assert.DoesNotContain(':', file);
        Assert.True(file.Length <= 123);
    }

    [Theory]
    [InlineData("<generated.py>", null, DebugSession.PYTHON, ".py")]
    [InlineData("unknown", null, DebugSession.CORECLR, ".cs")]
    [InlineData("unknown", null, "lldb-dap", ".cpp")]
    [InlineData("generated.txt", "text/x-python", DebugSession.CORECLR, ".py")]
    [InlineData("generated", "text/x-csharp", DebugSession.PYTHON, ".cs")]
    [InlineData("remote", "text/x-c", "lldb-dap", ".c")]
    [InlineData("generated.png", null, DebugSession.PYTHON, ".py")]
    [InlineData("generated.hor", null, DebugSession.PYTHON, ".py")]
    [InlineData("generated.pdf", null, DebugSession.CORECLR, ".cs")]
    [InlineData("generated.txt", "text/x-python; charset=utf-8", DebugSession.CORECLR, ".py")]
    public void NativeNamesAndMimeTypesKeepTheCorrectSyntaxExtension(string name, string? mime, string kind, string extension) =>
        Assert.EndsWith(extension, HexDebugSourceText.FileName(name, null, mime, kind));
}
