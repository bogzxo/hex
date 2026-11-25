using Hex.Core;

namespace Hex.Tests;

/// <summary>When a message to a chat that isn't working starts a turn: batched after a debounce, never for an echo.</summary>
public sealed class HexMailResumeTests
{
    private long clock;

    private HexMailResume Make() => new(() => clock);

    [Fact]
    public void MessagesArrivingTogetherAreReadyOnceAfterTheDebounce()
    {
        HexMailResume resume = Make();
        resume.Schedule(7);
        clock = 400;
        resume.Schedule(7);
        Assert.Empty(resume.Ready());

        clock = HexMailResume.DEBOUNCE_MS;
        Assert.Equal([7], resume.Ready());
        resume.Take(7);
        Assert.Empty(resume.Ready());
        Assert.False(resume.Pending(7));
    }

    [Fact]
    public void ASecondMessageDoesNotPushTheTurnLater()
    {
        HexMailResume resume = Make();
        resume.Schedule(3);
        clock = 900;
        resume.Schedule(3);
        clock = 1000;
        Assert.Equal([3], resume.Ready());
    }

    [Fact]
    public void AnAnswerWithinASecondToAChatThatMessagedYouIsAnEcho()
    {
        HexMailResume resume = Make();
        resume.Sent(1, 2);
        clock = 500;
        Assert.True(resume.Echo(2, 1), "2 answering what 1 just sent it must not start 1 again");
        Assert.False(resume.Echo(1, 2), "the same direction is not an echo");
        Assert.False(resume.Echo(3, 1), "a third chat is not an echo");

        clock = HexMailResume.ECHO_MS + 1;
        Assert.False(resume.Echo(2, 1), "a second later it is an ordinary message again");
    }

    [Fact]
    public void CancelForgetsAWaitingTurn()
    {
        HexMailResume resume = Make();
        resume.Schedule(5);
        resume.Cancel(5);
        clock = 5000;
        Assert.Empty(resume.Ready());
    }
}
