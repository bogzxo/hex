using System.Diagnostics;
using System.Numerics;
using Hex.Installation;
using Horizon.Core;
using Horizon.Engine;

namespace Hex.Installer;

internal sealed record SetupOptions(string Target, bool Uninstall, bool Update, string? Version, int? Parent, long ParentStart, bool Preview)
{
    public static SetupOptions Parse(string[] args)
    {
        string target = InstallPaths.DefaultRoot;
        string? version = null;
        bool uninstall = false, update = false, preview = false;
        int? parent = null;
        long start = 0;
        for (int i = 0; i < args.Length; i++)
        {
            string Value() => ++i < args.Length ? args[i] : throw new ArgumentException(InstallText.Get("errors.argument_value"));
            switch (args[i])
            {
                case "--target": target = Path.GetFullPath(Value()); break;
                case "--uninstall": uninstall = true; break;
                case "--update": update = true; break;
                case "--version": version = Value(); break;
                case "--wait-pid": parent = int.Parse(Value()); break;
                case "--wait-start": start = long.Parse(Value()); break;
                case "--preview": preview = true; break;
                default: throw new ArgumentException(InstallText.Get("errors.option", ("option", args[i])));
            }
        }
        if (uninstall && update || parent.HasValue && (parent <= 0 || start <= 0) || update && (parent is null || version is null))
            throw new ArgumentException(InstallText.Get("errors.handoff"));
        if (target.Contains('\n') || target.Contains('\r') || target.Contains('"')) throw new ArgumentException(InstallText.Get("errors.destination"));
        if (update || uninstall) _ = InstallTransaction.ReadManifest(target);
        return new(target, uninstall, update, version, parent, start, preview);
    }
}

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            InstallerStrings.Read();
            var options = SetupOptions.Parse(args);
            InstallPaths.ValidateRid(InstallPaths.Rid);
            // An installed uninstaller cannot remove its own native libraries on Windows. Run its copy instead.
            string installed = Path.GetFullPath(Path.Combine(options.Target, "installer"));
            if (Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory).Equals(installed, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                string copied = InstallerHandoff.CopyInstaller(installed);
                var start = new ProcessStartInfo(copied) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(copied)! };
                foreach (string arg in args) start.ArgumentList.Add(arg);
                Process.Start(start);
                return 0;
            }
            Directory.SetCurrentDirectory(AppContext.BaseDirectory);
            using var engine = new GameEngine(GameEngineConfiguration.Default with
            {
                Gamepads = false,
                WindowConfiguration = WindowManagerConfiguration.Default1600x900 with
                {
                    WindowTitle = InstallText.Get("window.title"), WindowIcon = "Assets/hex_icon.png", AppId = "hex-setup",
                    WindowSize = new Vector2(1000, 680), CustomFrame = true, ScaleToDisplay = true,
                    Mode = RunMode.Application, Behaviour = ApplicationBehaviour.All, VSync = true
                }
            });
            engine.SetScene(new InstallerScene(options));
            engine.Run();
            return Environment.ExitCode;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e.Message);
            // WinExe has no console when opened from Installed Apps. Keep an actionable diagnostic on disk.
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "hex-setup-error.txt"), e.ToString());
            return 1;
        }
    }
}
