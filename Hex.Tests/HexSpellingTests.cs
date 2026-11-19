using Hex.Core;
using Horizon.UI.Components;

namespace Hex.Tests;

public sealed class HexSpellingTests
{
    [Fact]
    public void Technical_text_is_hidden_without_moving_utf16_prose_columns()
    {
        const string source = "😀 naïve can’t recieve @user /compact ParserState snake_case https://example.com [word](target-name) \u0060x + y\u0060";
        string masked = HexSpelling.Mask(source, inlineCode: true);

        Assert.Equal(source.Length, masked.Length);
        Assert.Equal(source.IndexOf("recieve", StringComparison.Ordinal), masked.IndexOf("recieve", StringComparison.Ordinal));
        Assert.Contains("naïve can’t", masked, StringComparison.Ordinal);
        Assert.Contains("[word", masked, StringComparison.Ordinal);
        foreach (string skip in new[] { "@user", "/compact", "ParserState", "snake_case", "example.com", "target-name", "x + y" })
            Assert.DoesNotContain(skip, masked, StringComparison.Ordinal);
    }

    [Fact]
    public void A_viewport_starting_inside_a_fence_does_not_spell_check_code()
    {
        const string markdown = "Intro\n~~~~csharp\nstring spelchek;\n~~~\nstillcode\n~~~~\nThis is prose.\n    indentedcode";
        var visible = HexSpelling.MarkdownLines(markdown, 2, 7, CancellationToken.None);

        Assert.Equal(new SpellingLine(6, "This is prose."), Assert.Single(visible));
        Assert.Empty(HexSpelling.MarkdownLines(markdown, 0, 7, new CancellationToken(canceled: true)));
    }

    [Fact]
    public void Replacement_uses_the_editor_undo_transaction_and_utf16_columns()
    {
        var document = new UITextDocument { Text = "😀 recieve this" };
        Assert.True(document.ApplyEdits([new UITextEdit(new(0, 3), new(0, 10), "receive")]));
        Assert.Equal("😀 receive this", document.Text);
        document.Undo();
        Assert.Equal("😀 recieve this", document.Text);
    }

    [Fact]
    public void Windows_dictionary_returns_the_misspelling_at_its_original_column_with_suggestions()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var result = WindowsSpelling.Check("en-US", [new(12, "This is a speling mistake.")], CancellationToken.None);
        const int AccessDenied = unchecked((int)0x80070005);
        const int UnsupportedLanguage = unchecked((int)0x80070057);
        const int FactoryNotRegistered = unchecked((int)0x80040154);
        if (result.Language is null && result.Error is AccessDenied or UnsupportedLanguage or FactoryNotRegistered)
            return; // Headless build hosts may lack access to the installed Windows language features.

        Assert.True(result.Language == "en-US", $"Expected the en-US dictionary, Windows returned 0x{result.Error:X8}.");
        var issue = Assert.Single(result.Issues);
        Assert.Equal((12, 10, 7, "speling"), (issue.Line, issue.Column, issue.Length, issue.Word));
        Assert.NotEmpty(issue.Suggestions);
    }

    [Fact]
    public void Unavailable_windows_dictionary_is_reported_without_marking_text()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var result = WindowsSpelling.Check("x-hex-unsupported", [new(0, "recieve")], CancellationToken.None);
        Assert.Null(result.Language);
        Assert.Empty(result.Issues);
    }
}
