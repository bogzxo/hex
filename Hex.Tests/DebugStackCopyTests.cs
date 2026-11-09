using Hex.Dawn;
using Horizon.UI.Scripting;

namespace Hex.Tests;

public class DebugStackCopyTests
{
    [Fact]
    public void StackCopyPreservesThreadFrameOrderSourceLocationsAndExternalFrames()
    {
        DebugFrame[] frames = [new(1, "Namespace.Worker.Run()", "C:/project with spaces/Worker.cs", 11, 3),
            new(2, "runtime_dispatch", null, 0, 0), new(3, "main", "/workspace/main.cpp", 0, 0)];
        Assert.Equal(string.Join(Environment.NewLine, "Namespace.Worker.Run() in C:/project with spaces/Worker.cs:line 12, column 4",
            "runtime_dispatch", "main in /workspace/main.cpp:line 1, column 1"), HexDebugging.StackText(frames));
        Assert.Equal("", HexDebugging.StackText([]));
    }
}
