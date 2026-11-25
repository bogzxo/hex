using Hex.Core;

namespace Hex.Tests;

/// <summary>The pure parts of the Local AI window's two modes: mode and page state, the status sentence, the search filter and the redraw key.</summary>
public sealed class LocalAiWindowStateTests
{
    [Theory]
    [InlineData("advanced", "advanced")]
    [InlineData("Advanced", "advanced")]
    [InlineData("simple", "simple")]
    [InlineData("", "simple")]
    [InlineData(null, "simple")]
    [InlineData("auto", "simple")]
    public void A_mode_is_advanced_only_when_it_says_so(string? value, string expected) =>
        Assert.Equal(expected, LocalAiWindowState.ParseMode(value));

    [Fact]
    public void The_toggle_goes_between_the_two_and_only_advanced_has_a_nav()
    {
        Assert.Equal("advanced", LocalAiWindowState.Toggle("simple"));
        Assert.Equal("simple", LocalAiWindowState.Toggle("advanced"));
        Assert.True(LocalAiWindowState.HasNav("advanced"));
        Assert.False(LocalAiWindowState.HasNav("simple"));
    }

    [Theory]
    [InlineData(false, false, "simple")]
    [InlineData(false, true, "simple")]
    [InlineData(true, false, "simple")]
    [InlineData(true, true, "advanced")]
    public void The_mode_nobody_chose_is_advanced_only_for_someone_set_up_who_changed_advanced_settings(bool setUp, bool changed, string expected) =>
        Assert.Equal(expected, LocalAiWindowState.DefaultMode(setUp, changed));

    [Fact]
    public void Alt_digits_reach_the_pages_in_the_navs_order_and_no_further()
    {
        Assert.Equal(LocalAiPage.Overview, LocalAiWindowState.PageOfDigit(1));
        Assert.Equal(LocalAiPage.Setups, LocalAiWindowState.PageOfDigit(2));
        Assert.Equal(LocalAiPage.Glossary, LocalAiWindowState.PageOfDigit(9));
        Assert.Null(LocalAiWindowState.PageOfDigit(0));
        Assert.Null(LocalAiWindowState.PageOfDigit(10));
        Assert.Equal(9, Enum.GetValues<LocalAiPage>().Length);
    }

    [Theory]
    [InlineData("presets", "Presets")]
    [InlineData("Models", "Models")]
    [InlineData("advanced", "Predictions")]
    [InlineData("nonsense", "Overview")]
    [InlineData(null, "Overview")]
    public void A_page_name_is_read_with_the_old_advanced_page_being_predictions(string? name, string expected) =>
        Assert.Equal(expected, LocalAiWindowState.ParsePage(name).ToString());

    [Theory]
    [InlineData(false, false, false, false, false, false, "NotSetUp")]
    [InlineData(true, false, true, false, false, false, "Running")]
    [InlineData(true, false, true, false, false, true, "Outside")]
    [InlineData(true, false, false, true, false, false, "Starting")]
    [InlineData(true, false, false, false, true, false, "Problem")]
    [InlineData(true, false, false, false, false, false, "Stopped")]
    [InlineData(true, true, true, false, false, false, "Downloading")]
    [InlineData(false, true, false, false, false, false, "Downloading")]
    public void The_status_sentence_follows_what_the_engine_is_doing(bool setUp, bool downloading, bool running, bool starting, bool problem, bool outside, string expected) =>
        Assert.Equal(expected, LocalAiWindowState.StatusOf(setUp, downloading, running, starting, problem, outside).ToString());

    [Fact]
    public void A_status_key_is_the_kinds_lower_case_name()
    {
        Assert.Equal("local_ai.status_notsetup", LocalAiWindowState.StatusKey(LocalAiStatusKind.NotSetUp));
        Assert.Equal("local_ai.status_running", LocalAiWindowState.StatusKey(LocalAiStatusKind.Running));
    }

    [Fact]
    public void Progress_is_bucketed_to_whole_percents_and_minus_one_when_nothing_goes()
    {
        Assert.Equal(-1, LocalAiWindowState.PercentBucket(null));
        Assert.Equal(0, LocalAiWindowState.PercentBucket(0.004f));
        Assert.Equal(42, LocalAiWindowState.PercentBucket(0.426f));
        Assert.Equal(100, LocalAiWindowState.PercentBucket(1.5f));
        Assert.Equal(0, LocalAiWindowState.PercentBucket(-0.5f));
    }

