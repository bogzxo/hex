using System.Globalization;
using System.Text;

namespace Hex.Core;

/// <summary>
/// How Hex starts llama-server: the program, the model, the name it answers to, its port and context, where the model's
/// weights go (<see cref="LlamaCppServerArgs.AUTO"/> lets llama.cpp fit them, gpu puts them all on the card, experts
/// keeps a mixture of experts' experts in RAM, cpu uses the card for nothing) and anything else the user adds. It runs
/// one model, the one picked for code, and everything Hex asks of a model goes to it: predictions, Link Chat, edits by
/// instruction. A card's memory holds one model well, two would push each other out.
/// </summary>
internal sealed record LlamaCppServerConfig(string Server, string Model, string Alias, int Port, int Context, string Offload, string Extra, string? EditModel = null, string? EditAlias = null, IReadOnlyList<(string Alias, string Path)>? Others = null, IReadOnlyDictionary<string, LocalModelPreset>? ModelPresets = null)
{
    /// <summary>The preset of the model that answers to an alias (its sampling, context and offload in the router's preset), null when none was given.</summary>
    public LocalModelPreset? PresetOf(string alias) => ModelPresets is not null && ModelPresets.TryGetValue(alias, out LocalModelPreset? preset) ? preset : null;

    /// <summary>
    /// Every model the router serves besides the code model, in order: the edit model, then the others, a file or alias
    /// listed twice (or the code model's) once. Loaded one at a time (--models-max 1), swapped in when asked for.
    /// </summary>
    public IReadOnlyList<(string Alias, string Path)> Extras
    {
        get
        {
            var found = new List<(string Alias, string Path)>();
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(Model) };
            var names = new HashSet<string>(StringComparer.Ordinal) { Alias };
            void Add(string? alias, string? file)
            {
                if (string.IsNullOrEmpty(alias) || string.IsNullOrEmpty(file) || !files.Add(Path.GetFullPath(file)) || !names.Add(alias))
                    return;
                found.Add((alias, file));
            }
            Add(EditAlias, EditModel);
            if (Others is not null)
                foreach ((string alias, string file) in Others)
                    Add(alias, file);
            return found;
        }
    }

    /// <summary>The models the roles use (predictions, then the edit and chat model when it is another file): what Time It times, never the router's others.</summary>
    public IReadOnlyList<string> RoleAliases =>
        Router && Extras.Any(extra => extra.Alias == EditAlias) ? [Alias, EditAlias!] : [Alias];

    /// <summary>Whether the server routes between several models (the edit model and/or the others), one in memory at a time.</summary>
    public bool Routed => Extras.Count > 0;

    /// <summary>Whether a second model, a different file, is the one edits by instruction ask: one server routes between the two, one in memory at a time.</summary>
    public bool Router => !string.IsNullOrEmpty(EditModel) && !string.IsNullOrEmpty(EditAlias)
        && !string.Equals(Path.GetFullPath(EditModel), Path.GetFullPath(Model), StringComparison.OrdinalIgnoreCase);

    /// <summary>The name edits by instruction ask for: the edit model's in router mode, else the one model's.</summary>
    public string EditName => Router ? EditAlias! : Alias;

    // Equal by what it says, the other models by their names and files rather than by which list holds them: the settings
    // make a new list each time they're read, and a server restarted for every new list starts a new one every frame
    private string Key => string.Join('\n', Server, Model, Alias, Port, Context, Offload, Extra, EditModel, EditAlias,
        Others is null ? "" : string.Join('\n', Others.Select(other => other.Alias + "=" + other.Path)),
          ModelPresets is null ? "" : string.Join('\n', ModelPresets.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value)));

    // What the running server was started with: the same as Key but for the sampling of the presets (asked per request, never a reason
    // to start again) and the server-side fields of the models that aren't a role (the router reads its preset as it starts, so they
    // are written the next time it does)
    private string ServerKey => string.Join('\n', Server, Model, Alias, Port, Context, Offload, Extra, EditModel, EditAlias,
        Others is null ? "" : string.Join('\n', Others.Select(other => other.Alias + "=" + other.Path)),
        ModelPresets is null ? "" : string.Join('\n', ModelPresets.Where(pair => pair.Key == Alias || pair.Key == EditAlias)
            .OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value.Context + "|" + pair.Value.Offload + "|" + pair.Value.Extra.Trim())));

    /// <summary>Whether a server started with <paramref name="other"/> already runs as this asks: only a sampling change or one of a model no role uses is the difference.</summary>
    public bool SameServer(LlamaCppServerConfig? other) => other is not null && ServerKey == other.ServerKey;

    public bool Equals(LlamaCppServerConfig? other) => other is not null && Key == other.Key;

    public override int GetHashCode() => Key.GetHashCode(StringComparison.Ordinal);
}

