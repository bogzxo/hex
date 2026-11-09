using Hex.Core;

namespace Hex.Tests;

public class PythonEnvironmentCreationTests
{
    [Fact]
    public async Task CancelledCreationDoesNotCreateOrSelectAnEnvironment()
    {
        string root = Path.Combine(Path.GetTempPath(), "hex-python-create-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PythonEnvironment.Create("must-not-start", root, _ => { }, cancel.Token));
            Assert.False(Directory.Exists(Path.Combine(root, ".venv")));
            Assert.Null(PythonEnvironment.Selected(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ExistingFileAtEnvironmentPathIsNotOverwritten()
    {
        string root = Path.Combine(Path.GetTempPath(), "hex-python-create-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            string file = Path.Combine(root, ".venv");
            File.WriteAllText(file, "keep");
            await Assert.ThrowsAsync<IOException>(() => PythonEnvironment.Create("must-not-start", root, _ => { }, default));
            Assert.Equal("keep", File.ReadAllText(file));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task FailedStartPreservesPartialDirectoryAndAllowsRetry()
    {
        string root = Path.Combine(Path.GetTempPath(), "hex-python-create-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        var output = new List<string>();
        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                await Assert.ThrowsAnyAsync<Exception>(() => PythonEnvironment.Create("hex-nonexistent-python-" + Guid.NewGuid(), root, output.Add, default));
                Assert.False(Directory.Exists(Path.Combine(root, ".venv")));
                Assert.Equal(attempt + 1, Directory.GetDirectories(root, ".venv.failed-*").Length);
            }
            Assert.Contains(output, line => line.Contains("You can retry"));
            Assert.Empty(Directory.GetDirectories(root, ".hex-venv-*"));
            Assert.Null(PythonEnvironment.Selected(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ExistingDirectoryIsNeverMovedOnFailure()
    {
        string root = Path.Combine(Path.GetTempPath(), "hex-python-create-" + Guid.NewGuid());
        string folder = Path.Combine(root, ".venv");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "keep.txt"), "keep");
        try
        {
            await Assert.ThrowsAsync<IOException>(() => PythonEnvironment.Create("must-not-start", root, _ => { }, default));
            Assert.Equal("keep", File.ReadAllText(Path.Combine(folder, "keep.txt")));
            Assert.Empty(Directory.GetDirectories(root, ".venv.failed-*"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task NativeCancellationPreservesFilesAndRetryCreatesUsableEnvironment()
    {
        string? python = Environment.GetEnvironmentVariable("HEX_TEST_PYTHON");
        if (string.IsNullOrEmpty(python)) return;
        string root = Path.Combine(Path.GetTempPath(), "hex-python-create-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            using var cancel = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PythonEnvironment.Create(python, root, _ => cancel.Cancel(), cancel.Token));
            Assert.False(Directory.Exists(Path.Combine(root, ".venv")));
            string preserved = Assert.Single(Directory.GetDirectories(root, ".venv.failed-*"));
            Assert.True(File.Exists(Path.Combine(preserved, "pyvenv.cfg")));
            Assert.True(File.Exists(PythonEnvironment.ExecutableIn(preserved)));
            string interpreter = await PythonEnvironment.Create(python, root, _ => { }, default);
            Assert.Equal(PythonEnvironment.ExecutableIn(Path.Combine(root, ".venv")), interpreter);
            Assert.True(File.Exists(interpreter));
            Assert.True(File.Exists(Path.Combine(preserved, "pyvenv.cfg")));
            Assert.Null(PythonEnvironment.Selected(root));
        }
        finally { Directory.Delete(root, true); }
    }
}
