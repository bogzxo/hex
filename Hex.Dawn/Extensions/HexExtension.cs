using Horizon.HIDL;
using Horizon.HIDL.Runtime;

namespace Hex.Dawn;

/// <summary>
/// Something an extension can be told on its page of the settings, kept by Hex from one run to the next. Its kind is
/// text, path (a file or a folder, absolute or relative to whatever the extension says), number, flag or choice. A choice
/// is one of <paramref name="Choices"/>, each said in the dropdown as its label when it has one, with its line of help
/// under it. A number can have ends, <paramref name="Least"/> and <paramref name="Most"/> (none while most isn't past
/// least), the step it goes up and down in and a slider rather than a box.
/// </summary>
internal sealed record ExtensionSetting(string Key, string Title, string About, string Kind, IRuntimeValue Default,
    string[]? Choices = null, string[]? Labels = null, string[]? Help = null, float Least = 0.0f, float Most = 0.0f, float Step = 0.0f, bool Slider = false)
{
    /// <summary>Whether a number has ends it's kept between.</summary>
    public bool Ranged => Most > Least;

    /// <summary>A value of its kind made to fit, a number brought between its ends, null for one that can't, a choice that isn't one.</summary>
    public IRuntimeValue? Fitted(IRuntimeValue value) => (Kind, value) switch
    {
        ("number", NumberValue number) => Ranged ? new NumberValue(Math.Clamp(number.Value, Least, Most)) : number,
        ("number", _) => null,
        ("flag", BooleanValue) => value,
        ("flag", _) => null,
        ("choice", StringValue chosen) => Choices is { } choices && Array.IndexOf(choices, chosen.Value) >= 0 ? value : null,
        ("choice", _) => null,
        (_, StringValue) => value,
        _ => null
    };
}

/// <summary>
/// An extension, a folder with an extension.hor in it. The file says what the extension is (<c>let extension = { name,
/// description, version, author }</c>) and has functions Hex calls when things happen, <c>on_start(hex)</c> for now, in a
/// runtime of its own with <c>hex</c> the only thing it is handed. See docs/extensions.md.
/// </summary>
internal sealed class HexExtension : Horizon.UI.Scripting.IUIScriptOwner
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

    /// <summary>The C# side it said it has (native: "designer"), only taken from one that came with Dawn, null for none.</summary>
    public string? Native { get; private set; }

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
            Native = BuiltIn ? Text(said, "native") : null;

            // settings: { name: { title, about, kind, default } }, in the order they're written
            if (said.Properties.TryGetValue("settings", out IRuntimeValue? told) && told is ObjectValue settings)
            {
                foreach (var (key, value) in settings.Properties)
                {
                    if (value is ObjectValue setting)
                        Settings.Add(SettingOf(key, setting));
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
    public bool Raise(string name, params IRuntimeValue[] arguments) => Raise(name, arguments, out _);

    /// <summary>Calls one of its functions if it has one by that name, with what it handed back (null if it hasn't one).</summary>
    public bool Raise(string name, IRuntimeValue[] arguments, out IRuntimeValue returned)
    {
        returned = Values.Null;
        if (Runtime is not { } runtime || Failed is not null || !runtime.UserScope.Variables.TryGetValue(name, out IRuntimeValue? function) || !Values.IsFunction(function))
            return true;

        IRuntimeValue result = Values.Null;
        bool fine = Call(() => result = runtime.Invoke(function, arguments));
        returned = result;
        return fine;
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

    private static float? Number(ObjectValue of, string key) =>
        of.Properties.TryGetValue(key, out IRuntimeValue? value) && value is NumberValue number ? number.Value : null;

    // A list of texts, null when it isn't there, what isn't text in it said as it would be written
    private static string[]? Texts(ObjectValue of, string key) =>
        of.Properties.TryGetValue(key, out IRuntimeValue? value) && value is ListValue list ? [.. list.Items.Select(item => item is StringValue text ? text.Value : Values.Text(item))] : null;

    /// <summary>
    /// One setting out of the extension's settings object, <c>{ title, about, kind, default }</c>, a choice's choices,
    /// labels and help, a number's least, most, step and slider. A choice without choices is a line of text, and one
    /// whose default isn't one of them starts at the first.
    /// </summary>
    private static ExtensionSetting SettingOf(string key, ObjectValue setting)
    {
        string kind = Text(setting, "kind") ?? "text";
        string[]? choices = kind == "choice" ? Texts(setting, "choices") : null;
        if (kind == "choice" && choices is not { Length: > 0 })
            kind = "text";

        IRuntimeValue fallback = setting.Properties.TryGetValue("default", out IRuntimeValue? given) ? given : kind switch
        {
            "number" => new NumberValue(Number(setting, "least") ?? 0),
            "flag" => Values.Bool(false),
            "choice" => new StringValue(choices![0]),
            _ => new StringValue(string.Empty)
        };

        var made = new ExtensionSetting(key, Text(setting, "title") ?? key, Text(setting, "about") ?? string.Empty, kind, fallback,
            choices, kind == "choice" ? Texts(setting, "labels") : null, kind == "choice" ? Texts(setting, "help") : null,
            Number(setting, "least") ?? 0.0f, Number(setting, "most") ?? 0.0f, Number(setting, "step") ?? 0.0f,
            setting.Properties.TryGetValue("slider", out IRuntimeValue? slides) && slides is BooleanValue { Value: true });

        // A default that doesn't fit is what it would be made to, the first choice or a number brought between its ends
        return made.Fitted(fallback) is { } fits ? made with { Default = fits } : kind == "choice" ? made with { Default = new StringValue(choices![0]) } : made;
    }
}
