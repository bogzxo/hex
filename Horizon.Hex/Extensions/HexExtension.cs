using Horizon.HIDL;
using Horizon.HIDL.Runtime;

namespace Horizon.Hex;

/// <summary>
/// Something an extension can be told on its page of the settings, kept by Hex from one run to the next. Its kind is
/// text, path (a file or a folder, absolute or relative to whatever the extension says), number or flag.
/// </summary>
internal sealed record ExtensionSetting(string Key, string Title, string About, string Kind, IRuntimeValue Default);

/// <summary>
/// An extension, a folder with an extension.hor in it. The file says what the extension is (<c>let extension = { name,
/// description, version, author }</c>) and has functions Hex calls when things happen, <c>on_start(hex)</c> for now, in a
/// runtime of its own with <c>hex</c> the only thing it is handed. See the README.
/// </summary>
internal sealed class HexExtension
{
    public const string FILE = "extension.hor";

    /// <summary>The folder it is in, which is also what the settings know it by.</summary>
    public string Folder { get; }

    /// <summary>What the settings know it by, its folder's name.</summary>
    public string Id => Path.GetFileName(Folder);

    public string Name { get; private set; }
    public string Description { get; private set; } = string.Empty;
    public string Version { get; private set; } = string.Empty;
    public string Author { get; private set; } = string.Empty;

    /// <summary>A layout of its folder for its tab of the settings, null for a row for every one of its settings.</summary>
    public string? SettingsLayout { get; private set; }

    /// <summary>Whether it came with Hex rather than being put next to it by somebody.</summary>
    public bool BuiltIn { get; }

    /// <summary>Whether it is switched on in the settings.</summary>
    public bool Enabled { get; set; }

    /// <summary>What went wrong with it, null while nothing has. One that went wrong is left alone for the rest of the run.</summary>
    public string? Failed { get; private set; }

    /// <summary>The runtime it lives in, null until it is loaded.</summary>
    public HIDLRuntime? Runtime { get; private set; }

    public HexExtension(string folder, bool builtIn)
    {
        Folder = folder;
        Name = Path.GetFileName(folder);
        BuiltIn = builtIn;
    }

    /// <summary>
    /// Runs its file, with what Hex lets it do declared as <c>hex</c>, and reads what it says it is. False with
    /// <see cref="Failed"/> saying why if it didn't run.
    /// </summary>
    public bool Load(ObjectValue hex, Action<string> printed)
    {
        Commands.Clear();
        CommandKeys.Clear();
        Settings.Clear();
        Failed = null;
        Runtime = new HIDLRuntime { BaseDirectory = Folder, Output = printed };
        Runtime.UserScope.DeclareSystem("hex", hex);
        HexInterop.Declare(Runtime, Folder);

        if (!Call(() => Runtime.RunFile(Path.Combine(Folder, FILE))))
            return false;

        if (Runtime.UserScope.Variables.TryGetValue("extension", out IRuntimeValue? about) && about is ObjectValue said)
        {
            Name = Text(said, "name") ?? Name;
            Description = Text(said, "description") ?? string.Empty;
            Version = Text(said, "version") ?? string.Empty;
            Author = Text(said, "author") ?? string.Empty;
            SettingsLayout = Text(said, "settings_layout");

            // settings: { name: { title, about, kind, default } }, in the order they're written
            if (said.Properties.TryGetValue("settings", out IRuntimeValue? told) && told is ObjectValue settings)
            {
                foreach (var (key, value) in settings.Properties)
                {
                    if (value is not ObjectValue setting)
                        continue;

                    string kind = Text(setting, "kind") ?? "text";
                    IRuntimeValue fallback = setting.Properties.TryGetValue("default", out IRuntimeValue? given) ? given : kind switch
                    {
                        "number" => new NumberValue(0),
                        "flag" => Values.Bool(false),
                        _ => new StringValue(string.Empty)
                    };
                    Settings.Add(new ExtensionSetting(key, Text(setting, "title") ?? key, Text(setting, "about") ?? string.Empty, kind, fallback));
                }
            }
        }

        return true;
    }

    /// <summary>What it can be told on its page of the settings, out of the settings of its extension object.</summary>
    public List<ExtensionSetting> Settings { get; } = [];

    /// <summary>The commands it added to the command palette, by what they are called.</summary>
    public List<(string Name, IRuntimeValue Function)> Commands { get; } = [];

    /// <summary>The keys its commands said they're on, as they were written ("Ctrl+Shift+K").</summary>
    public List<(string Name, string Key)> CommandKeys { get; } = [];

    /// <summary>Calls one of its functions if it has one by that name. False if it went wrong, which switches it off.</summary>
    public bool Raise(string name, params IRuntimeValue[] arguments)
    {
        if (Runtime is not { } runtime || Failed is not null || !runtime.UserScope.Variables.TryGetValue(name, out IRuntimeValue? function) || !Values.IsFunction(function))
            return true;

        return Call(() => runtime.Invoke(function, arguments));
    }

    /// <summary>Calls a function it handed over (a command), false if it went wrong, which switches it off.</summary>
    public bool Invoke(IRuntimeValue function, params IRuntimeValue[] arguments) => Invoke(function, arguments, out _);

    /// <summary>Calls a function it handed over, with what it handed back, false if it went wrong, which switches it off.</summary>
    public bool Invoke(IRuntimeValue function, IRuntimeValue[] arguments, out IRuntimeValue returned)
    {
        returned = Values.Null;
        if (Runtime is not { } runtime || Failed is not null)
            return false;

        IRuntimeValue result = Values.Null;
        bool fine = Call(() => result = runtime.Invoke(function, arguments));
        returned = result;
        return fine;
    }

    /// <summary>
    /// Helper method to run something of the extension's, for a second at the most, on the editor's thread. Nothing it
    /// does gets past this, what went wrong is kept and the extension is done for.
    /// </summary>
    private bool Call(Action run)
    {
        using var cancel = new CancellationTokenSource(1000);
        Runtime!.Interpreter.Cancellation = cancel.Token;

        try
        {
            run();
            return true;
        }
        catch (Exception e)
        {
            Failed = cancel.IsCancellationRequested ? "it went on for more than a second and was stopped" : e.Message;
            return false;
        }
        finally
        {
            Runtime.Interpreter.Cancellation = CancellationToken.None;
        }
    }

    private static string? Text(ObjectValue of, string key) =>
        of.Properties.TryGetValue(key, out IRuntimeValue? value) && value is StringValue text ? text.Value : null;
}
