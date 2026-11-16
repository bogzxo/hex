using System.Runtime.CompilerServices;

using Hex.Core;
using Horizon.HIDL;
using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;
using Horizon.UI.Components;

namespace Hex.Tests;

/// <summary>
/// Every setting read out of a file and written back, held against what was recorded (Goldens/settings_*.txt). Three
/// files: one with every setting changed to something it takes, one with every number far past its top and every
/// choice something it isn't, one with every number far under its bottom and every flag a number. What comes back says
/// how each key is read, what it's held to and how it's written, so the reading and writing can be moved about (into
/// one table) and be known to do what they did. HEX_RECORD_GOLDENS=1 records them again, for a setting that's new.
/// </summary>
public class HexSettingsGoldenTests
{
    private static string Here([CallerFilePath] string at = "") => Path.GetDirectoryName(at)!;

    private static ObjectValue Object(params (string Key, IRuntimeValue Value)[] of)
    {
        var made = new HIDLObject();
        foreach (var (key, value) in of)
            made.Add(key, value);
        return made.ToValue();
    }

    private static IRuntimeValue Text(string text) => new StringValue(text);

    // What isn't a flag or a number is changed to one of these, a choice to another it takes
    private static readonly Dictionary<string, IRuntimeValue> changed = new(StringComparer.Ordinal)
    {
        ["csharp_colors"] = Text("visual_studio"),
        ["git_pull_mode"] = Text("rebase"),
        ["git_smart_commit"] = Text("always"),
        ["render_scale"] = Text("manual"),
        ["render_upscaler"] = Text("fsr1"),
        ["render_composition"] = Text("whole_frame"),
        ["effects"] = Text("subtle"),
        ["render_whitespace"] = Text("all"),
        ["cursor_style"] = Text("block"),
        ["rulers"] = Text("80, 120, abc, 80"),
        ["look"] = Text("Flat"),
        ["looks"] = Object(("Mine", Object(("effect_glass", Values.Bool(true)), ("glass_blur", new NumberValue(12.0f))))),
        ["exception_breakpoints"] = Object(("all", Values.Bool(true)), ("user", Values.Bool(false))),
        ["language_server_paths"] = Object(("csharp", Text(@"C:\tools\csharp-ls.exe"))),
        ["files_associations"] = Object(("*.foo", Text("csharp"))),
        ["files_exclude"] = Natives.List(["**/a", " **/b ", "**/a", ""]),
        ["search_exclude"] = Natives.List(["**/fixtures"]),
        ["language_servers_added"] = Object(("zig", Object(("name", Text("zls")), ("extensions", Natives.List([".zig"])), ("command", Text(@"C:\zls\zls.exe")), ("args", Natives.List(["--stdio"]))))),
        ["language_server_latest"] = Object(("csharp", Text("1.2.3"))),
        ["language_server_skipped"] = Object(("python", Text("0.9"))),
        ["disabled_extensions"] = Natives.List(["zeta", "alpha"]),
        ["catalog_extensions"] = Natives.List(["tools"]),
        ["debugger_path"] = Text(@"C:\dbg\netcoredbg.exe"),
        ["backdrop_image"] = Text(@"C:\pictures\a.png"),
        ["last_workspace"] = Text(@"C:\work\hex"),
        ["extension_catalog"] = Text(@"C:\catalogue"),
        ["git_account"] = Text("  somebody  ")
    };

    private static HIDLObject Input(string which)
    {
        var input = new HIDLObject();
        foreach (var (key, usual) in new HexSettings(null).EditorValues())
        {
            IRuntimeValue? value = (which, usual) switch
            {
                ("changed", _) when changed.TryGetValue(key, out IRuntimeValue? other) => other,
                ("changed", BooleanValue flag) => Values.Bool(!flag.Value),
                ("changed", NumberValue number) => new NumberValue(number.Value * 0.5f + 0.37f),
                ("changed", StringValue) => Text("hex/test"),
                ("high", NumberValue) => new NumberValue(1_000_000.0f),
                ("high", StringValue) => Text("not a choice"),
                ("low", NumberValue) => new NumberValue(-1_000_000.0f),
                ("low", BooleanValue) => new NumberValue(3.0f),
                ("low", StringValue) => Values.Bool(true),
                _ => null
            };

            if (value is not null)
                input.Add(key, value);
        }

        return input;
    }

    private static string Written(IRuntimeValue value)
    {
        try
        {
            return HIDLWriter.Write(value);
        }
        catch (NotSupportedException)
        {
            return value.GetType().Name;
        }
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("high")]
    [InlineData("low")]
    public void Every_setting_is_read_and_written_as_recorded(string which)
    {
        string folder = Path.Combine(Path.GetTempPath(), "hex-settings-golden-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string file = Path.Combine(folder, "settings.hor");
            File.WriteAllText(file, SettingsSections.Write(file, (SettingsSections.EDITOR, Input(which))));
            var settings = new HexSettings(file);
            string got = string.Join("\n", settings.EditorValues().Select(pair => $"{pair.Key} = {Written(pair.Value).ReplaceLineEndings(" ")}")) + "\n";

            string golden = Path.Combine(Here(), "Goldens", $"settings_{which}.txt");
            if (System.Environment.GetEnvironmentVariable("HEX_RECORD_GOLDENS") == "1")
                File.WriteAllText(golden, got);
            Assert.Equal(File.ReadAllText(golden).ReplaceLineEndings("\n"), got);

            // And what a save writes reads back the same, key for key
            Assert.Null(settings.Save());
            var again = new HexSettings(file);
            Assert.Equal(got, string.Join("\n", again.EditorValues().Select(pair => $"{pair.Key} = {Written(pair.Value).ReplaceLineEndings(" ")}")) + "\n");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
