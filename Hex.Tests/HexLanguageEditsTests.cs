using Hex.Core;

namespace Hex.Tests;

/// <summary>
/// A game's language file written by key (HexLanguageEdits, what double clicking a text on the canvas does), the
/// comments and the spacing left alone and the file read back as a language afterwards.
/// </summary>
public sealed class HexLanguageEditsTests
{
    private const string SAMPLE = """
        // The game's texts
        let strings = {
            // the first screen
            main_menu: {
                play: "Play", // big and green
                quit: "Quit"
            },
            help: {
                page: [
                    "# Help",
                    "Press things."
                ]
            }
        };
        """;

    private static readonly string Sample = SAMPLE.ReplaceLineEndings("\n");

    // What the file says by a key once it's read the way a game reads it
    private static string? Said(string text, string key)
    {
        string file = Path.Combine(Path.GetTempPath(), $"hex-language-test-{Guid.NewGuid():N}.hor");
        File.WriteAllText(file, text);
        try
        {
            return Horizon.Content.Localisation.ReadFile(file).TryGetValue(key, out string? said) ? said : null;
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void A_text_is_changed_where_it_is_written_and_nothing_else_moves()
    {
        string? made = HexLanguageEdits.SetText(Sample, null, "main_menu.play", "Start", out _);
        Assert.Equal(Sample.Replace("play: \"Play\"", "play: \"Start\""), made);
        Assert.Equal("Start", Said(made!, "main_menu.play"));
    }

    [Fact]
    public void Quotes_and_backslashes_are_escaped()
    {
        string? made = HexLanguageEdits.SetText(Sample, null, "main_menu.quit", "Say \"bye\" C:\\", out _);
        Assert.NotNull(made);
        Assert.Contains("quit: \"Say \\\"bye\\\" C:\\\\\"", made);
        Assert.Equal("Say \"bye\" C:\\", Said(made, "main_menu.quit"));
    }

    [Fact]
    public void A_text_of_lines_stays_a_list_of_lines()
    {
        string? made = HexLanguageEdits.SetText(Sample, null, "help.page", "# Help\nPress buttons.", out _);
        Assert.NotNull(made);
        Assert.Equal("# Help\nPress buttons.", Said(made, "help.page"));
        Assert.Contains("\"Press buttons.\"", made);
    }

    [Fact]
    public void A_missing_key_goes_into_its_section_and_a_missing_section_is_made()
    {
        string? key = HexLanguageEdits.SetText(Sample, null, "main_menu.options", "Options", out _);
        Assert.NotNull(key);
        Assert.Equal("Options", Said(key, "main_menu.options"));
        Assert.Contains("// big and green", key);

        string? section = HexLanguageEdits.SetText(Sample, null, "pause.resume", "Resume", out _);
        Assert.NotNull(section);
        Assert.Equal("Resume", Said(section, "pause.resume"));
        Assert.Equal("Quit", Said(section, "main_menu.quit"));
    }

    [Fact]
    public void A_key_through_a_text_or_a_broken_file_says_why()
    {
        Assert.Null(HexLanguageEdits.SetText(Sample, null, "main_menu.play.loud", "PLAY", out string why));
        Assert.NotEmpty(why);

        Assert.Null(HexLanguageEdits.SetText("let strings = { a: \"b\"", null, "a", "c", out why));
        Assert.Contains("doesn't run", why);

        Assert.Null(HexLanguageEdits.SetText(Sample, null, "main_menu..play", "c", out why));
    }

    [Fact]
    public void A_key_is_found_on_its_own_line_and_taken_out_again()
    {
        Assert.Equal((4, 8), HexLanguageEdits.Find(Sample, null, "main_menu.play"));
        Assert.Equal((5, 8), HexLanguageEdits.Find(Sample, null, "main_menu.quit"));
        Assert.Null(HexLanguageEdits.Find(Sample, null, "main_menu.nope"));

        string? added = HexLanguageEdits.SetText(Sample, null, "main_menu.options", "Options", out _);
        string? removed = HexLanguageEdits.RemoveText(added!, null, "main_menu.options", out _);
        Assert.Equal(Sample, removed);
    }
}
