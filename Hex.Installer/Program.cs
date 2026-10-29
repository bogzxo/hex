using System.Diagnostics;
using System.Numerics;
using Hex.Installation;
using Horizon.Core;
using Horizon.Engine;

namespace Hex.Installer;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            InstallerHandoff.WaitForLauncher();
            if (WindowsSetupBundle.TryRun(args, out int result)) return result;
            InstallerStrings.Read();
            var options = SetupOptions.Parse(args);
            InstallPaths.ValidateRid(InstallPaths.Rid);
            // An installed uninstaller cannot remove its own native libraries on Windows. Run its copy instead.
            string installed = Path.GetFullPath(Path.Combine(options.Target, "installer"));
            if (Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory).Equals(installed, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                string copied = InstallerHandoff.CopyInstaller(installed);
                InstallerHandoff.StartInstallerCopy(copied, args);
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
        finally { InstallerHandoff.CleanupCopyOnExit(); }
    }
}
