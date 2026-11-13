using System.Globalization;

using Horizon.HIDL;
using Horizon.HIDL.Runtime;
using Horizon.Logging;
using Horizon.UI.Drawing;

namespace Hex.Core;

/// <summary>
/// What the editor is set to and keeps from one run to the next, the look of itself, how its panels were dragged
/// and how it helps with lining things up. Kept as the editor section of settings.hor in the settings folder next to the
/// executable, a file anybody can read (hex_settings.hor next to the exe is the old file of its own, read when that has none).
/// The settings dialog (File, Settings) is where they are changed, the panels save their own sizes as they are let
/// go of. A file that is missing or makes no sense is everything at what it comes as. Only what isn't what it comes as
/// is written, so the file says what somebody changed. The folder that's open can have settings of its own over these,
/// see HexSettings.Workspace.
/// </summary>
internal sealed partial class HexSettings
{
    public const string FILE = "hex_settings.hor";

    /// <summary>The grids there are to snap to, 0 being none.</summary>
    public static readonly float[] Grids = [0.0f, 2.0f, 4.0f, 8.0f, 16.0f];

    /// <summary>Which skin the editor itself is drawn in (the flat one unless somebody fancies otherwise) and in which of its themes.</summary>
    public string EditorSkin = USUAL_SKIN, EditorTheme = USUAL_THEME;

    /// <summary>
    /// What Hex comes in: the flat skin in Solstice for the editor and Solstice's own theme of Dusk for the terminal,
    /// which is the Solstice look (HexLooks). The one place they're said, the settings window's reset and whether a row
    /// reads as changed go by these too.
    /// </summary>
    public const string USUAL_SKIN = "flat", USUAL_THEME = "solstice", USUAL_TERMINAL_THEME = "solstice_dusk", USUAL_LOOK = "Solstice";

    /// <summary>How wide the lists on the left and the properties on the right were dragged, 0 for as their layout has them.</summary>
    public float LeftWidth, RightWidth;

    /// <summary>How tall the list of layers and the panel under the editor were dragged, 0 for what the editor works out by itself.</summary>
    public float LayersHeight, PanelHeight;

    /// <summary>Whether the side bar, the panel under the editor and the properties were put away.</summary>
    public bool SideBarHidden, PanelHidden, PropertiesHidden;

    /// <summary>Whether the panel under the editor was folded down to its tabs by its chevron.</summary>
    public bool PanelCollapsed = true;

    /// <summary>What positions and sizes land on a multiple of when something is dragged in the canvas, 0 for anywhere.</summary>
    public float Snap;

    /// <summary>How big the code is drawn against the rest of the editor, 1 for the size it comes at.</summary>
    public float CodeZoom = 1.0f;

    /// <summary>Shared mouse-wheel scrolling distance throughout Dawn and Dusk, relative to the default.</summary>
    public float ScrollSpeed = 1.0f;

    /// <summary>Middle-mouse scrolling speed throughout Dawn and Dusk, relative to the default.</summary>
    public float MiddleScrollSpeed = 1.0f;

    /// <summary>Whether what is selected in the canvas can be dragged bigger by its edges.</summary>
    public bool CanvasHandles = true;

    /// <summary>Whether the editor starts with the layout that was worked on last.</summary>
    public bool ReopenLast;

    /// <summary>
    /// The picture behind the code (see DawnScene.Backdrop), whether it's there, which (empty for the girl that comes with
    /// Hex), how big against its pixels, how far from the bottom right corner it is, along and up, and how much of it shows.
    /// </summary>
    public bool BackdropOn;

    /// <summary>Whether wrapped code runs round what is drawn in the picture instead of over it (see CodeEditor.BackdropShape).</summary>
    public bool BackdropWrap = true;

    /// <summary>Whether the picture is behind Dusk's terminals as well, see Dusk's Backdrop.</summary>
    public bool BackdropTerminal;

    /// <summary>Where the picture is behind Dusk's terminals and how big, its own place and size and not the code's.</summary>
    public float TerminalBackdropScale = 0.5f, TerminalBackdropAcross, TerminalBackdropUp;

    /// <summary>How wide the picture is behind the code and behind the terminals against its own shape, 1 for the shape it has.</summary>
    public float BackdropStretch = 1.0f, TerminalBackdropStretch = 1.0f;

