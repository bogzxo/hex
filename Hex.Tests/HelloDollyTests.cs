using Horizon.HIDL;
using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;

namespace Hex.Tests;

/// <summary>
/// Hello, Dolly (hello_dolly in the hex-extensions catalog), run in a bare HIDL runtime the way Dawn runs it. A greeting
/// as it starts, a timer every minute that puts another one up, never the same one twice running, and the command.
/// </summary>
public class HelloDollyTests
{
    private static readonly string Folder = Path.Combine(Catalog.Folder, "hello_dolly");

    private sealed class Screen
    {
        public readonly List<string> Titles = [];
        public readonly List<(double Seconds, IRuntimeValue Function)> Every = [];
        public readonly List<(string Name, IRuntimeValue Function)> Commands = [];
        public ObjectValue Object { get; }

        public Screen()
        {
            Object = Natives.Object(
                ("title_note", Natives.Action("title_note", args => Titles.Add(args.Text(0)))),
                ("every", Natives.Action("every", args => Every.Add((args.Number(0), args.Callable(1))))),
                ("command", Natives.Action("command", args => Commands.Add((args.Text(0), args.Callable(1))))));
        }
    }

    private static (HIDLRuntime Runtime, Screen Hex) Start()
    {
        var screen = new Screen();
        var runtime = new HIDLRuntime { BaseDirectory = Folder, Output = null };
        runtime.UserScope.DeclareSystem("hex", screen.Object);
        runtime.RunFile(Path.Combine(Folder, "extension.hor"));
        runtime.Invoke(runtime.UserScope.Variables["on_start"], [screen.Object]);
        return (runtime, screen);
    }

    [Fact]
    public void GreetsAsItStartsAndAsksForAMinuteTimer()
    {
        var (_, hex) = Start();

        Assert.Single(hex.Titles);
        Assert.False(string.IsNullOrWhiteSpace(hex.Titles[0]));
        var (seconds, _) = Assert.Single(hex.Every);
        Assert.Equal(60, seconds);
    }

    [Fact]
    public void EveryMinuteIsANewGreetingNeverTheSameTwiceRunning()
    {
        var (runtime, hex) = Start();
        IRuntimeValue tick = hex.Every[0].Function;

        for (int minute = 0; minute < 50; minute++)
            runtime.Invoke(tick, [hex.Object]);

        Assert.Equal(51, hex.Titles.Count);
        for (int i = 1; i < hex.Titles.Count; i++)
            Assert.NotEqual(hex.Titles[i - 1], hex.Titles[i]);
        Assert.True(hex.Titles.Distinct().Count() > 5);
    }

    [Fact]
    public void TheCommandPutsUpAnotherGreeting()
    {
        var (runtime, hex) = Start();
        var (name, function) = Assert.Single(hex.Commands);
        Assert.Equal("Another greeting", name);

        runtime.Invoke(function, [hex.Object]);

        Assert.Equal(2, hex.Titles.Count);
        Assert.NotEqual(hex.Titles[0], hex.Titles[1]);
    }
}
