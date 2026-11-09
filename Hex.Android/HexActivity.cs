using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Views;

using Hex.Dawn;

using Horizon.Engine;
using Horizon.Platform.Android;

namespace Hex.Android;

/// <summary>
/// Hex on a tablet, Dawn the editor. Everything Android is HorizonActivity's (the surface, input from the keyboard, the
/// mouse and the screen, the lifecycle, the logs). This puts Hex's files where Dawn expects them (HexAndroidFiles), says
/// what a tablet can't do (HexHost), and hands the engine the editor. The system's document picker comes back here too,
/// see HexDocuments.
/// </summary>
[Activity(
    Name = "dev.horizon.hex.HexActivity",
    Label = "Hex",
    Icon = "@mipmap/ic_launcher",
    RoundIcon = "@mipmap/ic_launcher_round",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTask,
    ScreenOrientation = ScreenOrientation.FullUser,
    ResizeableActivity = true,
    WindowSoftInputMode = SoftInput.AdjustNothing | SoftInput.StateHidden,
    Theme = HorizonActivity.THEME,
    ConfigurationChanges = HorizonActivity.HANDLED_CHANGES)]
public sealed class HexActivity : HorizonActivity
{
    private GameEngine? made;

    public override GameEngineConfiguration Configure()
    {
        HexAndroidFiles.Prepare(this);
        HexDocuments.Attach(this);
        Horizon.Logging.Log.Info($"[Hex] Runtime {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}, "
            + $"dynamic code supported={System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported}, "
            + $"compiled={System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeCompiled}.");
        return DawnProgram.Hosted(base.Configure());
    }

    public override GameEngine MakeEngine(GameEngineConfiguration configuration) => made = base.MakeEngine(configuration);

    public override Scene MakeFirstScene()
    {
        GameEngine engine = made ?? throw new InvalidOperationException("The engine wasn't made before the editor was asked for.");
        HexAndroidFiles files = HexAndroidFiles.Current ?? throw new InvalidOperationException("Hex's files weren't set up.");

        // HORIZON_HEX_SELFTEST=code,workspace (am start --es) has the editor test itself in a folder of its own
        if (Environment.GetEnvironmentVariable("HORIZON_HEX_SELFTEST") is { } suites)
        {
            string[] named = suites.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(suite => !suite.Equals("all", StringComparison.OrdinalIgnoreCase)).ToArray();
            bool exit = string.Equals(Environment.GetEnvironmentVariable("HORIZON_HEX_EXIT"), "on", StringComparison.OrdinalIgnoreCase);

            // Settings, keys and sessions of the test's own, nobody's real ones read or written by it
            // (named the way the desktop's self test names its own, which its terminal checks look for)
            string home = Path.Combine(Path.GetDirectoryName(files.SelfTest)!, "horizon-hex-selftest-dusk-android");
            if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
            Directory.CreateDirectory(home);
            Environment.SetEnvironmentVariable("HEX_HOME", home);
            return DawnProgram.HostedSelfTest(engine, files.SelfTest, named, exit);
        }

        // HORIZON_HEX_OPEN names a file in the app's folders to start on, HORIZON_HEX_FOLDER a folder to open
        string? open = files.Inside(Environment.GetEnvironmentVariable("HORIZON_HEX_OPEN"));
        string? folder = files.Inside(Environment.GetEnvironmentVariable("HORIZON_HEX_FOLDER"));
        return DawnProgram.HostedScene(engine, files.Workspace, folder, open);
    }

    protected override void OnPause()
    {
        // Android may end a process at the back without asking, so what isn't saved goes into the backups first
        if (Session?.Engine is { } engine) DawnProgram.HostedLeaving(engine);
        base.OnPause();
    }

    protected override void OnActivityResult(int requestCode, [global::Android.Runtime.GeneratedEnum] Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        HexDocuments.Answered(this, requestCode, resultCode, data);
    }

    protected override void OnDestroy()
    {
        bool leaving = IsFinishing;
        HexDocuments.Detach(this);
        base.OnDestroy();
        if (leaving && Session?.Engine is null) DawnProgram.HostedStopped();
    }
}
