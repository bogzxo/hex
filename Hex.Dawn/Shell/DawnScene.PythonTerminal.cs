using Hex.Core.Notebooks;
using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private sealed record PythonTerminal(string Interpreter);
    private const string PythonProfilePrefix = "hex-python:";

    private static TerminalProfile PythonProfile(TerminalProfile shell, string python) =>
        new("Python", shell.Command, shell.Icon, PythonProfilePrefix + Uri.EscapeDataString(shell.Id) + ":" + Uri.EscapeDataString(python))
        { Tag = new PythonTerminal(python) };

    private TerminalProfile? RestoredTerminalProfile(string id)
    {
        if (!id.StartsWith(PythonProfilePrefix, StringComparison.Ordinal)) return terminals.ProfileCalled(id);
        try
        {
            string[] parts = id[PythonProfilePrefix.Length..].Split(':', 2);
            if (parts.Length == 2 && terminals.ProfileCalled(Uri.UnescapeDataString(parts[0])) is { Kind: TerminalProfileKind.Shell } shell)
            {
                string python = Uri.UnescapeDataString(parts[1]);
                if (Path.IsPathFullyQualified(python) && File.Exists(python)) return PythonProfile(shell, python);
            }
        }
        catch (Exception error) when (error is UriFormatException or ArgumentException) { }
        Say(Said("languages.python_terminal_unavailable"), error: true);
        return new TerminalProfile(Said("languages.python_terminal_unavailable"), string.Empty, id: id) { Kind = TerminalProfileKind.Embedded };
    }

    private void WirePythonTerminal()
    {
        var environmentOf = terminalPanel.EnvironmentOf;
        terminalPanel.EnvironmentOf = profile => profile.Tag is PythonTerminal python
            ? PythonEnvironment.TerminalEnvironment(python.Interpreter, environmentOf?.Invoke(profile))
            : environmentOf?.Invoke(profile);
        commands.Add("Run/check", Said("languages.python_terminal"), OpenPythonTerminal, icon: "terminal")
            .IsVisible = () => HexDebugging.IsPython(document.Path);
    }

    private void OpenPythonTerminal()
    {
        string folder = document.Path is { } path ? Path.GetDirectoryName(path)! : TerminalFolder();
        string root = explorer.workspace.Folders.Where(candidate => PlatformPaths.IsUnder(folder, candidate))
            .OrderByDescending(candidate => candidate.Length).FirstOrDefault() ?? folder;
        string? python = PythonEnvironment.FindLocal(folder, root) ?? KernelSpecs.Pythons().FirstOrDefault();
        if (python is null) { Say(Said("languages.python_terminal_missing"), error: true); return; }
        var usual = terminals.UsualProfile;
        if (usual is null || usual.Kind != TerminalProfileKind.Shell || usual.Id.StartsWith("wsl", StringComparison.OrdinalIgnoreCase))
        {
            Say(Said("languages.python_terminal_shell"), error: true);
            return;
        }
        var profile = PythonProfile(usual, python);
        ShowPanel(CONSOLE_PAGE);
        terminalPanel.NewTab(profile, root);
    }
}