    /// <summary>How narrow and how wide the picture can be stretched against its own shape.</summary>
    public const float LEAST_STRETCH = 0.25f, MOST_STRETCH = 4.0f;

    /// <summary>How far the wrapped code keeps off what is drawn in the picture, in the editor's pixels, round every edge.</summary>
    public float BackdropWrapMargin = 8.0f;

    /// <summary>How quickly a word the wrap moves glides to its new line (CodeEditor.FlowRate), 0 to snap.</summary>
    public float WrapFlow = Horizon.UI.Components.CodeEditor.FLOW_RATE;

    /// <summary>
    /// The effects, none, subtle (the lights, and a burst of sparks on a save when the sparks are on) or all (sparks as it
    /// types too), see DawnScene.Effects. The glass and the caret's glide have switches of their own and don't wait on it.
    /// </summary>
    public string Effects = "none";

    /// <summary>
    /// How many pixels Hex is drawn with, the engine's render scale (WindowManager.Scaling). auto lets the screen decide
    /// (a dense tablet draws fewer), native is a pixel for every pixel, manual is <see cref="RenderScalePercent"/> of
    /// them. The UI stays at the window's own pixels unless <see cref="RenderComposition"/> says whole_frame, and as Hex
    /// is nearly all UI that's the one that saves the GPU anything, at the price of softer text.
    /// </summary>
    public string RenderScale = "auto", RenderUpscaler = "cas", RenderComposition = "native_ui";

    /// <summary>The share of each side drawn with a manual render scale, 25 to 100, and how hard CAS sharpens, 0 to 1.</summary>
    public float RenderScalePercent = 75.0f, RenderSharpness = 0.0f;

    /// <summary>
    /// Whether the effects that are on throw sparks, whether the caret glides, and whether the dialogs, menus, palette and
    /// side panels sit over a blur of what is behind them. New settings start in the Flat look; saved choices are read below.
    /// </summary>
    public bool EffectSparks, EffectMotion, EffectGlass;

    /// <summary>The effects of light, a glow under the caret, a spotlight under the pointer, rings off clicks, embers behind the pointer.</summary>
    public bool EffectCaretGlow, EffectSpotlight, EffectRipples, EffectTrail;

    /// <summary>The look picked last in the settings by its name (one of Hex's three or one of the user's), empty for none.</summary>
    public string Look = USUAL_LOOK;

    /// <summary>
    /// The looks the user saved, in the order they were made, each the visual settings it sets by their keys in this section
    /// (and terminal_theme for Dusk's). Hex.Shell's HexLooks is what reads and applies them.
    /// </summary>
    public readonly List<(string Name, Dictionary<string, IRuntimeValue> Values)> Looks = [];

    /// <summary>How thick the glass is, the popups' as they are and the sides' worked out of them (the engine's UIGlass.Clear to begin with, Hex.Shell's HexGlass draws it).</summary>
    public float GlassBlur = UIGlass.Clear.Blur, GlassClarity = UIGlass.Clear.Clarity, GlassRim = UIGlass.Clear.Rim, GlassBend = UIGlass.Clear.Bend;

    /// <summary>How quickly the UI moves (UICompositor.AnimationSpeed), 0 for no motion, and how long the caret glides for.</summary>
    public float UiAnimation = 1.0f, CaretGlide;

    /// <summary>How many columns wide a tab is and a level of indentation, 1 to 16.</summary>
    public int TabSize = 4;

    /// <summary>Whether Tab and indenting put in spaces rather than a tab.</summary>
    public bool InsertSpaces = true;

    /// <summary>Whether a file that is opened is indented the way it already is, tabs or so many spaces, rather than as set here.</summary>
    public bool DetectIndentation = true;

    /// <summary>Whether the code has its lines numbered down the left and guides down its indentation.</summary>
    public bool LineNumbers = true, IndentGuides = true;

    /// <summary>Whether a project's .editorconfig files have the last word on how its files are indented.</summary>
    public bool UseEditorConfig = true;

    /// <summary>The columns a faint line is drawn down the code at, VS Code's rulers, none to begin with.</summary>
    public int[] Rulers = [];

    /// <summary>Where the spaces and tabs are drawn, none, selection, boundary (leading and trailing) or all.</summary>
    public string RenderWhitespace = "selection";

    /// <summary>How the caret is drawn, line, block or underline.</summary>
    public string CursorStyle = "line";

