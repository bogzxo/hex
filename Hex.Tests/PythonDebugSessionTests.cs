using Horizon.UI.Scripting;
using Hex.Core;
using System.Diagnostics;

namespace Hex.Tests;

public sealed class PythonDebugSessionTests
{
    [Fact]
    public async Task InstalledDebugpyStopsAtBreakpointAndReadsLocals()
    {
        string? python = Environment.GetEnvironmentVariable("HEX_DEBUGPY_PYTHON");
        if (string.IsNullOrEmpty(python)) return;
        string folder = Path.Combine(Path.GetTempPath(), "hex-python-debug-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "main.py");
        await File.WriteAllTextAsync(file, "import sys\nvalue = 42\nprefix = sys.prefix\nprint(value)\n");
        try
        {
            string environment = Path.Combine(folder, ".venv");
            var create = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string argument in new[] { "-m", "venv", "--without-pip", environment }) create.ArgumentList.Add(argument);
            using var creating = Process.Start(create)!;
            await creating.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(0, creating.ExitCode);
            string interpreter = PythonEnvironment.FindLocal(folder, folder)!;
            Assert.Equal(PythonEnvironment.ExecutableIn(environment), interpreter);
            using var session = DebugSession.Start(python, ["-m", "debugpy.adapter"]);
            Assert.NotNull(session);
            bool launched = await session.Launch(file, [], folder,
                new Dictionary<string, IReadOnlyCollection<int>> { [file] = [3] }, DebugSession.PYTHON, interpreter: interpreter).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(launched, session.Failed);
            DebugEvent? stopped = null;
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline && stopped is null)
            {
                while (session.TryTake(out var happened))
                    if (happened.Kind == DebugEventKind.Stopped) stopped = happened;
                if (stopped is null) await Task.Delay(25);
            }
            Assert.NotNull(stopped);
            var frame = stopped.Value.Frames[0];
            Assert.Equal(3, frame.Line);
            Assert.Equal(file, frame.File);
            var scopes = await session.Scopes(frame.Id);
            var variables = new List<DebugVariable>();
            foreach (var scope in scopes) variables.AddRange(await session.Variables(scope.Reference));
            Assert.Contains(variables, variable => variable.Name == "value" && variable.Value == "42");
            Assert.Contains(variables, variable => variable.Name == "prefix" && variable.Value.Replace("\\\\", "\\").Contains(environment, StringComparison.OrdinalIgnoreCase));
            session.Continue();
            deadline = DateTime.UtcNow.AddSeconds(10);
            while (session.State != DebugState.Ended && DateTime.UtcNow < deadline) await Task.Delay(25);
            Assert.Equal(DebugState.Ended, session.State);
        }
        finally { Directory.Delete(folder, true); }
    }
}
