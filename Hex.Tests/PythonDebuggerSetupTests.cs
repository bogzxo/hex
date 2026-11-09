using Hex.Core;

namespace Hex.Tests;

public sealed class PythonDebuggerSetupTests
{
    [Fact]
    public async Task CancelledSetupDoesNotBecomeReady()
    {
        string folder = Path.Combine(Path.GetTempPath(), "hex-debugpy-cancel-" + Guid.NewGuid());
        try
        {
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PythonDebuggerSetup.Install("unused-python", folder, _ => { }, cancel.Token));
            Assert.False(PythonDebuggerSetup.Ready(folder));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task InstallsAndVerifiesRealDebugpyWhenRequested()
    {
        string? python = Environment.GetEnvironmentVariable("HEX_TEST_DEBUGPY_INSTALL");
        if (string.IsNullOrEmpty(python)) return;
        string folder = Path.Combine(Path.GetTempPath(), "hex-debugpy-install-" + Guid.NewGuid());
        try
        {
            var output = new System.Collections.Concurrent.ConcurrentQueue<string>();
            await PythonDebuggerSetup.Install(python, folder, output.Enqueue, CancellationToken.None);
            Assert.True(PythonDebuggerSetup.Ready(folder));
            Assert.NotEmpty(output);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
