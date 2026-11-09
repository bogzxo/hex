using Hex.Core.Notebooks;
using Horizon.Core.Threading;

namespace Hex.Dawn;

internal sealed partial class HexDebugging
{
    private void OfferPythonDebugger(string file, Action then)
    {
        if (context.Work.Find("debugpy:install") is { } going)
        {
            context.Work.Renew(going);
            return;
        }
        context.Notices.Notify(context.Strings.Said("debugging.python_setup_offer"), false,
            (context.Strings.Said("debugging.python_setup_button"), () => InstallPythonDebugger(file, then)));
    }

    private void InstallPythonDebugger(string file, Action then)
    {
        if (HexHost.NoDownloads("debugpy") is { } unavailable)
        {
            context.Notices.Say(unavailable, error: true);
            return;
        }
        if (KernelSpecs.Pythons().FirstOrDefault() is not { } python)
        {
            context.Notices.Notify(context.Strings.Said("debugging.python_required"), false,
                (context.Strings.Said("debugging.get_python"), () => context.Windows.OpenInBrowser("https://www.python.org/downloads/")));
            return;
        }
        context.Work.Start("debugpy:install", context.Strings.Said("debugging.python_installing"),
            work => PythonDebuggerSetup.Install(python, PythonDebuggerSetup.Folder, line => work.Write(line), work.Token), item =>
            {
                foreach (string line in item.Output) context.Notices.Note(line, error: item.State == WorkState.Failed);
                if (item.State == WorkState.Succeeded && !Debugging && (File.Exists(file) || Directory.Exists(file))) then();
                else if (item.State == WorkState.Failed)
                    context.Notices.Say(context.Strings.Said("debugging.launch_failed", ("why", item.Error ?? "debugpy installation failed")), error: true);
            });
    }
}
