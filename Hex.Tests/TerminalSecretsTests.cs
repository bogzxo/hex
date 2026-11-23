using Hex.Core;
using Hex.Dawn;

using Horizon.UI.Components;

namespace Hex.Tests;

/// <summary>
/// Passwords typed in the terminal: recognised from the prompt and from the keys not being echoed, so they are never kept
/// in the history or shown to a prediction, and the terminal's predictions that keep clear of them.
/// </summary>
public sealed class TerminalSecretsTests
{
    [Theory]
    [InlineData("[sudo] password for bob: ")]
    [InlineData("Password:")]
    [InlineData("bob@host's password: ")]
    [InlineData("Enter passphrase for key '/home/bob/.ssh/id_ed25519': ")]
    [InlineData("Enter PIN: ")]
    [InlineData("Password for 'https://github.com': ")]
    [InlineData("Enter passphrase (empty for no passphrase): ")]
    [InlineData("Password: *****")]
    [InlineData("Verification code: ")]
    [InlineData("PIN:")]
    [InlineData("Password for 'https://u@github.com':")]
    public void A_prompt_for_a_secret_is_known(string prompt) => Assert.True(TerminalSecrets.IsSecretPrompt(prompt));

    [Theory]
    [InlineData("bob@pc:~/passwords$ ")]
    [InlineData("PS C:\\src\\credentials>")]
    [InlineData("user@host:~/fix-api-key (fix-api-key)$ ")]
    [InlineData("~/secrets # ")]
    [InlineData("Enter the password")]
    [InlineData("PS C:\\code> ")]
    [InlineData("user@host:~$ ")]
    [InlineData("C:\\Users\\bob>")]
    [InlineData("")]
    [InlineData("Compiling shapes: ")]
    public void An_ordinary_prompt_is_not(string prompt) => Assert.False(TerminalSecrets.IsSecretPrompt(prompt));

    [Fact]
    public void A_command_that_ends_in_the_word_is_a_command_and_not_a_prompt()
    {
        // The row is the prompt and what was typed, and what was typed is on its end
        Assert.False(TerminalSecrets.IsSecretPrompt("PS C:\\> git log --grep=password", "git log --grep=password"));
        Assert.False(TerminalSecrets.IsSecretPrompt("$ echo reset your pin", "echo reset your pin"));
        Assert.False(TerminalSecrets.IsSecret("git log --grep=password", "PS C:\\> git log --grep=password"));
    }

    [Fact]
    public void Keys_that_did_not_come_back_onto_the_row_were_not_echoed()
    {
        Assert.False(TerminalSecrets.WasEchoed("hunter2", "Enter value: "));
        Assert.True(TerminalSecrets.WasEchoed("ls -la", "$ ls -la"));
        Assert.True(TerminalSecrets.WasEchoed("", "$ "));
    }

    [Fact]
    public void A_line_typed_at_a_secret_prompt_is_a_secret_by_either_sign()
    {
        // By the prompt, though what was typed is drawn as stars
        Assert.True(TerminalSecrets.IsSecret("hunter2", "[sudo] password for bob: *******"));
        // By the echo alone, the prompt is nothing the list knows
        Assert.True(TerminalSecrets.IsSecret("hunter2", "Enter the thing: "));
        // An ordinary line is neither
        Assert.False(TerminalSecrets.IsSecret("dotnet build", "PS C:\\code> dotnet build"));
        Assert.False(TerminalSecrets.IsSecret("", "PS C:\\code> "));
    }

    [Fact]
    public void The_history_does_not_keep_what_was_typed_at_a_prompt_for_a_password()
    {
        // The host asks before it adds, see TerminalHost.Typed; this is what it asks
        var history = new TerminalHistory(null);
        foreach ((string typed, string row) in new[] { ("git status", "$ git status"), ("hunter2", "[sudo] password for bob: "), ("ls", "$ ls") })
        {
            if (!TerminalSecrets.IsSecret(typed, row))
                history.Add(typed);
        }

        Assert.Equal(["git status", "ls"], history.Lines);
    }

