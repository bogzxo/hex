using System.Diagnostics;
using System.Text;

namespace Hex.Dusk;

// How fast a shell's output goes through, the way a long build's does: a file of build-like lines about 23 MB long typed
// out by the real shell through ConPTY into a pane, timed from the command to the marker after it, printed in MB a second.
// Only run when asked for by name, it's a measurement and takes a while. Windows only, it types with cmd's type.
internal sealed partial class DuskScene
{
    private void TestThroughput(DuskScript test)
    {
        if (!OperatingSystem.IsWindows())
            return;

        string file = Path.Combine(Path.GetTempPath(), "dusk-throughput.txt");
        long bytes = 0;
        var took = new Stopwatch();
        // Typed with an empty expansion in it, so only what echo says has the marker whole
        const string MARKER = "throughput-done-marker", TYPED = "throughput-done-%COMSPEC:~0,0%marker";
        test.Run(() =>
        {
            var line = new StringBuilder();
            using (var writer = new StreamWriter(file, false, new UTF8Encoding(false)))
            {
                for (int i = 0; writer.BaseStream.Length < 23L * 1024 * 1024; i++)
                {
                    line.Clear().Append("  Compiling C:\\src\\horizon\\Horizon.Terminal\\TerminalScreen.cs (").Append(i).Append(") -> obj\\Debug\\net11.0\\Horizon.Terminal.dll warning CS8618: non-nullable field");
                    writer.WriteLine(line);
                    if (i % 1000 == 0)
                        writer.Flush();
                }
            }

            bytes = new FileInfo(file).Length;
            took.Restart();
            panel.FocusedView!.Send($"cmd /c 'type \"{file}\" & echo {TYPED}'\r");
        });
        test.WaitFor(() => panel.FocusedView!.Screen.Text().Contains(MARKER, StringComparison.Ordinal), 600.0f);
        test.Run(() =>
        {
            took.Stop();
            double mb = bytes / (1024.0 * 1024.0);
            Console.WriteLine($"[Dusk self-test] throughput: {mb:0.0} MB in {took.Elapsed.TotalSeconds:0.00} s, {mb / took.Elapsed.TotalSeconds:0.0} MB/s");
            File.Delete(file);
        });
        test.Check("throughput: the output all went through", () => panel.FocusedView!.Screen.Text().Contains(MARKER, StringComparison.Ordinal));
    }
}