    private static readonly LocalAiRowDef[] Rows =
    [
        new("temperature", LocalAiPage.Presets), new("top_p", LocalAiPage.Presets), new("invocation", LocalAiPage.Predictions), new("engine_port", LocalAiPage.Engine)
    ];

    private static (string Title, string Short, string Long) Texts(string key) => key switch
    {
        "temperature" => ("Temperature", "How adventurous it is", "Lower is steadier. Higher wanders."),
        "top_p" => ("Top P", "Only the likeliest words", "Words inside this share of the probability."),
        "invocation" => ("When to predict", "As you type or when asked", "Typing asks at every pause."),
        _ => ("Port", "Where the server listens", "Change it when another program has it."),
    };

    [Fact]
    public void Search_matches_title_short_and_long_text_in_any_case_and_needs_every_word()
    {
        Assert.Equal(["temperature"], LocalAiWindowState.Filter("TEMPERATURE", Rows, Texts).Select(row => row.Key));
        Assert.Equal(["top_p"], LocalAiWindowState.Filter("probability", Rows, Texts).Select(row => row.Key));
        Assert.Equal(["engine_port"], LocalAiWindowState.Filter("another program", Rows, Texts).Select(row => row.Key));
        Assert.Empty(LocalAiWindowState.Filter("temperature probability", Rows, Texts));
        Assert.Equal(Rows.Length, LocalAiWindowState.Filter("  ", Rows, Texts).Count);
    }

    [Fact]
    public void Search_jumps_to_the_page_of_the_first_match_and_stays_when_the_page_still_matches()
    {
        IReadOnlyList<LocalAiRowDef> found = LocalAiWindowState.Filter("temperature", Rows, Texts);
        Assert.Equal(LocalAiPage.Presets, LocalAiWindowState.JumpTo(found, LocalAiPage.Overview));
        Assert.Equal(LocalAiPage.Presets, LocalAiWindowState.JumpTo(found, LocalAiPage.Presets));
        IReadOnlyList<LocalAiRowDef> both = LocalAiWindowState.Filter("the", Rows, Texts);
        Assert.Equal(LocalAiPage.Engine, LocalAiWindowState.JumpTo(both, LocalAiPage.Engine));
        Assert.Equal(LocalAiPage.Presets, LocalAiWindowState.JumpTo(both, LocalAiPage.Predictions));
        Assert.Null(LocalAiWindowState.JumpTo([], LocalAiPage.Overview));
    }

    [Fact]
    public void The_window_signature_moves_with_the_mode_page_setup_search_open_help_and_download_percent()
    {
        string a = LocalAiWindowState.Signature("simple", LocalAiPage.Overview, "allrounder", "", null, -1, "x");
        Assert.Equal(a, LocalAiWindowState.Signature("simple", LocalAiPage.Overview, "allrounder", "", null, -1, "x"));
        Assert.NotEqual(a, LocalAiWindowState.Signature("advanced", LocalAiPage.Overview, "allrounder", "", null, -1, "x"));
        Assert.NotEqual(a, LocalAiWindowState.Signature("simple", LocalAiPage.Models, "allrounder", "", null, -1, "x"));
        Assert.NotEqual(a, LocalAiWindowState.Signature("simple", LocalAiPage.Overview, "light", "", null, -1, "x"));
        Assert.NotEqual(a, LocalAiWindowState.Signature("simple", LocalAiPage.Overview, "allrounder", "temp", null, -1, "x"));
        Assert.NotEqual(a, LocalAiWindowState.Signature("simple", LocalAiPage.Overview, "allrounder", "", "context", -1, "x"));
        Assert.NotEqual(a, LocalAiWindowState.Signature("simple", LocalAiPage.Overview, "allrounder", "", null, 41, "x"));
        Assert.NotEqual(a, LocalAiWindowState.Signature("simple", LocalAiPage.Overview, "allrounder", "", null, -1, "y"));
        // An unreadable mode is the simple one
        Assert.Equal(a, LocalAiWindowState.Signature("nonsense", LocalAiPage.Overview, "allrounder", "", null, -1, "x"));
    }

    [Fact]
    public void A_download_a_percent_on_changes_the_signature_but_a_tenth_of_a_percent_does_not()
    {
        string At(float progress) => LocalAiWindowState.Signature("simple", LocalAiPage.Setups, "", "", null, LocalAiWindowState.PercentBucket(progress));
        Assert.Equal(At(0.421f), At(0.425f));
        Assert.NotEqual(At(0.421f), At(0.431f));
    }
}
