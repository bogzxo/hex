using Hex.Core.Notebooks;
using Horizon.Core.Threading;

namespace Hex.Dawn;

internal sealed partial class HexLanguages
{
    private void CreatePythonEnvironment()
    {
        string? root = context.Documents.Current.Path is { } file ? ServerRootFor(file) : context.Workspace.Roots.FirstOrDefault();
        if (root is null) return;
        var pythons = KernelSpecs.Pythons();
        if (pythons.Count == 0)
        {
            context.Notices.Notify(context.Strings.Said("languages.python_terminal_missing"), false,
                (context.Strings.Said("debugging.get_python"), () => context.Windows.OpenInBrowser("https://www.python.org/downloads/")));
            return;
        }
        if (pythons.Count == 1) CreatePythonEnvironment(root, pythons[0]);
        else context.Windows.Choose(context.Strings.Said("languages.python_create"), context.Strings.Said("languages.python_base"),
            pythons.Select(python => (python, (Action)(() => CreatePythonEnvironment(root, python)))).ToList());
    }

    internal void CreatePythonEnvironment(string root, string python)
    {
        string key = "python:venv:" + Path.GetFullPath(root);
        if (context.Work.Find(key) is { } going) { context.Work.Renew(going); return; }
        string? interpreter = null;
        context.Work.Start(key, context.Strings.Said("languages.python_creating", ("folder", root)),
            async work => interpreter = await PythonEnvironment.Create(python, root, line => work.Write(line), work.Token), item =>
            {
                foreach (string line in item.Output) context.Notices.Note(line, error: item.State == WorkState.Failed);
                if (item.State == WorkState.Succeeded && interpreter is not null) UsePythonInterpreter(root, interpreter);
                else if (item.State == WorkState.Failed)
                    context.Notices.Say(context.Strings.Said("languages.python_create_failed", ("why", item.Error ?? string.Empty)), error: true);
            });
    }
}