    [Fact]
    public void Ghost_text_has_no_control_characters_or_escapes()
    {
        Assert.Equal("ls -la", TerminalView.CleanGhost("ls\u0007 -la\u007f"));
        Assert.Equal("a b", TerminalView.CleanGhost("a\r\nb"));
        Assert.Null(TerminalView.CleanGhost("x\u001b[31m"));
        Assert.Null(TerminalView.CleanGhost("x\u009b"));
        Assert.Null(TerminalView.CleanGhost("\u0007  "));
    }

    [Fact]
    public void Commands_that_carry_a_credential_are_not_sent_to_the_model()
    {
        string[] secret =
        [
            "export API_KEY=abc", "curl -H 'Authorization: Basic x' u", "curl -H \"authorization: Bearer abc\" u", "mysql -uroot -phunter2",
            "git clone https://bob:pw@github.com/a/b", "AWS_SECRET_ACCESS_KEY=x aws s3 ls", "echo $GITHUB_TOKEN", "export GH_HOST=x", "set PASSWORD=1"
        ];
        foreach (string command in secret)
            Assert.True(HexTerminalPredictionGlue.LooksSecret(command), command);

        foreach (string command in new[] { "git status", "dotnet build -c Release", "cd ~/code", "git clone https://github.com/a/b", "ls -p" })
            Assert.False(HexTerminalPredictionGlue.LooksSecret(command), command);

        Assert.Equal(["git status", "ls"], HexTerminalPredictionGlue.Lines(["git status", "export TOKEN=1", "ls"], "")[..2]);
    }

    [Fact]
    public void Terminal_predictions_are_off_until_switched_on_and_need_predictions_on()
    {
        var settings = new HexSettings(null);
        Assert.False(settings.EditPredictionsTerminal);
        Assert.False(HexTerminalPredictionGlue.On(settings));

        settings.EditPredictionsTerminal = true;
        Assert.False(HexTerminalPredictionGlue.On(settings));

        settings.EditPredictions = true;
        Assert.True(HexTerminalPredictionGlue.On(settings));

        settings.EditPredictionsWorkspace = false;
        Assert.False(HexTerminalPredictionGlue.On(settings));
    }

    [Fact]
    public void Nothing_is_predicted_at_a_prompt_for_a_password_or_away_from_a_shells_prompt()
    {
        Assert.True(HexTerminalPredictionGlue.Gate(true, true, true, false, true, "git ch"));
        Assert.False(HexTerminalPredictionGlue.Gate(false, true, true, false, true, "git ch"));
        Assert.False(HexTerminalPredictionGlue.Gate(true, true, true, true, true, "hunter2"));
        Assert.False(HexTerminalPredictionGlue.Gate(true, true, false, false, true, "git ch"));
        Assert.False(HexTerminalPredictionGlue.Gate(true, false, true, false, true, "git ch"));
        Assert.False(HexTerminalPredictionGlue.Gate(true, true, true, false, false, "git ch"));
        Assert.False(HexTerminalPredictionGlue.Gate(true, true, true, false, true, "  "));
    }

    [Fact]
    public void The_model_is_shown_the_last_commands_and_the_line()
    {
        var history = new List<string>();
        for (int i = 0; i < 30; i++)
            history.Add("cmd" + i);

        string[] lines = HexTerminalPredictionGlue.Lines(history, "git ch");
        Assert.Equal(HexTerminalPredictionGlue.HISTORY_LINES + 1, lines.Length);
        Assert.Equal("cmd10", lines[0]);
        Assert.Equal("git ch", lines[^1]);
    }

    [Fact]
    public void A_single_insertion_at_the_end_is_the_ghost_text()
    {
        string[] lines = ["ls", "git ch"];
        int end = "ls\ngit ch".Length;
        PredictionResult Result(PredictionEdit edit, bool ghost = true) => new(new object(), 1, [edit], null, ghost, false, default);

        Assert.Equal("eckout main", HexTerminalPredictionGlue.Ghost(Result(new(end, end, "eckout main\nnext")), lines));
        Assert.Null(HexTerminalPredictionGlue.Ghost(Result(new(end - 1, end, "x")), lines));
        Assert.Null(HexTerminalPredictionGlue.Ghost(Result(new(end, end, "x"), ghost: false), lines));
        Assert.Null(HexTerminalPredictionGlue.Ghost(Result(new(end, end, "  ")), lines));
    }
}
