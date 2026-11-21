using Hex.Core;

namespace Hex.Tests;

/// <summary>The last 64 predictions by the text round the caret.</summary>
public sealed class PredictionCacheTests
{
    [Fact]
    public void The_same_text_round_the_caret_finds_the_edits_moved_to_where_the_caret_is()
    {
        var cache = new PredictionCache();
        string tail = new string('x', 2500) + "\nint x = ";
        cache.Put(PredictionCache.Key(tail, tail.Length, "m", "qwen"), tail.Length, [new PredictionEdit(tail.Length, tail.Length, "1;")], tail.Length + 2);

        string further = "// a header\n" + tail;
        Assert.True(cache.TryGet(PredictionCache.Key(further, further.Length, "m", "qwen"), further.Length, out var edits, out int? caret));
        Assert.Equal(new PredictionEdit(further.Length, further.Length, "1;"), Assert.Single(edits));
        Assert.Equal(further.Length + 2, caret);

        string text = "int x = ";
        Assert.False(cache.TryGet(PredictionCache.Key(text, 8, "m", "qwen"), 8, out _, out _));
        Assert.False(cache.TryGet(PredictionCache.Key(text, 8, "other", "qwen"), 8, out _, out _));
    }

    [Fact]
    public void It_keeps_the_64_used_last()
    {
        var cache = new PredictionCache();
        for (int i = 0; i < 70; i++)
            cache.Put(PredictionCache.Key("text" + i, 0, "m", "f"), 0, [new PredictionEdit(0, 0, "x")], null);

        Assert.Equal(64, cache.Count);
        Assert.False(cache.TryGet(PredictionCache.Key("text0", 0, "m", "f"), 0, out _, out _));
        Assert.True(cache.TryGet(PredictionCache.Key("text69", 0, "m", "f"), 0, out _, out _));
    }

    [Fact]
    public void Edits_past_what_the_key_covers_arent_kept()
    {
        var cache = new PredictionCache();
        string text = new('a', 5000);
        cache.Put(PredictionCache.Key(text, 0, "m", "f"), 0, [new PredictionEdit(4000, 4000, "x")], null);

        Assert.Equal(0, cache.Count);
    }
}
