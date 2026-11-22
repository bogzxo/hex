using Hex.Core.Git;
using Hex.Shell;
using Horizon.HIDL.Runtime;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestGitSupport(HexPointerScript test)
    {
        if (!GitProcess.Installed)
        {
            test.Run(() => Console.WriteLine("[Hex self-test] git-support skipped: Git is not installed"));
            return;
        }

        string root = Path.Combine(testFolder, "git-support");
        string original = Path.Combine(root, "original.txt");
        string added = Path.Combine(root, "added.txt");
        string activate = EditorSettingsCatalog.GIT_ENABLED;
        test.Run(() =>
        {
            Directory.CreateDirectory(root);
            if (!GitProcess.Run(root, ["init", "-q"]).Ok)
                throw new InvalidOperationException("Could not initialize the Git fixture.");
            File.WriteAllText(original, "Original\n");
            explorer.OpenFolder(root);
            Open(original);
        });
        test.WaitFor(() => git.StatusFor(original) is not null, 12);
        test.Check("git-support: the default discovers and tracks a repository", () =>
            settings.GitEnabled && git.StatusFor(original) is not null && GitProcess.ProbeStarted);

        test.Run(() =>
        {
            ShowSide(HexSide.SourceControl);
            OpenSettings(EditorSettingsCatalog.GIT);
        });
        test.WaitFor(() => SettingsPage?.RowBounds(activate) is not null, 5);
        test.Run(() => SettingsPage!.Enter(activate, Values.Bool(false)));
        test.Check("git-support: disabled controls show why and refuse edits", () =>
            !settings.GitEnabled && GitSettingKeys.All(key => SettingsPage!.DependentShown(key)?.Dimmed == true
                && SettingsPage.NoteShown(key) == Said("settings_catalog.git_required"))
            && !SettingsPage!.Enter("git_blame", Values.Bool(false)) && settings.GitBlame);
        test.Run(() =>
        {
            File.WriteAllText(added, "Written while off\n");
            git.RefreshAll();
        });
        test.Check("git-support: turning off clears repository state and does not restart the Git probe", () =>
            git.StatusFor(original) is null && git.RepositoryFor(added) is null
            && GitProcess.InstalledNow == false && !GitProcess.ProbeStarted && branchItem.Text.Length == 0);

        test.Run(() => SettingsPage!.Enter(activate, Values.Bool(true)));
        test.WaitFor(() => git.StatusFor(original) is not null && git.StateOf(added).Change == GitChange.Untracked, 12);
        test.Check("git-support: turning on rediscovers the repository and changes made while off", () =>
            settings.GitEnabled && GitProcess.ProbeStarted && git.StateOf(added).Change == GitChange.Untracked);
        test.Check("git-support: dependent controls become editable again", () =>
            SettingsPage!.NoteShown("git_blame") is null && SettingsPage.DependentShown("git_blame")?.Dimmed == false
            && SettingsPage.Enter("git_blame", Values.Bool(false)) && !settings.GitBlame);
        test.Run(() =>
        {
            SettingsPage!.Reset("git_blame");
            CloseSettings();
        });
    }
}