    /// <summary>How long an arrow key is held before the caret goes quicker, in seconds, 0 for never, and how many times a second it moves then.</summary>
    public float CaretFastAfter = 1.0f, CaretFastRate = 80.0f;

    /// <summary>Whether the lines the top of the code is inside of stay held at the top as it scrolls.</summary>
    public bool StickyScroll = true;

    /// <summary>Whether Ctrl+S has the file's language server format it before it's written, when the server can.</summary>
    public bool FormatOnSave;

    /// <summary>Whether native language servers format statement endings and new lines as they are typed.</summary>
    public bool FormatOnType = true;

    /// <summary>Whether a file of code loses the spaces at the ends of its lines and gets a newline at its end when saved.</summary>
    public bool TrimWhitespace = true, FinalNewline;

    /// <summary>Whether a file of code saves itself once typing in it stops for this many seconds.</summary>
    public bool AutoSave;
    public float AutoSaveDelay = 1.0f;

    /// <summary>Whether long lines of code are wrapped at the edge of the editor (Alt+Z).</summary>
    public bool WrapCode;

    /// <summary>Whether the line the caret is on says faintly at its end who last changed it and when, git blame.</summary>
    public bool GitBlame = true;

    /// <summary>Whether the code is drawn small down the right of the editor, a slider over what's in sight.</summary>
    public bool Minimap = true;

    /// <summary>Whether the names of parameters and the like are written faintly in among the code, inlay hints.</summary>
    public bool InlayHints = true;

    /// <summary>Whether a declaration has how many places use it written small over it, Visual Studio's code lens.</summary>
    public bool CodeLensReferences = true;

    /// <summary>Whether names are coloured by what they are (a class, a field, a parameter), from the language servers and from HIDL itself.</summary>
    public bool SemanticHighlighting = true;

    /// <summary>Whether the keys are Visual Studio's where they differ from VS Code's (Ctrl+- back, Ctrl+K Ctrl+D formats), the Visual Studio look turns it on.</summary>
    public bool VisualStudioKeys;

    /// <summary>
    /// Whether Visual Studio's keys were ever turned on or off, or kept as they were when a folder with a solution
    /// offered them. Until then such a folder offers them once.
    /// </summary>
    public bool VisualStudioKeysChosen;

    /// <summary>The colours C# is shown in, resharper, visual_studio, or skin for whatever the skin has.</summary>
    public string CSharpColors = "resharper";
    /// <summary>
    /// Whether agents can drive Hex over MCP, its server up on a pipe only this user on this machine can open. Off as it
    /// comes, and never a folder's to switch on (see WorkspaceKeys), a repository somebody cloned mustn't open the door.
    /// </summary>
    public bool McpServer;

    /// <summary>Whether the workspace's repositories are fetched from their remotes every few minutes, for behind to be known.</summary>
    public bool GitAutoFetch = true;

    /// <summary>How many minutes go between those fetches.</summary>
    public float GitAutoFetchInterval = 3.0f;

    /// <summary>
    /// How a pull brings the branch up to date, default for however git config has it, merge, rebase, or ff_only for only
    /// when it can just move along.
    /// </summary>
    public string GitPullMode = "default";

    /// <summary>
    /// The GitHub account git signs in as, for a machine with more than one where the credential helper would otherwise ask
    /// which every time it is used. Empty leaves it to git.
    /// </summary>
    public string GitAccount = string.Empty;

    /// <summary>Whether a sync asks before it pulls and pushes, VS Code's git.confirmSync.</summary>
    public bool GitConfirmSync = true;

    /// <summary>What a commit with nothing staged does, ask whether to stage everything first, always just do it, or never.</summary>
    public string GitSmartCommit = "ask";

    /// <summary>How long a commit's summary is best kept to, and past how many characters a line is too long to show whole.</summary>
    public int GitSubjectLength = 50, GitLineLength = 72;

    /// <summary>Whether F5 says what a file printed and declared in the console of the terminal page rather than the output.</summary>
    public bool RunToTerminal;

    /// <summary>Where netcoredbg is, empty to look for it (DawnScene.Debugging).</summary>
    public string DebuggerPath = string.Empty;
    public bool DebugJustMyCode = true;
    public readonly Dictionary<string, bool> ExceptionBreakpoints = new(StringComparer.Ordinal);

