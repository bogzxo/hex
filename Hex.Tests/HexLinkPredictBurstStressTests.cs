namespace Hex.Tests;

public sealed class HexLinkPredictBurstStressTests
{
    [Fact]
    public async Task Prediction_order_survives_two_hundred_runs_under_CPU_load()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("HEX_LINK_NOTICE_STRESS"), "1", StringComparison.Ordinal))
            return;

        using var stress = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        Thread[] workers = [.. Enumerable.Range(0, Math.Clamp(Environment.ProcessorCount - 2, 1, 4)).Select(_ => new Thread(() =>
        {
            while (!stress.IsCancellationRequested)
            {
                Thread.SpinWait(20_000);
                Thread.Yield();
            }
        }) { IsBackground = true, Priority = ThreadPriority.BelowNormal })];
        foreach (Thread worker in workers)
            worker.Start();

        try
        {
            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
            {
                var tests = new HexLinkPredictTests();
                for (int i = 0; i < 50; i++)
                {
                    stress.Token.ThrowIfCancellationRequested();
                    await tests.Pieces_stream_in_order_and_done_ends_them();
                }
            }, stress.Token))).WaitAsync(stress.Token);
        }
        finally
        {
            stress.Cancel();
            foreach (Thread worker in workers)
                worker.Join();
        }
    }
}