/// <summary>The arguments llama-server is started with, and the addresses Hex asks it at.</summary>
internal static class LlamaCppServerArgs
{
    public const string AUTO = "auto", GPU = "gpu", EXPERTS = "experts", CPU = "cpu";

    /// <summary>Every offload there is, as the settings write them.</summary>
    public static readonly string[] Offloads = [AUTO, GPU, EXPERTS, CPU];

    /// <summary>The port llama-server listens on as it comes, its own.</summary>
    public const int USUAL_PORT = 8080;

    /// <summary>The context it's started with as it comes: room for a long file and a chat, small enough for any card.</summary>
    public const int USUAL_CONTEXT = 32768;

    /// <summary>The server's root on this machine.</summary>
    public static string Root(int port) => $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Where Hex's predictions ask it, its OpenAI completions.</summary>
    public static string CompletionsUrl(int port) => Root(port) + "/v1/completions";

    /// <summary>
    /// The arguments, one per item, never through a shell. It listens on this machine alone (Hex shares the model with
    /// paired devices over Hex Link, never the server itself), keeps one slot (one user, all the context for it), reuses
    /// what it has of a prompt that comes again (each keystroke's prompt is mostly the last one's) and uses flash
    /// attention where the card has it. What the user adds comes last and wins.
    /// </summary>
    public static List<string> Build(LlamaCppServerConfig config, string? preset = null)
    {
        if (config.Routed)
        {
            ArgumentNullException.ThrowIfNull(preset);
            var routed = new List<string>
            {
                "--models-preset", preset,
                "--models-max", "1",
                "--host", "127.0.0.1",
                "--port", config.Port.ToString(CultureInfo.InvariantCulture),
            };
            routed.AddRange(Split(config.Extra));
            return routed;
        }

        var args = new List<string>
        {
            "-m", config.Model,
            "--alias", config.Alias,
            "--host", "127.0.0.1",
            "--port", config.Port.ToString(CultureInfo.InvariantCulture),
            "-c", (config.PresetOf(config.Alias)?.Context ?? config.Context).ToString(CultureInfo.InvariantCulture),
            "-np", "1",
            "--cache-reuse", "256",
            "--jinja",
        };

        switch (config.Offload)
        {
            case GPU:
                args.AddRange(["-ngl", "999", "-fa", "on"]);
                break;
            case EXPERTS:
                args.AddRange(["-ngl", "999", "--cpu-moe", "-fa", "on"]);
                break;
            case CPU:
                args.AddRange(["-ngl", "0"]);
                break;
            default:
                args.AddRange(["--fit", "on", "-fa", "auto"]);
                break;
        }

        args.AddRange(Split(config.Extra));
        return args;
    }

    /// <summary>
    /// The preset llama-server's router reads (--models-preset): its sections are the model names requests route by, its
    /// keys the command line's arguments without dashes. The code model loads at start, the edit model when asked for.
    /// </summary>
    public static string Preset(LlamaCppServerConfig config)
    {
        var text = new StringBuilder();
        text.Append("version = 1\n\n[*]\nparallel = 1\ncache-reuse = 256\njinja = true\n");
        switch (config.Offload)
        {
            case GPU:
                text.Append("n-gpu-layers = 999\nflash-attn = on\n");
                break;
            case EXPERTS:
                text.Append("n-gpu-layers = 999\ncpu-moe = true\nflash-attn = on\n");
                break;
            case CPU:
                text.Append("n-gpu-layers = 0\n");
                break;
            default:
                text.Append("fit = on\nflash-attn = auto\n");
                break;
        }
        Model(text, config, config.Alias, config.Model, true);
        foreach ((string alias, string file) in config.Extras)
            Model(text, config, alias, file, false);
        return text.ToString();
    }

