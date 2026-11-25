using System.Diagnostics;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    /// <summary>
    /// What Link costs while it is on and nothing happens: the bytes a tick allocates, what the Devices window leaves behind
    /// once it's closed, and a Link Chat of six hundred messages. The numbers are written to hex-link-idle.txt in the temp folder.
    /// </summary>
    private void TestLinkIdle(HexPointerScript test)
    {
        string home = LinkTestFolder("hex-link-idle-", test);
        double perTick = -1, windowLeft = -1, talkMs = -1;
        int talkKept = 0, windowRootChildren = 0, children = 0;
        long held = 0;
        test.Run(() =>
        {
            StopLink();
            StartLink(port: 0, home: home, loopbackOnly: true);
            linkByTest = true;
        });
        test.Settle(1.0f);
        test.Run(() =>
        {
            for (int warm = 0; warm < 200; warm++)
                UpdateLink(0.0167f);
            long before = GC.GetAllocatedBytesForCurrentThread();
            const int TICKS = 1200;
            for (int tick = 0; tick < TICKS; tick++)
                UpdateLink(0.0167f);
            perTick = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)TICKS;
        });

        // The window made and closed again, frames between so its closing runs to its end: what is still held after
        void Cycle()
        {
            test.Run(() =>
            {
                ShowLinkWindowForTest();
                UpdateLink(0.0167f);
                CloseLinkWindow();
            });
            test.Settle(0.5f);
        }
        for (int warm = 0; warm < 3; warm++)
            Cycle();
        test.Run(() =>
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            held = GC.GetTotalMemory(true);
            children = chrome.Root.Children.Count();
        });
        for (int cycle = 0; cycle < 10; cycle++)
            Cycle();
        test.Run(() =>
        {
            windowRootChildren = chrome.Root.Children.Count() - children;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            windowLeft = (GC.GetTotalMemory(true) - held) / 10.0;

            // Six hundred messages heard
            var sender = new HexLinkDevice("idle-sender", "Sender");
            long started = Stopwatch.GetTimestamp();
            for (int index = 0; index < 600; index++)
                HearLinkTalk(new HexLinkTalk(Guid.NewGuid(), sender, $"message {index} with a src/File.cs:12 reference", DateTimeOffset.UtcNow));
            talkMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            talkKept = linkTalkMessages.Count;
            LinkTalkFlush();
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "hex-link-idle.txt"),
                $"perTick={perTick:0.0} windowLeft={windowLeft:0} talkMs={talkMs:0.0} talkKept={talkKept} rootChildrenLeft={windowRootChildren}\n");
        });
        test.Check("link-idle: an idle tick with Link on allocates next to nothing", () => perTick is >= 0 and < 256);
        test.Check("link-idle: the Devices window leaves nothing behind", () => windowLeft < 20_000 && windowRootChildren == 0);
        test.Check("link-idle: the chat keeps a bounded window", () => talkKept is > 0 and <= HexLinkTalkHistory.MOST);
        test.Run(() =>
        {
            ResetLinkTalk();
            StopLink();
        });
    }
}
