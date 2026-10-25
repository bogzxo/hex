using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using System.Text;

using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

using Hex.Dusk;
using Hex.Shell;

using Horizon.Core.Processes;

namespace Hex.Tests;

/// <summary>
/// Dusk as the system's terminal. The registry it writes on Windows (under a key of the test's own, never the user's
/// HKCU\Console), the files it writes on Linux (in folders of the test's own), and a console passed on to Dusk the way
/// Windows Terminal's console host passes it, through ITerminalHandoff3, with real pipes and a real program, its output
/// read, its input written, its size sent down the signal pipe and its end heard.
/// </summary>
[Collection("default terminal")]
public sealed unsafe class DefaultTerminalTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), $"hex-default-terminal-{Guid.NewGuid():N}");
    private readonly string key = $@"Software\HexTests\DefaultTerminal\{Guid.NewGuid():N}";

    public DefaultTerminalTests()
    {
        Directory.CreateDirectory(folder);
        HexDefaultTerminal.Program = () => @"C:\Tools\Hex\Hex.exe";
        HexDefaultTerminal.Folders = () => (Path.Combine(folder, "config"), Path.Combine(folder, "data"));
        HexDefaultTerminal.Gnome = false;
        if (OperatingSystem.IsWindows())
        {
            HexDefaultTerminal.Root = () => Registry.CurrentUser.CreateSubKey(key);

            // The machine's keys stand in a key of the test's own too, and the admin prompt is a write there that's counted
            HexDefaultTerminal.MachineRoot = () => Registry.CurrentUser.CreateSubKey(key + @"\Machine");
            HexDefaultTerminal.UsersClassesWork = () => usersClassesWork;

            // What's run inside Windows Terminal's package, written down rather than run
            HexDefaultTerminal.InPackage = (family, commands) =>
            {
                inPackage.AddRange(commands.Select(command => $"{family}: {command}"));
                return null;
            };
            HexDefaultTerminal.RegisterForEveryone = program =>
            {
                prompts++;
                if (!adminSaysYes)
                    return "the admin prompt was turned down";
                using RegistryKey server = HexDefaultTerminal.MachineRoot().CreateSubKey(@"Software\Classes\CLSID\{80CF73D7-0B2D-411E-8996-3E4D4921CA1D}\LocalServer32");
                server.SetValue(null, $"\"{program}\" --dusk");
                return null;
            };
            HexDefaultTerminal.UnregisterForEveryone = () =>
            {
                prompts++;
                HexDefaultTerminal.MachineRoot().DeleteSubKeyTree(@"Software\Classes\CLSID\{80CF73D7-0B2D-411E-8996-3E4D4921CA1D}", throwOnMissingSubKey: false);
                return null;
            };
        }
    }

    // Whether this Windows starts a COM server of the user's, whether the admin prompt is said yes to, and how many came up
    private bool usersClassesWork = true, adminSaysYes = true;
    private int prompts;
    private readonly List<string> inPackage = [];

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
            HexDefaultTerminal.UseTheSystem();
        }
        HexDefaultTerminal.Program = () => Environment.ProcessPath;
        HexDefaultTerminal.Gnome = true;
        Directory.Delete(folder, recursive: true);
    }

    /* Windows */

    [SupportedOSPlatform("windows")]
    private static RegistryKey Under(string name) => HexDefaultTerminal.Root().CreateSubKey(name);

    [Fact]
    public void Without_windows_terminal_it_says_so_and_changes_nothing()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string? why = HexDefaultTerminal.Set(true);
        Assert.Contains("Windows Terminal", why);
        Assert.False(HexDefaultTerminal.Installed);
        Assert.Null(HexDefaultTerminal.Root().OpenSubKey(@"Console\%%Startup"));
    }

    [Fact]
    public void On_windows_it_registers_dusk_as_the_terminal_with_windows_terminals_console_host_and_puts_back_what_was()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // Windows Terminal Preview installed, and a default terminal of somebody else's set before
        Under(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages\Microsoft.WindowsTerminalPreview_1.23.0.0_x64__8wekyb3d8bbwe").Dispose();
        using (RegistryKey startup = Under(@"Console\%%Startup"))
        {
            startup.SetValue("DelegationConsole", "{11111111-1111-1111-1111-111111111111}");
            startup.SetValue("DelegationTerminal", "{22222222-2222-2222-2222-222222222222}");
        }

        Assert.Null(HexDefaultTerminal.Set(true));
        Assert.True(HexDefaultTerminal.Installed);
        using (RegistryKey startup = Under(@"Console\%%Startup"))
        {
            Assert.Equal("{06EC847C-C0A5-46B8-92CB-7C92F6E35CD5}", startup.GetValue("DelegationConsole"));
            Assert.Equal("{80CF73D7-0B2D-411E-8996-3E4D4921CA1D}", startup.GetValue("DelegationTerminal"));
        }
        using (RegistryKey server = Under(@"Software\Classes\CLSID\{80CF73D7-0B2D-411E-8996-3E4D4921CA1D}\LocalServer32"))
            Assert.Equal("\"C:\\Tools\\Hex\\Hex.exe\" --dusk", server.GetValue(null));

        // The same two values inside Windows Terminal Preview's package, where its OpenConsole reads them
        Assert.Equal(
        [
            "Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe: add \"HKCU\\Console\\%%Startup\" /v DelegationConsole /t REG_SZ /d {06EC847C-C0A5-46B8-92CB-7C92F6E35CD5} /f",
            "Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe: add \"HKCU\\Console\\%%Startup\" /v DelegationTerminal /t REG_SZ /d {80CF73D7-0B2D-411E-8996-3E4D4921CA1D} /f"
        ], inPackage);
        inPackage.Clear();

        // Asked twice it doesn't remember itself as what was there before
        Assert.Null(HexDefaultTerminal.Set(true));
        inPackage.Clear();

        Assert.Null(HexDefaultTerminal.Set(false));
        Assert.False(HexDefaultTerminal.Installed);
        using (RegistryKey startup = Under(@"Console\%%Startup"))
        {
            Assert.Equal("{11111111-1111-1111-1111-111111111111}", startup.GetValue("DelegationConsole"));
            Assert.Equal("{22222222-2222-2222-2222-222222222222}", startup.GetValue("DelegationTerminal"));
        }
        Assert.Null(HexDefaultTerminal.Root().OpenSubKey(@"Software\Classes\CLSID\{80CF73D7-0B2D-411E-8996-3E4D4921CA1D}"));

        // And taken back out of the package
        Assert.Equal(
        [
            "Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe: delete \"HKCU\\Console\\%%Startup\" /v DelegationConsole /f",
            "Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe: delete \"HKCU\\Console\\%%Startup\" /v DelegationTerminal /f"
        ], inPackage);
    }

    [Fact]
    public void When_windows_terminals_package_cant_be_reached_nothing_is_left_half_set()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Under(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages\Microsoft.WindowsTerminal_1.25.2733.0_x64__8wekyb3d8bbwe").Dispose();
        HexDefaultTerminal.InPackage = (_, commands) => commands[0].StartsWith("add", StringComparison.Ordinal) ? "no PowerShell here" : null;

        string? why = HexDefaultTerminal.Set(true);
        Assert.Contains("no PowerShell here", why);
        Assert.False(HexDefaultTerminal.Installed);
        Assert.Null(HexDefaultTerminal.Root().OpenSubKey(@"Console\%%Startup")?.GetValue("DelegationTerminal"));
        Assert.Null(HexDefaultTerminal.Root().OpenSubKey(@"Software\Classes\CLSID\{80CF73D7-0B2D-411E-8996-3E4D4921CA1D}"));
    }

    [Fact]
    public void With_nothing_set_before_switching_it_off_leaves_windows_to_decide()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Under(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages\Microsoft.WindowsTerminal_1.22.0.0_x64__8wekyb3d8bbwe").Dispose();
        Assert.Null(HexDefaultTerminal.Set(true));
        using (RegistryKey startup = Under(@"Console\%%Startup"))
            Assert.Equal("{2EACA947-7F5F-4CFA-BA87-8F7FBEEFBE69}", startup.GetValue("DelegationConsole"));

        Assert.Null(HexDefaultTerminal.Set(false));
        using RegistryKey after = Under(@"Console\%%Startup");
        Assert.Null(after.GetValue("DelegationConsole"));
        Assert.Null(after.GetValue("DelegationTerminal"));
    }

    [Fact]
    public void Where_windows_wont_start_a_users_com_server_dusk_is_registered_for_everyone_behind_one_prompt_and_taken_off_again()
    {
        if (!OperatingSystem.IsWindows())
            return;

        usersClassesWork = false;
        Under(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages\Microsoft.WindowsTerminal_1.25.2733.0_x64__8wekyb3d8bbwe").Dispose();

        Assert.Null(HexDefaultTerminal.Set(true));
        Assert.Equal(1, prompts);
        Assert.True(HexDefaultTerminal.Installed);
        using (RegistryKey? everyone = HexDefaultTerminal.MachineRoot().OpenSubKey(@"Software\Classes\CLSID\{80CF73D7-0B2D-411E-8996-3E4D4921CA1D}\LocalServer32"))
            Assert.Equal("\"C:\\Tools\\Hex\\Hex.exe\" --dusk", everyone?.GetValue(null));

        Assert.Null(HexDefaultTerminal.Set(false));
        Assert.Equal(2, prompts);
        Assert.False(HexDefaultTerminal.Installed);
        Assert.Null(HexDefaultTerminal.MachineRoot().OpenSubKey(@"Software\Classes\CLSID\{80CF73D7-0B2D-411E-8996-3E4D4921CA1D}"));
    }

    [Fact]
    public void An_admin_prompt_turned_down_leaves_the_default_terminal_as_it_was()
    {
        if (!OperatingSystem.IsWindows())
            return;

        usersClassesWork = false;
        adminSaysYes = false;
        Under(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages\Microsoft.WindowsTerminal_1.25.2733.0_x64__8wekyb3d8bbwe").Dispose();

        string? why = HexDefaultTerminal.Set(true);
        Assert.Contains("turned down", why);
        Assert.False(HexDefaultTerminal.Installed);
        Assert.Null(HexDefaultTerminal.Root().OpenSubKey(@"Software\Classes\CLSID\{80CF73D7-0B2D-411E-8996-3E4D4921CA1D}"));
        Assert.Null(HexDefaultTerminal.Root().OpenSubKey(@"Console\%%Startup")?.GetValue("DelegationTerminal"));
    }

    [Fact]
    public void Finding_out_whether_windows_starts_a_users_com_server_starts_nothing_and_leaves_nothing_behind()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var timer = Stopwatch.StartNew();
        _ = HexDefaultTerminal.ProbeUsersClasses();
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Null(Registry.CurrentUser.OpenSubKey(@"Software\Classes\CLSID\{4D7A2E19-63B0-4C8F-A1D5-9E2B7C0F3A68}"));
    }

    /* Linux, the files, which are the same on any system */

    [Fact]
    public void On_linux_dusk_goes_first_in_xdg_terminals_list_and_comes_back_out_leaving_the_rest()
    {
        string config = Path.Combine(folder, "config"), data = Path.Combine(folder, "data");
        Directory.CreateDirectory(config);
        File.WriteAllLines(Path.Combine(config, "xdg-terminals.list"), ["# mine", "kitty.desktop", "hex-dusk.desktop"]);

        Assert.Null(HexDefaultTerminal.InstallOnLinux("/opt/hex/Hex"));
        Assert.True(HexDefaultTerminal.InstalledOnLinux());
        Assert.Equal(["hex-dusk.desktop", "kitty.desktop"], File.ReadAllLines(Path.Combine(config, "xdg-terminals.list")));
        Assert.Contains("exec \"/opt/hex/Hex\" --dusk \"$@\"", File.ReadAllText(Path.Combine(data, "hex", "dusk")));
        string desktop = File.ReadAllText(Path.Combine(data, "applications", "hex-dusk.desktop"));
        Assert.Contains("Exec=\"/opt/hex/Hex\" --dusk", desktop);
        Assert.Contains("X-TerminalArgExec=-e", desktop);

        Assert.Null(HexDefaultTerminal.UninstallOnLinux());
        Assert.False(HexDefaultTerminal.InstalledOnLinux());
        Assert.Equal(["kitty.desktop"], File.ReadAllLines(Path.Combine(config, "xdg-terminals.list")));
        Assert.False(File.Exists(Path.Combine(data, "hex", "dusk")));
    }

    /* The console passed on */

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public nint Descriptor;
        public int Inherit;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out nint read, out nint write, nint attributes, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(nint file, byte[] buffer, uint count, out uint read, nint overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(nint file, byte[] buffer, uint count, out uint written, nint overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    private static byte[] Read(nint pipe, int count)
    {
        var all = new List<byte>();
        var buffer = new byte[count];
        while (all.Count < count && ReadFile(pipe, buffer, (uint)(count - all.Count), out uint read, 0) && read > 0)
            all.AddRange(buffer.AsSpan(0, (int)read).ToArray());
        return [.. all];
    }

    /// <summary>
    /// What Windows Terminal's console host does, the signal pipe, a reference on the console, its own process and the
    /// program's, Dusk's class made through its class factory and asked for ITerminalHandoff3, and EstablishPtyHandoff
    /// called through the interface's table of functions, the way COM's proxy calls it in Dusk's process.
    /// </summary>
    [Fact]
    public void A_console_passed_on_through_iterminalhandoff3_becomes_a_terminal_dusk_can_read_write_resize_and_see_end()
    {
        if (!OperatingSystem.IsWindows())
            return;
        while (DuskHandoff.TryTake(out PseudoTerminal stale, out _))
            stale.Dispose();

        Assert.True(CreatePipe(out nint signalRead, out nint signalWrite, 0, 0));
        string referenceFile = Path.Combine(folder, "reference");
        using var reference = File.Create(referenceFile);
        using var program = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul & exit 7") { CreateNoWindow = true, UseShellExecute = false })!;

        // The class factory, the way COM gets it from CoRegisterClassObject, and ITerminalHandoff3 out of it
        var wrappers = new StrategyBasedComWrappers();
        nint factory = wrappers.GetOrCreateComInterfaceForObject(new DuskHandoff.HandoffFactory(), CreateComInterfaceFlags.None);
        Assert.Equal(0, Marshal.QueryInterface(factory, new Guid("00000001-0000-0000-C000-000000000046"), out nint classFactory));
        Guid iid = new("6F23DA90-15C5-4203-9DB0-64E73F1B1B00");
        nint handoff;
        var createInstance = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)(*(nint**)classFactory)[3];
        Assert.Equal(0, createInstance(classFactory, 0, &iid, &handoff));

        var title = Marshal.StringToBSTR("Ping");
        var startup = new DuskHandoff.TerminalStartupInfo { Title = title, IconPath = 0, ShowWindow = 1 };
        nint input, output;
        var establish = (delegate* unmanaged[Stdcall]<nint, nint*, nint*, nint, nint, nint, nint, DuskHandoff.TerminalStartupInfo*, int>)(*(nint**)handoff)[3];
        int said = establish(handoff, &input, &output, signalWrite, reference.SafeFileHandle.DangerousGetHandle(), GetCurrentProcess(), program.Handle, &startup);
        Marshal.FreeBSTR(title);
        Assert.Equal(0, said);

        // What COM would close once the call is over, Dusk kept copies
        CloseHandle(signalWrite);
        Marshal.Release(handoff);
        Marshal.Release(classFactory);
        Marshal.Release(factory);

        Assert.True(DuskHandoff.TryTake(out PseudoTerminal console, out string named));
        Assert.Equal("Ping", named);
        var heard = new StringBuilder();
        int? ended = null;
        console.Output += bytes => { lock (heard) heard.Append(Encoding.UTF8.GetString(bytes)); };
        console.Exited += code => ended = code;

        // The console host writes what the program put on its screen, Dusk reads it
        byte[] screen = "\u001b[1mhello from the console\u001b[0m"u8.ToArray();
        Assert.True(WriteFile(output, screen, (uint)screen.Length, out _, 0));
        // Dusk types, the console host reads it
        console.Write("dir\r");
        Assert.Equal("dir\r", Encoding.UTF8.GetString(Read(input, 4)));
        // The terminal's size goes down the signal pipe, three shorts, the signal and the size
        console.Resize(120, 40);
        byte[] packet = Read(signalRead, 6);
        Assert.Equal([8, 0, 120, 0, 40, 0], packet);

        Assert.True(SpinWait.SpinUntil(() => { lock (heard) return heard.ToString().Contains("hello from the console"); }, 5000));
        Assert.True(SpinWait.SpinUntil(() => ended is not null, 15000), "the program ending should end the terminal");
        Assert.Equal(7, ended);

        console.Dispose();
        CloseHandle(input);
        CloseHandle(output);
        CloseHandle(signalRead);
    }

    [Fact]
    public void A_handoff_without_somewhere_to_put_the_pipes_is_refused()
    {
        if (!OperatingSystem.IsWindows())
            return;
        Assert.Equal(unchecked((int)0x80004003), DuskHandoff.Establish(null, null, 0, 0, 0, 0, null));
    }
}
