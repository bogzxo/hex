using Hex.Shell;

namespace Hex.Tests;

/// <summary>A pipe server never takes the program down: two on one name, a stale socket file left by a crash.</summary>
public class HexInstanceResilienceTests
{
    private static string Unique() => $"hex-test-{Guid.NewGuid():N}";

    private static bool Hear(HexInstance on, string pipe, string line)
    {
        for (int tries = 0; tries < 60; tries++)
        {
            if (HexInstance.Tell(pipe, line))
            {
                for (int wait = 0; wait < 60; wait++)
                {
                    if (on.TryTake(out string? got) && got == line)
                        return true;
                    Thread.Sleep(10);
                }
            }
            else
            {
                Thread.Sleep(20);
            }
        }

        return false;
    }

    [Fact]
    public void A_second_server_on_one_name_leaves_the_first_serving_and_throws_nothing()
    {
        string pipe = Unique();
        using var first = new HexInstance(pipe);
        Assert.True(Hear(first, pipe, "one"));

        var heard = new List<string>();
        using var cts = new CancellationTokenSource();
        Exception? thrown = null;
        var second = new Thread(() =>
        {
            try { HexInstance.Serve(pipe, heard.Add, cts.Token); }
            catch (Exception e) { thrown = e; }
        });
        second.Start();

        Assert.True(second.Join(TimeSpan.FromSeconds(20)), "the second server gave up rather than loop for ever");
        Assert.Null(thrown);
        Assert.Empty(heard);
        Assert.True(Hear(first, pipe, "two"));
    }

    [Fact]
    public void A_stale_socket_file_is_taken_away_and_the_pipe_served()
    {
        if (OperatingSystem.IsWindows())
            return;

        string pipe = Unique();
        string file = Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + pipe);

        // a file at the socket's path nobody listens on, as a crashed program leaves (a closed Socket unlinks its own)
        File.WriteAllText(file, string.Empty);
        Assert.True(File.Exists(file));

        using var server = new HexInstance(pipe);
        Assert.True(Hear(server, pipe, "back"));
    }
}
