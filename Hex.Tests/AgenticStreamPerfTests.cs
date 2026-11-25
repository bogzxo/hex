using Hex.Core;
using Hex.Dawn;
using Horizon.UI.Components;

namespace Hex.Tests;

/// <summary>
/// Unit and allocation tests for agentic streaming append/coalescing, incremental markdown splitting,
/// placeholder pooling, and zero-allocation status/duration formatting helpers.
/// </summary>
public sealed class AgenticStreamPerfTests
{
    [Fact]
    public void Streaming_append_coalesces_chunks_and_tracks_length_lazily()
    {
        var chat = new HexAgentChat(1, "claude_code");
        chat.Ask("Stream a long reply");

        HexAgentEntry first = chat.Say("Hello ", append: true);
        Assert.Equal(6, first.TextLength);

        HexAgentEntry same = chat.Say("world\n\nSecond paragraph", append: true);
        Assert.Same(first, same);
        Assert.Equal("Hello world\n\nSecond paragraph".Length, same.TextLength);
        Assert.Equal(2, chat.Entries.Count); // User + single coalesced Text entry
        Assert.Equal("Hello world\n\nSecond paragraph", same.Text);

        HexAgentEntry thought = chat.Think("step 1", append: true);
        HexAgentEntry thought2 = chat.Think(" -> step 2", append: true);
        Assert.Same(thought, thought2);
        Assert.Equal("step 1 -> step 2", thought.Text);
    }

    [Fact]
    public void Chat_pending_and_failed_flags_stay_exact_across_lifecycle()
    {
        var chat = new HexAgentChat(2, "claude_code");
        chat.Ask("first turn");
        Assert.False(chat.HasPending);
        Assert.False(chat.Failed);

        HexAgentEntry approval = chat.Approval("Run tests", "dotnet test", _ => { });
        Assert.True(chat.HasPending);

        HexAgentEntry question = chat.Question("Pick one", ["A", "B"], 0, 30, _ => { });
        Assert.True(chat.HasPending);

        Assert.True(chat.Decide(approval, allowed: true));
        Assert.True(chat.HasPending); // question still pending

        Assert.True(chat.Reply(question, "A"));
        Assert.False(chat.HasPending);

        chat.Error("something broke");
        Assert.True(chat.Failed);
        chat.Done();
        Assert.True(chat.Failed);

        // New turn clears Failed
        chat.Ask("second turn");
        Assert.False(chat.Failed);

        // Restore recomputes flags
        var restoredError = new HexAgentEntry(HexAgentEntryKind.Error, "restored err");
        var restoredApproval = new HexAgentEntry(HexAgentEntryKind.Approval, "confirm") { Answer = _ => { } };
        chat.Restore("restored", [restoredError, restoredApproval]);
        Assert.True(chat.Failed);
        Assert.True(chat.HasPending);

        chat.Clear();
        Assert.False(chat.Failed);
        Assert.False(chat.HasPending);
    }

    [Fact]
    public void StableMarkdownSplit_respects_fences_and_scans_incrementally()
    {
        string text = "First paragraph.\n\n```csharp\nvar a = 1;\n\nvar b = 2;\n```\n\nTail line";
        int split1 = HexAgentRows.StableMarkdownSplit(text);
        Assert.Equal("First paragraph.\n\n```csharp\nvar a = 1;\n\nvar b = 2;\n```\n\n", text[..split1]);
        Assert.Equal("Tail line", text[split1..]);

        // Inside an unclosed fence, blank lines do not advance the split point
        string openFence = "Intro.\n\n```\nline 1\n\nline 2";
        int splitOpen = HexAgentRows.StableMarkdownSplit(openFence);
        Assert.Equal("Intro.\n\n", openFence[..splitOpen]);

        // Incremental scan from previous split point yields the same result as full scan
        string grown = text + "\n\nNew tail";
        int full = HexAgentRows.StableMarkdownSplit(grown);
        int delta = HexAgentRows.StableMarkdownSplit(grown, split1);
        Assert.Equal(full, delta);
        Assert.Equal("New tail", grown[delta..]);
    }

    [Fact]
    public void HasColonDigit_fast_path_filters_plain_markdown()
    {
        Assert.False(DawnScene.HasColonDigit("Looking at **the loader** with `code` and a note: nothing here."));
        Assert.False(DawnScene.HasColonDigit("https://example.com/path"));
        Assert.True(DawnScene.HasColonDigit("See Hex.Core/Agents/HexAgentChat.cs:42 for details"));
        Assert.True(DawnScene.HasColonDigit("`File.cs:1`"));
    }

    [Fact]
    public void Formatting_helpers_are_exact_and_zero_allocation_on_hot_ranges()
    {
        // Warm up static caches before measuring allocations
        Assert.Equal("0", HexAgentRows.SmallInt(0));
        Assert.Equal("42", HexAgentRows.SmallInt(42));
        Assert.Equal("256", HexAgentRows.SmallInt(256));
        Assert.Equal("0.0s", HexAgentRows.FormatTenthsSeconds(0.0));
        Assert.Equal("12.4s", HexAgentRows.FormatTenthsSeconds(12.44));
        Assert.Equal("60.0s", HexAgentRows.FormatTenthsSeconds(60.0));
        Assert.Equal("0s", HexAgentRows.FormatDurationShort(0.0));
        Assert.Equal("45s", HexAgentRows.FormatDurationShort(45.2));
        Assert.Equal("5m", HexAgentRows.FormatDurationShort(300.0));
        Assert.Equal("1h 5m", HexAgentRows.FormatDurationShort(3900.0));

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200; i++)
        {
            _ = HexAgentRows.SmallInt(i);
            _ = HexAgentRows.FormatTenthsSeconds(i * 0.1);
            _ = HexAgentRows.FormatDurationShort(i % 60);
            _ = HexAgentRows.FormatDurationShort((i % 59) * 60.0 + 60.0);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void RowList_reuses_pooled_placeholders_across_chat_switches()
    {
        var stack = new VirtualStack();
        var list = new HexAgentRowList(stack);

        Panel firstWaiting = list.RentWaitingPlace();
        Panel firstGrouped = list.RentGroupedPlace();
        list.InsertLater(0, firstWaiting, new object());
        list.Insert(1, firstGrouped, new object());

        list.Clear();

        Panel secondWaiting = list.RentWaitingPlace();
        Panel secondGrouped = list.RentGroupedPlace();
        Assert.Same(firstWaiting, secondWaiting);
        Assert.Same(firstGrouped, secondGrouped);
        Assert.True(secondWaiting.Visible);
        Assert.False(secondGrouped.Visible);
    }
}