    /// <summary>Where each language server is, by its name in language_servers.hor, for one that isn't the file's command on the PATH.</summary>
    public readonly Dictionary<string, string> ServerPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Which language the files of a glob are, VS Code's files.associations, "*.weird" to python. The most specific
    /// glob wins (CodeLanguages.SetAssociations), plaintext for none.
    /// </summary>
    public readonly Dictionary<string, string> FileAssociations = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What files.exclude comes as in VS Code, the folders of other version controls and the litter of file browsers,
    /// gone from the explorer and from everything else.
    /// </summary>
    public static readonly string[] UsualFilesExclude = ["**/.git", "**/.svn", "**/.hg", "**/CVS", "**/.DS_Store", "**/Thumbs.db", "**/.vs"];

    /// <summary>
    /// What search.exclude comes as, what gets built and what package managers fetch, still in the explorer but not in
    /// quick open, Find in Files or the watcher. Old builds kept on purpose too.
    /// </summary>
    public static readonly string[] UsualSearchExclude = ["**/node_modules", "**/bin", "**/obj", "**/venv", "**/.venv", "**/__pycache__", "**/target",
        "**/dist", "**/.terraform", "**/.gradle", "**/.tox", "**/.pytest_cache", "**/Build History"];

    /// <summary>
    /// The globs of files.exclude and search.exclude (see HexExcludes). Each change is a new array and never one changed
    /// in place, a walk off the editor's thread goes by the one it started with.
    /// </summary>
    public string[] FilesExclude = UsualFilesExclude, SearchExclude = UsualSearchExclude;

    /// <summary>Whether the walk of the files leaves out what each .gitignore says as well, search.useIgnoreFiles.</summary>
    public bool SearchUseIgnoreFiles = true;

    /// <summary>
    /// The language servers added in the settings, one a language, in the order they were added. One for a language
    /// language_servers.hor has a server for as well is used instead of that one (HexLanguages).
    /// </summary>
    public readonly List<HexAddedServer> AddedServers = [];

    /// <summary>Whether Hex looks for new versions of the language servers on GitHub, once a day as it starts.</summary>
    public bool ServerUpdates = true;

    /// <summary>When it last looked (UTC, round trip), empty for never.</summary>
    public string ServersChecked = string.Empty;

    /// <summary>The newest version of each server it found, and the versions that were said not to be offered again.</summary>
    public readonly Dictionary<string, string> ServerLatest = new(StringComparer.OrdinalIgnoreCase), ServerSkipped = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The font the code is written in, a folder of Assets/fonts (hack), empty for the skin's (see DawnScene.CodeFont).</summary>
    public string CodeFont = "hack";
    public string BackdropImage = string.Empty;
    public float BackdropScale = 0.5f, BackdropAcross, BackdropUp, BackdropOpacity = 0.22f;

    /// <summary>The workspace file that was open last, empty for one that was never saved (kept in the settings folder instead).</summary>
    public string LastWorkspace = string.Empty;

    // The extensions that are switched off, by the name of their folder
    public readonly HashSet<string> DisabledExtensions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Where the extensions that can be installed come from, a GitHub repository (owner/name, its main branch) or a folder
    /// on this machine laid out the same, a folder an extension.
    /// </summary>
    public string ExtensionCatalog = DEFAULT_CATALOG;

    /// <summary>The catalog Hex comes set to, the extensions of its own repository on GitHub.</summary>
    public const string DEFAULT_CATALOG = "bogzxo/hex-extensions";

    /// <summary>Whether Hex looks at the catalog for new versions of the extensions installed out of it, once a day as it starts.</summary>
    public bool ExtensionUpdates = true;

    /// <summary>When it last looked (UTC, round trip), empty for never.</summary>
    public string ExtensionsChecked = string.Empty;

    // The extensions that were installed out of the catalog, by the name of their folder, the ones offered updates
    public readonly HashSet<string> CatalogExtensions = new(StringComparer.OrdinalIgnoreCase);

    // What every extension was told on its page of the settings, by the name of its folder and then of the setting.
    // Kept for one that isn't there any more as well, it might come back
    public readonly Dictionary<string, Dictionary<string, IRuntimeValue>> ExtensionSettings = new(StringComparer.OrdinalIgnoreCase);

    private readonly string? path;

