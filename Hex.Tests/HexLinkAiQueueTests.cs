using Hex.Core;
using Hex.Dawn;

using static Hex.Tests.HexLinkTestKit;

namespace Hex.Tests;

/// <summary>
/// Overlapping @ai questions (docs/hex-link-plan.md, 5.10): one is answered at a time, the ones behind it are told their place
/// in the chat and answered in order, and one past what may wait is told the model is busy rather than let go in silence.
/// Also the pairing dialog's switches: every one of them is in what it grants.
/// </summary>
public sealed class HexLinkAiQueueTests
{
    private static HexLinkTalk Said(string text) => new(Guid.NewGuid(), DeviceB, text, DateTimeOffset.UtcNow);

    [Fact]
    public async Task Three_overlapping_questions_queue_in_order_and_a_fourth_is_told_the_model_is_busy()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = new List<string>();
        var said = new List<string>();
        var options = new LinkAssistantOptions("http://x", "qwen", string.Empty, PredictionTuning.Usual);

        using var assistant = new LinkAssistant(() => options, (text, _, _) =>
        {
            lock (said)
                said.Add(text);
            return HexLinkTalkResult.Sent;
        }, () => DeviceA.Id, async (_, conversation, cancel) =>
        {
            string question = conversation[^1].Content;
            lock (asked)
                asked.Add(question);
            await release.Task.WaitAsync(cancel);
            return new ChatAnswer("answer to " + question);
        })
        {
            Busy = "busy",
            Queued = place => "queued " + place,
        };

        assistant.Heard(Said("@ai one"));
        Assert.True(Eventually(() => asked.Count == 1));
        assistant.Heard(Said("@ai two"));
        assistant.Heard(Said("@ai three"));
        assistant.Heard(Said("@ai four"));

        // The second and third say their place, the fourth that the model is busy; none waits in silence
        lock (said)
            Assert.Equal(["queued 2", "queued 3", "busy"], said);
        Assert.Single(asked);

        release.SetResult();
        Assert.True(Eventually(() => assistant.Waiting == 0));
        Assert.Equal(3, asked.Count);
        Assert.Contains("one", asked[0]);
        Assert.Contains("two", asked[1]);
        Assert.Contains("three", asked[2]);
        lock (said)
            Assert.Equal(["queued 2", "queued 3", "busy", "answer to " + asked[0], "answer to " + asked[1], "answer to " + asked[2]], said);
    }

    [Fact]
    public void Every_switch_of_the_pairing_dialog_is_in_what_it_grants()
    {
        Assert.Equal(HexLinkRights.Ports | HexLinkRights.Ai, DawnScene.LinkPairRights(false, false, false, false, true, true));
        Assert.Equal(HexLinkRights.View | HexLinkRights.Edit | HexLinkRights.Predict | HexLinkRights.Ai,
            DawnScene.LinkPairRights(true, false, true, true, false, true));
        Assert.Equal(HexLinkRights.None, DawnScene.LinkPairRights(false, false, false, false, false, false));
    }
}
