using Horizon.Core.Threading;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The self test of the work going on in the background and its cards: a piece of work with a card that follows how far
// it got and what it's doing, the same key started again being the one there is, Cancel stopping it, a failure saying
// why with its output, the status bar's count, and an install of a language server by its command line, pressed twice.
internal sealed partial class DawnScene
{
    /// <summary>
    /// A picture of the window as it is, into the folder HEX_SELFTEST_SHOTS names like every other part's pictures, for
    /// whoever wants to look. DAWN_SELFTEST_SHOTS was this one's own name for it and still works. Nothing without either.
    /// </summary>
    private void Shot(string name)
    {
        string? folder = System.Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS");
        if (string.IsNullOrEmpty(folder))
            folder = System.Environment.GetEnvironmentVariable("DAWN_SELFTEST_SHOTS");
        if (!string.IsNullOrEmpty(folder))
            Engine.CaptureScreenshot(Path.Combine(folder, name + ".png"));
    }

    private void TestWork(HexPointerScript test)
    {
        var go = new TaskCompletionSource();
        var halfway = new TaskCompletionSource();
        WorkItem? item = null, again = null;
        UINotification? Card(WorkItem? of) => of is not null && workCards.Cards.TryGetValue(of, out var shown) ? shown.Card : null;

        test.Run(() =>
        {
            item = StartWork("selftest:work", "Doing the self test's work", async context =>
            {
                context.Report(0.5f, "half of it");
                context.Write("a line it wrote");
                halfway.TrySetResult();
                await go.Task;
            });
            again = StartWork("selftest:work", "Doing it again", _ => Task.CompletedTask);
        });
        test.WaitFor(() => halfway.Task.IsCompleted, 5.0f);
        test.Wait();
        test.Run(() => Shot("work-card"));
        test.Check("work: a piece of work has a card, the bar as far as it got and what it's doing under it", () =>
            Card(item) is { Closed: false, Progress: 0.5f } card && card.Text == "Doing the self test's work" && card.Detail == "a line it wrote"
            && card.Actions.Exists(action => action.Label == "Cancel"));
        test.Check("work: started again while it goes it's the one there is, with the one card", () =>
            ReferenceEquals(item, again) && toasts.Cards.Count(card => card.Text.StartsWith("Doing", StringComparison.Ordinal)) == 1);
        test.Check("work: the status bar says it's going", () => workCards.Item!.IsVisible!() && workCards.Item!.Text.Contains("Doing the self test's work"));
        test.Settle(0.2f);
        test.Run(() => Shot("work-status"));

        UINotification? ended = null;
        test.Run(() =>
        {
            ended = Card(item);
            go.SetResult();
        });
        test.WaitFor(() => item!.State == WorkState.Succeeded && ended!.Icon == "check", 5.0f);
        test.Check("work: done, its card says so with how long it took and what it wrote", () =>
            ended is { Icon: "check", Progress: null } card && card.Text.StartsWith("Doing the self test's work, done") && card.Actions.Exists(action => action.Label == "Output")
            && !workCards.Cards.ContainsKey(item!));

        // Cancel, the card's own button
        WorkItem? stopped = null;
        test.Run(() =>
        {
            stopped = StartWork("selftest:stop", "Waiting for ever", async context => await Task.Delay(Timeout.Infinite, context.Token));
            Card(stopped)!.Actions.Find(action => action.Label == "Cancel").Pressed();
        });
        test.WaitFor(() => stopped!.State == WorkState.Cancelled, 5.0f);
        test.Wait();
        test.Check("work: Cancel stops it", () => stopped!.State == WorkState.Cancelled && toasts.Cards.Any(card => card.Text == "Waiting for ever, stopped"));

        // A failure stays up and says why
        WorkItem? broken = null;
        test.Run(() => broken = StartWork("selftest:fail", "Breaking", context =>
        {
            context.Write("what went before");
            throw new InvalidOperationException("the network is gone");
        }));
        test.WaitFor(() => broken!.State == WorkState.Failed, 5.0f);
        test.Wait();
        test.Check("work: one that fails says why, stays up until it's closed, its output a click away", () =>
            toasts.Cards.FirstOrDefault(card => card.Text.StartsWith("Breaking didn't work", StringComparison.Ordinal)) is { Seconds: 0.0f, Icon: "error" } card
            && card.Text.Contains("the network is gone") && card.Actions.Exists(action => action.Label == "Output"));

        // A language server installed by its command line, pressed twice, run once, how far it got out of what it printed
        string twice = OperatingSystem.IsWindows() ? "echo 25%&& ping -n 2 127.0.0.1 >nul&& echo 75%" : "echo 25%; sleep 1; echo 75%";
        var server = new HexServerEntry("selftest", "hex-selftest-no-such-server", [], "selftest", [".selftestnever"], string.Empty, twice, twice, string.Empty, null);
        WorkItem? installing = null;
        int installs = 0;
        test.Run(() =>
        {
            languages.InstallServer(server);
            installing = languages.Installing(server);
            languages.InstallServer(server);
            installs = work.Items.Count(going => going.Key == HexLanguages.INSTALL_WORK + "selftest");
        });
        test.WaitFor(() => installing!.Progress is >= 0.25f, 5.0f);
        test.Check("work: an install pressed twice is the one install, its bar set by the percentages it printed", () =>
            installing is not null && installs == 1 && installing.Progress is >= 0.25f && Card(installing) is not null);
        test.WaitFor(() => !installing!.IsActive, 15.0f);
        test.Check("work: and it ends, what it printed kept", () =>
            installing!.State == WorkState.Succeeded && installing.Progress == 1.0f && installing.Output.Any(line => line.Contains("75%")));
        test.Run(() =>
        {
            foreach (UINotification card in toasts.Cards.ToList())
                card.Close();
        });
    }
}