    /// <param name="path">Where it is kept, null for settings that are never read or written (the editor testing itself).</param>
    /// <param name="old">The file the editor's settings were in before they were a section of Hex's one, read when the section isn't there yet.</param>
    public HexSettings(string? path, string? old = null)
    {
        this.path = path;
        if (path is null)
            return;

        try
        {
            // The editor's section of Hex's settings file, the extensions' beside it, or the old file of its own
            Dictionary<string, IRuntimeValue> values;
            Dictionary<string, IRuntimeValue>? byExtension = null;
            var all = Horizon.UI.Components.SettingsSections.Read(path);
            if (all is not null && all.ContainsKey(Horizon.UI.Components.SettingsSections.EDITOR))
            {
                values = Horizon.UI.Components.SettingsSections.Section(all, Horizon.UI.Components.SettingsSections.EDITOR);
                byExtension = Horizon.UI.Components.SettingsSections.Section(all, Horizon.UI.Components.SettingsSections.EXTENSIONS);
            }
            else if (old is not null && File.Exists(old))
            {
                values = Horizon.UI.Components.SettingsSections.ReadText(File.ReadAllText(old));
            }
            else
            {
                return;
            }

            userValues = new Dictionary<string, IRuntimeValue>(values, StringComparer.Ordinal);
            Apply(values);

            if (byExtension is null && values.TryGetValue("extension_settings", out var told) && told is ObjectValue inOld)
                byExtension = inOld.Properties;
            if (byExtension is not null)
            {
                foreach (var (id, its) in byExtension)
                {
                    if (its is ObjectValue settingsOf)
                        ExtensionSettings[id] = new Dictionary<string, IRuntimeValue>(settingsOf.Properties, StringComparer.OrdinalIgnoreCase);
                }
            }
        }
        catch (Exception e)
        {
            Log.Warning($"[Hex] {path} makes no sense and is left alone, the editor comes up as it is made: {e.Message}");
        }
    }

    /// <summary>
    /// The editor's settings put in force from a section of them, the keys it has and nothing else. Once for the user's
    /// file, then again for the folder's own over it.
    /// </summary>
    private void Apply(Dictionary<string, IRuntimeValue> values)
    {
        foreach (HexSettingRow row in Rows)
            row.Read(this, values);
    }

    internal static float ScrollSpeedOf(float value) => float.IsFinite(value) ? Math.Clamp(value, 0.25f, 5) : 1;

    private static float Number(Dictionary<string, IRuntimeValue> values, string key, float otherwise) =>
        values.TryGetValue(key, out var value) && value is NumberValue number ? number.Value : otherwise;

    private static bool Flag(Dictionary<string, IRuntimeValue> values, string key, bool otherwise) =>
        values.TryGetValue(key, out var value) && value is BooleanValue flag ? flag.Value : otherwise;

    /// <summary>The globs of a list as they're kept, trimmed, what isn't a text or is empty left out and each once.</summary>
    public static string[] Patterns(ListValue list) =>
        [.. list.Items.OfType<StringValue>().Select(item => item.Value.Trim()).Where(pattern => pattern.Length > 0).Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// The columns of the rulers out of what was typed, "80, 120" being 80 and 120. Anything that isn't a whole number
    /// from 1 to 500 is dropped, so "abc, 90" is just 90, and the same column twice is once.
    /// </summary>
    public static int[] ParseRulers(string said)
    {
        var columns = new List<int>();
        foreach (string part in said.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int column) && column is >= 1 and <= 500 && !columns.Contains(column))
                columns.Add(column);
        }

