using Horizon.UI.Components;

namespace Hex.Dusk;

// The self test of the dawn command, typed into every shell there is with its integration (PowerShell, cmd, Git Bash
// and a WSL distribution when they're on the machine). dawn file edits the file in place of the pane, Ctrl+S's save
// writes it, Ctrl+Q's close is stopped once by changes that aren't saved, and dawn -open asks for a Dawn, which the self
// test hasn't got (DUSK_HEX is none), so it's said that there isn't one and nothing else happens.
internal sealed partial class DuskScene
{
    private void TestDawn(DuskScript test)
    {
        TerminalView View() => panel.FocusedView!;
        string folder = Path.Combine(Path.GetTempPath(), "dusk-dawn-selftest");

        test.Run(() =>
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
            Directory.CreateDirectory(folder);
        });

        // The shells by their profiles, and the first WSL distribution, whose paths go through wslpath
        string[] shells = ["pwsh", "powershell", "cmd", "git_bash", .. panel.Profiles.Where(profile => profile.Id.StartsWith("wsl:", StringComparison.Ordinal)).Take(1).Select(profile => profile.Id)];
        foreach (string id in shells)
        {
            TerminalProfile? shell = panel.Profiles.Find(profile => profile.Id == id);
            if (shell is null)
                continue;

            string name = $"edited-{id.Replace(':', '-')}.txt";
            string file = Path.Combine(folder, name);
            test.Run(() => panel.NewTab(shell, folder));
            test.WaitFor(() => View().Profile == shell && View().Screen.MarksSeen && View().Screen.AtPrompt, 15.0f);
            test.Run(() => View().Send($"dawn {name}\r"));
            test.WaitFor(() => View().InlineEditor is not null, 8.0f);
            test.Check($"dawn: in {shell.Name}, dawn file edits it in place of the pane", () =>
                View().InlineEditor is not null && string.Equals(View().InlinePath, file, StringComparison.OrdinalIgnoreCase));

            test.Run(() => View().InlineEditor!.Document.Insert($"written in {id}"));
            test.Check($"dawn: in {shell.Name}, a change that isn't saved stops the close once", () => View().InlineChanged && !View().CloseInline() && View().InlineEditor is not null);
            test.Run(() => View().SaveInline());
            test.Check($"dawn: in {shell.Name}, and the save writes it", () => File.Exists(file) && File.ReadAllText(file) == $"written in {id}" && !View().InlineChanged);
            test.Run(() => View().CloseInline());
            test.WaitFor(() => View().InlineEditor is null && View().Screen.AtPrompt, 5.0f);
            test.Check($"dawn: in {shell.Name}, and closed it's the shell again", () => View().InlineEditor is null && View().Screen.AtPrompt);
        }

        // There's no Dawn in the self test, which is said rather than anything opening
        string? said = null;
        test.Run(() =>
        {
            File.WriteAllText(Path.Combine(folder, "opened.txt"), "opened");
            said = null;
            host.Requested(View(), $"dawn;open;{Path.Combine(folder, "opened.txt")}");
            said = messageItem.Text;
        });
        test.Check("dawn: -open with no Dawn to be had says so", () => said?.Contains("no Dawn", StringComparison.Ordinal) == true);
    }
}
