using Hex.Core.Git;
using Hex.Shell;

using Horizon.HIDL.Runtime;

namespace Hex.Dawn;

// The self test, git's settings. Editor · Git's rows, each put in force, reset and kept in the file, and what they change,
// a commit with nothing staged asking first or refusing, the summary's warning moving with its length, and the palette's
// commands for blame and for the section itself. Run in the git suite's repository with nothing staged and one file
// nobody added.
internal sealed partial class DawnScene
{
    // The rows of Editor · Git in the order the settings window shows them
    private static readonly string[] GitSettingKeys =
        ["git_blame", "git_autofetch", "git_autofetch_interval", "git_pull_mode", "git_account", "git_confirm_sync", "git_smart_commit", "git_subject_length", "git_line_length"];

    // A command of the Git menu by its label, as the palette and the menu run it
    private MenuItem? GitCommand(string label) => menu.Menus.FirstOrDefault(bar => bar.Title == "Git")?.Everything().Select(pair => pair.Item).FirstOrDefault(item => item.Label == label);

    private void TestGitSettings(HexPointerScript test, string repo)
    {
        // Git Settings... opens the window at Editor · Git, which has its nine rows
        test.Run(() => GitCommand(Said("git.settings_command"))?.OnPicked?.Invoke());
        test.WaitFor(() => SettingsPage?.CurrentGroup == EditorSettingsCatalog.GIT, 3.0f);
        test.Check("settings: Git Settings... in the palette opens the settings at Editor · Git", () =>
            SettingsPage is { } page && page.CurrentGroup == EditorSettingsCatalog.GIT);
        test.Check("settings: Editor · Git has its nine rows", () =>
            SettingsPage!.Settings.Where(setting => setting.Group == EditorSettingsCatalog.GIT).Select(setting => setting.Key).SequenceEqual(GitSettingKeys));

        // Every row changed is put in force and lit as changed, and its reset puts it back
        IRuntimeValue Other(string key) => EditorSettingsCatalog.Usual[key] switch
        {
            BooleanValue flag => Values.Bool(!flag.Value),
            NumberValue number => new NumberValue(key == "git_autofetch_interval" ? 10.0f : number.Value + 10.0f),
            _ => new StringValue(key == "git_pull_mode" ? "rebase" : "never")
        };

        bool allChanged = false, allReset = false;
        test.Run(() =>
        {
            allChanged = GitSettingKeys.All(key => SettingsPage!.Enter(key, Other(key)) && SettingsPage.IsModified!(key));
            allReset = GitSettingKeys.All(key => SettingsPage!.Reset(key) && !SettingsPage.IsModified!(key));
        });
        test.Check("settings: every git row changed is lit as changed, and its reset puts it back", () =>
            allChanged && allReset && settings.GitPullMode == "default" && settings.GitSmartCommit == "ask" && settings.GitAutoFetchInterval == 3.0f
            && settings.GitSubjectLength == 50 && settings.GitLineLength == 72 && settings.GitConfirmSync && settings.GitBlame && settings.GitAccount.Length == 0);
        test.Run(CloseSettings);

        // What they're set to is written to the file and read back
        string file = Path.Combine(testFolder, "git_settings.hor");
        HexSettings? read = null;
        test.Run(() =>
        {
            File.Delete(file);
            var written = new HexSettings(file)
            {
                GitAutoFetchInterval = 7.0f,
                GitPullMode = "ff_only",
                GitConfirmSync = false,
                GitSmartCommit = "never",
                GitSubjectLength = 40,
                GitLineLength = 90,
                GitAccount = "someone"
            };
            written.Save();
            read = new HexSettings(file);
            File.Delete(file);
        });
        test.Check("settings: the git settings are kept in the file and read back", () =>
            read is { GitAutoFetchInterval: 7.0f, GitPullMode: "ff_only", GitConfirmSync: false, GitSmartCommit: "never", GitSubjectLength: 40, GitLineLength: 90, GitAccount: "someone" });

        // Commit with nothing staged asks first, and Cancel stages nothing
        string stage = Said("git.smart_stage");
        test.Run(() => GitCommand(Said("git.commit_command"))?.OnPicked?.Invoke());
        test.WaitFor(() => textDialog is { IsOpen: true } && textDialog.Buttons.Any(button => button.Label == stage), 3.0f);
        test.Check("settings: smart commit ask, a commit with nothing staged asks to stage everything first", () =>
            textDialog is { IsOpen: true } dialog && dialog.Buttons.Any(button => button.Label == stage));
        test.Run(() => textDialog?.Buttons.First(button => button.Label == Said("git.smart_cancel")).OnPressed?.Invoke());
        test.Settle(0.5f);
        test.Check("settings: smart commit ask cancel stages nothing", () =>
            GitProcess.Run(repo, ["diff", "--cached", "--name-only"]).Output.Trim().Length == 0
            && !(textDialog is { IsOpen: true } open && open.Buttons.Any(button => button.Label == Said("git.commit_button"))));

        // Never refuses with a notice, and the Commit row says nothing's staged
        IEnumerable<UITreeItem> SideRows(IEnumerable<UITreeItem> rows) => rows.SelectMany(row => new[] { row }.Concat(SideRows(row.Children ?? [])));
        test.Run(() =>
        {
            chrome.Dialog?.Close();
            new EditorSource(this).Set("git_smart_commit", new StringValue("never"));
            ShowSide(HexSide.SourceControl);
            GitCommand(Said("git.commit_command"))?.OnPicked?.Invoke();
        });
        test.WaitFor(() => SideRows(sideTree.Items).Any(row => row.Text == Said("git.commit_nothing_staged")), 3.0f);
        test.Check("settings: smart commit never refuses with a notice, and the Commit row says nothing is staged", () =>
            messageItem.Text.Contains(Said("git.nothing_staged"), StringComparison.Ordinal) && textDialog is not { IsOpen: true }
            && SideRows(sideTree.Items).Any(row => row.Text == Said("git.commit_nothing_staged")));
        test.Run(() => new EditorSource(this).Set("git_smart_commit", new StringValue("ask")));

        // The summary's warning goes by the length set, 40 characters fine at 50 and too long at 30
        string forty = new('x', 40);
        bool fine = false, warned = false;
        test.Run(() =>
        {
            fine = !git.SummaryNote(forty).Warning;
            settings.GitSubjectLength = 30;
            warned = git.SummaryNote(forty) is { Warning: true } note && note.Text.Contains("30", StringComparison.Ordinal);
            settings.GitSubjectLength = 50;
        });
        test.Check("settings: subject length setting moves the summary warning", () => fine && warned);

        // Toggle Inline Blame from the palette flips the setting and back
        bool flipped = false;
        test.Run(() =>
        {
            MenuItem? toggle = GitCommand(Said("git.blame_toggle"));
            toggle?.OnPicked?.Invoke();
            flipped = toggle is not null && !settings.GitBlame && messageItem.Text.Contains(Said("git.blame_off"), StringComparison.Ordinal);
            toggle?.OnPicked?.Invoke();
        });
        test.Check("settings: toggle blame command flips git_blame", () => flipped && settings.GitBlame);

        // The stash and a merge called off, in a repository of their own so this one stays as it is
        TestGitStash(test);
    }
}
