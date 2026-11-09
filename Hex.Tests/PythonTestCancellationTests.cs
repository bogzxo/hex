using Hex.Core;

namespace Hex.Tests;

public class PythonTestCancellationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationRetainsCompletedTestsAndOmitsTheInterruptedTest(bool pytest)
    {
        string? python = Environment.GetEnvironmentVariable(pytest ? "HEX_TEST_PYTEST" : "HEX_TEST_PYTHON");
        if (string.IsNullOrEmpty(python)) return;
        string root = Path.Combine(Path.GetTempPath(), "hex-test-cancel-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        using var cancellation = new CancellationTokenSource();
        Task<System.Text.Json.Nodes.JsonObject>? running = null;
        try
        {
            if (pytest) File.WriteAllText(Path.Combine(root, "pytest.ini"), "[pytest]\n");
            string source = pytest ? """
import pathlib, time
def test_a_done():
    print('completed output', flush=True)
    assert True
def test_b_interrupted():
    pathlib.Path('running').write_text('started')
    time.sleep(60)
""" : """
import pathlib, time, unittest
class Sample(unittest.TestCase):
    def test_a_done(self):
        print('completed output', flush=True)
        self.assertTrue(True)
    def test_b_interrupted(self):
        pathlib.Path('running').write_text('started')
        time.sleep(60)
""";
            File.WriteAllText(Path.Combine(root, "test_sample.py"), source);
            running = PythonUnitTests.Execute(python, root, true, cancel: cancellation.Token);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!File.Exists(Path.Combine(root, "running")))
            {
                if (running.IsCompleted) await running;
                await Task.Delay(20, deadline.Token);
            }
            cancellation.Cancel();
            var report = await running.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(report["cancelled"]!.GetValue<bool>());
            var result = Assert.Single(report["results"]!.AsArray())!;
            Assert.EndsWith("test_a_done", result["id"]!.GetValue<string>());
            Assert.Equal("passed", result["outcome"]!.GetValue<string>());
            Assert.DoesNotContain(report["results"]!.AsArray(), row => row!["id"]!.GetValue<string>().Contains("interrupted"));
        }
        finally
        {
            cancellation.Cancel();
            if (running is not null) { try { await running.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { } }
            Directory.Delete(root, true);
        }
    }
}