    // One model's section: its file, the context and, when it has a preset, that preset's sampling and offload
    private static void Model(StringBuilder text, LlamaCppServerConfig config, string alias, string file, bool startup)
    {
        LocalModelPreset? preset = config.PresetOf(alias);
        string context = (preset?.Context ?? config.Context).ToString(CultureInfo.InvariantCulture);
        text.Append($"\n[{Section(alias)}]\nmodel = {OneLine(file)}\nc = {context}\nload-on-startup = {(startup ? "true" : "false")}\n");
        if (preset is null)
            return;
        text.Append($"temp = {Number(preset.Temperature)}\ntop-p = {Number(preset.TopP)}\ntop-k = {preset.TopK.ToString(CultureInfo.InvariantCulture)}\n");
        text.Append($"min-p = {Number(preset.MinP)}\nrepeat-penalty = {Number(preset.RepeatPenalty)}\n");
        if (preset.Offload != config.Offload)
            text.Append(OffloadKeys(preset.Offload));
        foreach (string line in ExtraKeys(preset.Extra))
            text.Append(line).Append('\n');
    }

    private static string Number(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static string OffloadKeys(string offload) => offload switch
    {
        GPU => "n-gpu-layers = 999\nflash-attn = on\n",
        EXPERTS => "n-gpu-layers = 999\ncpu-moe = true\nflash-attn = on\n",
        CPU => "n-gpu-layers = 0\n",
        _ => "fit = on\nflash-attn = auto\n",
    };

    /// <summary>A model's extra arguments (--key value, --flag) as preset lines (key = value, key = true).</summary>
    internal static IEnumerable<string> ExtraKeys(string extra)
    {
        List<string> parts = Split(extra);
        for (int i = 0; i < parts.Count; i++)
        {
            if (!parts[i].StartsWith("--", StringComparison.Ordinal) || parts[i].Length <= 2)
                continue;
            string key = OneLine(parts[i][2..]);
            if (i + 1 < parts.Count && !parts[i + 1].StartsWith("--", StringComparison.Ordinal))
                yield return $"{key} = {OneLine(parts[++i])}";
            else
                yield return $"{key} = true";
        }
    }

    // A section's name without what would end it or start a comment
    private static string Section(string name) =>
        new([.. name.Where(c => c is not ('[' or ']' or ';' or '#' or '\r' or '\n')).Select(c => c)]);

    private static string OneLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ');

    /// <summary>
    /// A line of arguments split as a shell would: by spaces, with double quotes keeping spaces in one, a backslash before
    /// a quote meaning the quote. Nothing of a shell runs it, no variables, no globs.
    /// </summary>
    public static List<string> Split(string line)
    {
        var parts = new List<string>();
        var part = new StringBuilder();
        bool quoted = false, any = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\\' && i + 1 < line.Length && line[i + 1] == '"')
            {
                part.Append('"');
                i++;
                any = true;
            }
            else if (c == '"')
            {
                quoted = !quoted;
                any = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any)
                    parts.Add(part.ToString());
                part.Clear();
                any = false;
            }
            else
            {
                part.Append(c);
                any = true;
            }
        }
        if (any)
            parts.Add(part.ToString());
        return parts;
    }

    /// <summary>The command line as it reads, for the log and for copying, each argument quoted where it has to be.</summary>
    public static string Show(string server, IEnumerable<string> args) =>
        string.Join(' ', new[] { server }.Concat(args).Select(arg => arg.Length == 0 || arg.Any(char.IsWhiteSpace) || arg.Contains('"') ? $"\"{arg.Replace("\"", "\\\"", StringComparison.Ordinal)}\"" : arg));
}