        return [.. columns];
    }

    /// <summary>The rulers written the way the row shows them and the file keeps them, "80, 120".</summary>
    public static string RulersText(int[] columns) => string.Join(", ", columns);

    // Helper method to read an object of texts by name, what isn't text left out
    private static void Names(Dictionary<string, IRuntimeValue> values, string key, Dictionary<string, string> into)
    {
        if (!values.TryGetValue(key, out var value) || value is not ObjectValue named)
            return;

        foreach (var (name, said) in named.Properties)
        {
            if (said is StringValue { Value.Length: > 0 } text)
                into[name] = text.Value;
        }
    }

    // Helper method to read the language servers added in the settings, an object of them by their language
    private void ReadAddedServers(Dictionary<string, IRuntimeValue> values)
    {
        if (!values.TryGetValue("language_servers_added", out var value) || value is not ObjectValue added)
            return;

        static string[] Texts(ObjectValue of, string key) =>
            of.Properties.GetValueOrDefault(key) is ListValue list ? [.. list.Items.OfType<StringValue>().Select(item => item.Value)] : [];

        foreach (var (language, its) in added.Properties)
        {
            if (its is not ObjectValue server || server.Properties.GetValueOrDefault("command") is not StringValue { Value.Length: > 0 } command)
                continue;

            string name = server.Properties.GetValueOrDefault("name") is StringValue { Value.Length: > 0 } named ? named.Value : language;
            AddedServers.Add(new HexAddedServer(language, name, Texts(server, "extensions"), command.Value, Texts(server, "args")));
        }
    }

    // Helper method to write the added language servers, each its name, files, program and what it's started with
    private HIDLObject AddedServersObject()
    {
        static ListValue List(IEnumerable<string> texts) => new([.. texts.Select(text => (IRuntimeValue)new StringValue(text))]);

        var added = new HIDLObject();
        foreach (HexAddedServer server in AddedServers)
        {
            added.Add(server.Language, new HIDLObject()
                .Add("name", server.Name)
                .Add("extensions", List(server.Extensions))
                .Add("command", server.Command.Replace('\\', '/'))
                .Add("args", List(server.Arguments)));
        }

        return added;
    }

    // Helper method to write texts by name, in the order of their names
    private static HIDLObject Named(Dictionary<string, string> values, bool paths = false)
    {
        var named = new HIDLObject();
        foreach (var (name, value) in values.Where(pair => pair.Value.Length > 0).OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            named.Add(name, paths ? value.Replace('\\', '/') : value);
        return named;
    }

    private HIDLObject ExceptionBreakpointsObject()
    {
        var filters = new HIDLObject();
        foreach (var (name, enabled) in ExceptionBreakpoints.OrderBy(pair => pair.Key, StringComparer.Ordinal)) filters.Add(name, enabled);
        return filters;
    }

    /// <summary>Writes the settings to their file. Says what went wrong if they couldn't be, null if they could.</summary>
    public string? Save()
    {
        if (path is null)
            return null;

        var extensions = new HIDLObject();
        foreach (string id in ExtensionSettings.Where(told => told.Value.Count > 0).Select(told => told.Key).Order(StringComparer.OrdinalIgnoreCase))
        {
            var its = new HIDLObject();
            foreach (var (key, value) in ExtensionSettings[id].OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
                its.Add(key, value);
            extensions.Add(id, its);
        }

        // Only what isn't what it comes as. A setting the folder's own file covers is written as the user had it, the
        // folder's value is the folder's and never ends up in here
        var settings = new HIDLObject();
        foreach (var (key, value) in EditorValues())
        {
            // The languages files were set to by hand are a folder's, never the user's
            if (key == LANGUAGE_OVERRIDES)
                continue;

            IRuntimeValue? own = WorkspaceValues.ContainsKey(key) ? userValues.GetValueOrDefault(key) : value;
            if (own is not null && !IsUsual(key, own))
                settings.Add(key, own);
        }

        // What the file had that is no setting of this Hex's is written back as it was: a newer Hex's setting, or a key
        // somebody mistyped and would rather find again than have vanish. Only what the rows write was written, so one
        // save by an older Hex dropped every setting a newer one had added
        foreach (var (key, value) in userValues.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!IsKnown(key))
                settings.Add(key, value);
        }

        // the editor's and the extensions' sections of Hex's one settings file, the terminal's kept as it is
        string text = Horizon.UI.Components.SettingsSections.Write(path, (Horizon.UI.Components.SettingsSections.EDITOR, settings),
            (Horizon.UI.Components.SettingsSections.EXTENSIONS, extensions));

        try
        {
            DocumentFile.Write(path, text, new System.Text.UTF8Encoding(false));
            return null;
        }
        catch (Exception e)
        {
            return $"couldn't keep the settings for next time: {e.Message}";
        }
    }

    /// <summary>Every one of the editor's settings as it is now by its key, in the order the file has them.</summary>
    public Dictionary<string, IRuntimeValue> EditorValues()
    {
        var written = new HIDLObject();
        foreach (HexSettingRow row in Rows)
            row.Write(this, written);
        return written.ToValue().Properties;
    }
}
