using System.Diagnostics;
using System.Text.Json;

namespace Hex.Core;

/// <summary>The JSON llama.cpp's code writes by itself, made ahead of time (Hex is published trimmed and native).</summary>
[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class LlamaCppJson : System.Text.Json.Serialization.JsonSerializerContext;

/// <summary>How the llama-server Hex looks after stands.</summary>
internal enum LlamaCppServerState { Stopped, Starting, Ready, Failed }

/// <summary>One model of a router's /v1/models: its id, its state (loaded, unloaded, loading) and the file name its args load.</summary>
internal sealed record LlamaCppRouterModel(string Id, string State, string? File);

/// <summary>How fast a model answered: its prompt read and its answer written, in tokens a second, and what it wrote.</summary>
internal sealed record LlamaCppSpeed(double PromptPerSecond, double AnswerPerSecond, int Tokens, string Text);

/// <summary>
/// The llama-server Hex starts and stops: started with the settings' arguments (never through a shell), its output kept
/// (the last few hundred lines), ready once /health says ok, which takes as long as loading the model does. One already
/// answering on the port with the same model is used as it is and left running when Hex is done; one Hex started ends
/// with Hex, on Windows in a job that the system ends with Hex however Hex ends, elsewhere as Hex exits. Its state is
/// read from any thread; <see cref="Changed"/> is raised on the thread that changed it.
/// </summary>
internal sealed class LlamaCppServer : IDisposable
{
    private const int MOST_LOG = 400, MOST_LINE = 400;

    private readonly Lock gate = new();
    private readonly List<string> log = [];
    private readonly HttpClient http;
    private Process? process;
    private HexAgentJob? job;
    private CancellationTokenSource? starting;

    public LlamaCppServer(HttpClient? http = null)
    {
        this.http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Kill();
    }

    /// <summary>How it stands.</summary>
    public LlamaCppServerState State { get; private set; }

    /// <summary>What went wrong the last time it failed, in a sentence, null otherwise.</summary>
    public string? Problem { get; private set; }

    /// <summary>What it was started with, or found running with; null while it's stopped.</summary>
    public LlamaCppServerConfig? Config { get; private set; }

    /// <summary>Whether it was running before Hex asked for it, so Hex leaves it running.</summary>
    public bool Adopted { get; private set; }


    /// <summary>Raised whenever the state, the problem or the log changed.</summary>
    public event Action? Changed;

    /// <summary>The last lines it printed, oldest first.</summary>
    public string[] Log
    {
        get { lock (gate) return [.. log]; }
    }

    /// <summary>Counts every line written, so a reader of the log can tell nothing new came without copying it.</summary>
    public int LogVersion => Volatile.Read(ref logVersion);

    private int logVersion;

    /// <summary>The last <paramref name="count"/> lines that aren't blank, each cut to <paramref name="width"/> characters (with "…"), oldest first. No copy of the whole ring.</summary>
    public string[] LogTail(int count, int width)
    {
        lock (gate)
        {
            var tail = new List<string>(count);
            for (int i = log.Count - 1; i >= 0 && tail.Count < count; i--)
            {
                string line = log[i];
                if (!string.IsNullOrWhiteSpace(line))
                    tail.Add(line.Length > width ? line[..width] + "…" : line);
            }
            tail.Reverse();
            return [.. tail];
        }
    }

    /// <summary>
    /// Starts it, or carries on with the one going when it's the same. A server already answering on the port with the
    /// model's name is used as it is; anything else there is a problem. Ready once /health says so, failed when the
    /// program ends first or doesn't answer within <paramref name="loading"/>.
    /// </summary>
    public Task<bool> Start(LlamaCppServerConfig config, TimeSpan? loading = null, CancellationToken cancel = default)
    {
        int mine;
        lock (gate)
        {
            // Asked at every request while it's going: no state machine for the answer that's already known
            if (Config == config && State is LlamaCppServerState.Ready or LlamaCppServerState.Starting)
                return Task.FromResult(State == LlamaCppServerState.Ready);
            mine = ++tries;
        }
        return StartNew(config, loading, cancel, mine);
    }

    private async Task<bool> StartNew(LlamaCppServerConfig config, TimeSpan? loading, CancellationToken cancel, int mine)
    {
        // One start at a time up to its process being held: two at once would each start one, and the first be lost
        try
        {
            await launching.WaitAsync(cancel).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        Process started;
        CancellationTokenSource waiting;
        string root = LlamaCppServerArgs.Root(config.Port);
        try
        {
            if (Superseded(mine))
                return false;
            if (await Launch(config, root, mine, cancel).ConfigureAwait(false) is not { } launched)
                return State == LlamaCppServerState.Ready && Config == config;
            (started, waiting) = launched;
        }
        finally
        {
            launching.Release();
        }

        // Ready when /health says ok; it says 503 while the model loads
        var until = Stopwatch.StartNew();
        TimeSpan most = loading ?? TimeSpan.FromMinutes(10);
        try
        {
            while (until.Elapsed < most)
            {
                if (started.HasExited)
                    return false;
                if (await Healthy(root, waiting.Token).ConfigureAwait(false)
                    && (!config.Routed || await LoadsCodeModel(root, config.Alias, until, most, started, waiting.Token).ConfigureAwait(false)))
                {
                    lock (gate)
                    {
                        if (process != started)
                            return false;
                        State = LlamaCppServerState.Ready;
                    }
                    Write($"Ready at {root}, loaded in {until.Elapsed.TotalSeconds:0.0} s.");
                    return true;
                }
                await Task.Delay(500, waiting.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            Abandon(started);
            return false;
        }

        if (Abandon(started))
            Set(LlamaCppServerState.Failed, $"llama-server didn't answer within {most.TotalMinutes:0} minutes. Its log says why.", null);
        return false;
    }

    private readonly SemaphoreSlim launching = new(1, 1);
    private int tries;

    // Whether a later start came along: it's the one that counts
    private bool Superseded(int mine)
    {
        lock (gate)
            return mine != tries;
    }

    // Ends a process a start began: through Stop while it's still the one held, else by itself, never touching a later one's
    private bool Abandon(Process started)
    {
        bool held;
        lock (gate)
            held = process == started;
        if (held)
        {
            StopHeld();
            return true;
        }
        try
        {
            if (!started.HasExited)
                started.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        return false;
    }

    // Stops what ran, then adopts one already answering or starts the program and holds it; null when nothing was started
    private async Task<(Process, CancellationTokenSource)?> Launch(LlamaCppServerConfig config, string root, int mine, CancellationToken cancel)
    {
        StopHeld();

        if (await Answers(root, cancel).ConfigureAwait(false) is { } running)
        {
            if (running.Contains(config.Alias))
            {
                Set(LlamaCppServerState.Ready, null, config, adopted: true);
                WriteOnce($"Using the llama-server already running at {root} ({config.Alias}).");
                return null;
            }
            // A router that calls the same model file by another name is used under its name
            if (running.Count > 0 && await AliasOfFile(root, config.Model, cancel).ConfigureAwait(false) is { } theirs)
            {
                Set(LlamaCppServerState.Ready, null, config with { Alias = theirs }, adopted: true);
                WriteOnce($"Using the llama-server already running at {root}, which serves {Path.GetFileName(config.Model)} as {theirs}.");
                return null;
            }
            Set(LlamaCppServerState.Failed, running.Count == 0
                ? $"Something is answering on port {config.Port} but it lists no models. Stop it, or pick another port."
                : $"A llama-server on port {config.Port} serves other models ({string.Join(", ", running)}). Stop it, or pick another port.", null);
            return null;
        }

        if (!File.Exists(config.Server))
        {
            Set(LlamaCppServerState.Failed, $"llama-server isn't at {config.Server}. Run Set Up llama.cpp again.", null);
            return null;
        }
        if (!File.Exists(config.Model))
        {
            Set(LlamaCppServerState.Failed, $"The model isn't at {config.Model}. Run Set Up llama.cpp again, or pick the file.", null);
            return null;
        }
        string? preset = null;
        if (config.Routed)
        {
            if (config.Router && !File.Exists(config.EditModel))
            {
                Set(LlamaCppServerState.Failed, $"The edit model isn't at {config.EditModel}. Pick its file again, or leave it empty.", null);
                return null;
            }
            try
            {
                preset = PresetPath ?? Path.Combine(HexData.Root, "llama.cpp", "hex-models.ini");
                Directory.CreateDirectory(Path.GetDirectoryName(preset)!);
                File.WriteAllText(preset, LlamaCppServerArgs.Preset(config));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Set(LlamaCppServerState.Failed, $"The models preset couldn't be written: {e.Message}", null);
                return null;
            }
        }
        List<string> args = LlamaCppServerArgs.Build(config, preset);
        var info = new ProcessStartInfo(config.Server)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(config.Server)!,
        };
        foreach (string arg in args)
            info.ArgumentList.Add(arg);
        // A CUDA build is quicker for it, and it costs nothing elsewhere
        info.Environment["GGML_CUDA_GRAPH_OPT"] = "1";

        if (Superseded(mine))
            return null;
        Set(LlamaCppServerState.Starting, null, config);
        Write("> " + LlamaCppServerArgs.Show(config.Server, args));
        Process started;
        try
        {
            started = Process.Start(info) ?? throw new InvalidOperationException("the system didn't start it");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Set(LlamaCppServerState.Failed, $"llama-server couldn't be started: {e.Message}", null);
            return null;
        }

        var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        Process? before = null;
        HexAgentJob? beforeJob = null;
        bool stopped;
        lock (gate)
        {
            // Stopped while it was starting: it never runs
            stopped = mine != tries;
            if (!stopped)
            {
                before = process;
                beforeJob = job;
                process = started;
                job = HexAgentJob.Hold(started);
                starting = waiting;
            }
        }
        if (stopped)
        {
            try { started.Kill(entireProcessTree: true); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            started.Dispose();
            waiting.Dispose();
            bool idle;
            lock (gate)
                idle = process is null && State == LlamaCppServerState.Starting;
            if (idle)
                Set(LlamaCppServerState.Stopped, null, null);
            return null;
        }
        // Never two: one still held (it shouldn't be, Stop ran) goes now
        if (before is not null)
        {
            beforeJob?.Dispose();
            try { if (!before.HasExited) before.Kill(entireProcessTree: true); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        started.OutputDataReceived += (_, said) => Write(said.Data);
        started.ErrorDataReceived += (_, said) => Write(said.Data);
        started.EnableRaisingEvents = true;
        started.Exited += (_, _) => Ended(started);
        started.BeginOutputReadLine();
        started.BeginErrorReadLine();
        return (started, waiting);
    }

    /// <summary>Where the router's preset is written, null for the file in Hex's data folder.</summary>
    internal string? PresetPath { get; init; }

    // A router says /health ok before any model is in: one token asked of the code model loads it, and it's ready when that answers
    private async Task<bool> LoadsCodeModel(string root, string alias, Stopwatch until, TimeSpan most, Process started, CancellationToken cancel)
    {
        string named = JsonSerializer.Serialize(alias, LlamaCppJson.Default.String);
        using var slow = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(most > until.Elapsed ? most - until.Elapsed : TimeSpan.FromSeconds(1));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, root + "/completion")
            {
                Content = new StringContent("{\"model\":" + named + ",\"prompt\":\"//\",\"n_predict\":1}", System.Text.Encoding.UTF8, "application/json"),
            };
            using HttpResponseMessage answered = await slow.SendAsync(request, deadline.Token).ConfigureAwait(false);
            return answered.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !cancel.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Stops the one Hex started (one it found running is left to whoever started it). Nothing when it's stopped.</summary>
    public void Stop()
    {
        // A start still waiting its turn, or between starting the program and holding it, is no longer wanted
        lock (gate)
            tries++;
        StopHeld();
    }

    // Stops what's held without ending the starts that are queued (Launch and Abandon use it on their own)
    private void StopHeld()
    {
        lock (gate)
            starting?.Cancel();
        Kill();
        Set(LlamaCppServerState.Stopped, null, null);
    }

    // Ends the process Hex started, and everything in its job
    private void Kill()
    {
        Process? going;
        HexAgentJob? held;
        lock (gate)
        {
            going = process;
            held = job;
            process = null;
            job = null;
        }
        held?.Dispose();
        try
        {
            if (going is { HasExited: false })
                going.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        going?.Dispose();
    }

    // It ended by itself: a failure, with the last thing it said as the reason
    private void Ended(Process ended)
    {
        lock (gate)
        {
            if (process != ended)
                return;
            process = null;
            job?.Dispose();
            job = null;
        }
        string last = Log.LastOrDefault(line => line.Contains("error", StringComparison.OrdinalIgnoreCase) || line.Contains("failed", StringComparison.OrdinalIgnoreCase))
            ?? Log.LastOrDefault() ?? string.Empty;
        int code = 0;
        try { code = ended.ExitCode; } catch (InvalidOperationException) { }
        Set(LlamaCppServerState.Failed, $"llama-server ended (code {code}). {last}".Trim(), null);
    }

    private void Set(LlamaCppServerState state, string? problem, LlamaCppServerConfig? config, bool adopted = false)
    {
        lock (gate)
        {
            State = state;
            Problem = problem;
            Config = state == LlamaCppServerState.Failed ? Config : config;
            Adopted = adopted;
        }
        if (problem is not null)
            Write(problem);
        else
            Changed?.Invoke();
    }

    // A line that isn't said again when the log has it already (adopting the same server twice)
    private void WriteOnce(string line)
    {
        bool same;
        lock (gate)
            same = log.Contains(line);
        if (same)
            Changed?.Invoke();
        else
            Write(line);
    }

    private void Write(string? line)
    {
        if (line is null)
            return;
        lock (gate)
        {
            // A line is kept to what a screen can show: llama-server's progress lines can run to kilobytes, times four hundred
            log.Add(line.Length > MOST_LINE ? line[..MOST_LINE] : line);
            logVersion++;
            if (log.Count > MOST_LOG)
                log.RemoveRange(0, log.Count - MOST_LOG);
        }
        Changed?.Invoke();
    }

    // Whether /health says it's ready
    private async Task<bool> Healthy(string root, CancellationToken cancel)
    {
        try
        {
            using HttpResponseMessage answered = await http.GetAsync(root + "/health", cancel).ConfigureAwait(false);
            return answered.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancel.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>The names of the models a llama-server on this machine answers to, null when nothing answers there.</summary>
    public async Task<List<string>?> Answers(string root, CancellationToken cancel)
    {
        try
        {
            using HttpResponseMessage answered = await http.GetAsync(root + "/v1/models", cancel).ConfigureAwait(false);
            if (!answered.IsSuccessStatusCode)
                return [];
            using JsonDocument json = JsonDocument.Parse(await answered.Content.ReadAsStringAsync(cancel).ConfigureAwait(false));
            return Models(json.RootElement);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException && !cancel.IsCancellationRequested)
        {
            return e is JsonException ? [] : null;
        }
    }

    /// <summary>The id a running server or router knows a model file by (the file its status args name with --model), null when none is that file.</summary>
    public async Task<string?> AliasOfFile(string root, string modelPath, CancellationToken cancel)
    {
        try
        {
            using HttpResponseMessage answered = await http.GetAsync(root + "/v1/models", cancel).ConfigureAwait(false);
            if (!answered.IsSuccessStatusCode)
                return null;
            using JsonDocument json = JsonDocument.Parse(await answered.Content.ReadAsStringAsync(cancel).ConfigureAwait(false));
            return AliasOfFile(json.RootElement, modelPath);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException && !cancel.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>The id of the model in a /v1/models answer whose status args load the same file name as <paramref name="modelPath"/>.</summary>
    public static string? AliasOfFile(JsonElement root, string modelPath)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
            return null;
        string wanted = Path.GetFileName(modelPath.Replace('\\', '/'));
        foreach (JsonElement model in data.EnumerateArray())
        {
            if (!model.TryGetProperty("id", out JsonElement id) || id.ValueKind != JsonValueKind.String
                || !model.TryGetProperty("status", out JsonElement status) || status.ValueKind != JsonValueKind.Object
                || !status.TryGetProperty("args", out JsonElement args) || args.ValueKind != JsonValueKind.Array)
                continue;
            string? previous = null;
            foreach (JsonElement arg in args.EnumerateArray())
            {
                string? text = arg.ValueKind == JsonValueKind.String ? arg.GetString() : null;
                if (previous is "--model" or "-m" && text is not null
                    && string.Equals(Path.GetFileName(text.Replace('\\', '/')), wanted, StringComparison.OrdinalIgnoreCase))
                    return id.GetString();
                previous = text;
            }
        }
        return null;
    }

    /// <summary>The ids of the models loaded now: a router says each one's status, a plain server lists only what it has loaded.</summary>
    public static List<string> Loaded(JsonElement root)
    {
        var names = new List<string>();
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
            return names;
        foreach (JsonElement model in data.EnumerateArray())
        {
            if (!model.TryGetProperty("id", out JsonElement id) || id.ValueKind != JsonValueKind.String)
                continue;
            bool loaded = true;
            if (model.TryGetProperty("status", out JsonElement status) && status.ValueKind == JsonValueKind.Object
                && status.TryGetProperty("value", out JsonElement value) && value.ValueKind == JsonValueKind.String)
                loaded = value.GetString() == "loaded";
            if (loaded)
                names.Add(id.GetString()!);
        }
        return names;
    }

    /// <summary>Every model a /v1/models answer lists, with its state ("loaded" when a plain server lists it without one) and the file its status args load.</summary>
    public static List<LlamaCppRouterModel> RouterModels(JsonElement root)
    {
        var models = new List<LlamaCppRouterModel>();
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
            return models;
        foreach (JsonElement model in data.EnumerateArray())
        {
            if (!model.TryGetProperty("id", out JsonElement id) || id.ValueKind != JsonValueKind.String)
                continue;
            string state = "loaded";
            string? file = null;
            if (model.TryGetProperty("status", out JsonElement status) && status.ValueKind == JsonValueKind.Object)
            {
                if (status.TryGetProperty("value", out JsonElement value) && value.ValueKind == JsonValueKind.String)
                    state = value.GetString() ?? state;
                if (status.TryGetProperty("args", out JsonElement args) && args.ValueKind == JsonValueKind.Array)
                {
                    string? previous = null;
                    foreach (JsonElement arg in args.EnumerateArray())
                    {
                        string? text = arg.ValueKind == JsonValueKind.String ? arg.GetString() : null;
                        if (previous is "--model" or "-m" && text is not null)
                            file = Path.GetFileName(text.Replace('\\', '/'));
                        previous = text;
                    }
                }
            }
            models.Add(new LlamaCppRouterModel(id.GetString()!, state, file));
        }
        return models;
    }

    /// <summary>Every model of the server at a port with its state, null when nothing answers.</summary>
    public async Task<List<LlamaCppRouterModel>?> RouterAt(int port, CancellationToken cancel)
    {
        try
        {
            using HttpResponseMessage answered = await http.GetAsync(LlamaCppServerArgs.Root(port) + "/v1/models", cancel).ConfigureAwait(false);
            if (!answered.IsSuccessStatusCode)
                return null;
            using JsonDocument json = JsonDocument.Parse(await answered.Content.ReadAsStringAsync(cancel).ConfigureAwait(false));
            return RouterModels(json.RootElement);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }


    /// <summary>The model names in an OpenAI /v1/models answer, and the aliases llama-server adds to them.</summary>
    public static List<string> Models(JsonElement root)
    {
        var names = new List<string>();
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
            return names;
        foreach (JsonElement model in data.EnumerateArray())
        {
            if (model.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.String)
                names.Add(id.GetString()!);
            if (model.TryGetProperty("aliases", out JsonElement aliases) && aliases.ValueKind == JsonValueKind.Array)
                names.AddRange(aliases.EnumerateArray().Where(alias => alias.ValueKind == JsonValueKind.String).Select(alias => alias.GetString()!));
        }
        return names;
    }

    /// <summary>
    /// How fast the running model is: it's asked to go on from a line of code (its own /completion, which says its
    /// timings), and what it wrote and how fast it read and wrote are given back.
    /// </summary>
    public async Task<LlamaCppSpeed> Measure(int port, string model, CancellationToken cancel)
    {
        // The model named, which a router needs to know which of its models is meant
        string named = System.Text.Json.JsonSerializer.Serialize(model, LlamaCppJson.Default.String);
        using var request = new HttpRequestMessage(HttpMethod.Post, LlamaCppServerArgs.Root(port) + "/completion")
        {
            Content = new StringContent("{\"model\":" + named + ",\"prompt\":\"// A C# method that says whether a number is prime\\nstatic bool IsPrime(int n)\\n{\\n\",\"n_predict\":96,\"temperature\":0,\"cache_prompt\":false}",
                System.Text.Encoding.UTF8, "application/json"),
        };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        using var slow = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using HttpResponseMessage answered = await slow.SendAsync(request, deadline.Token).ConfigureAwait(false);
        string body = await answered.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
        if (!answered.IsSuccessStatusCode)
            throw new HttpRequestException($"llama-server said {(int)answered.StatusCode}: {body[..Math.Min(body.Length, 200)]}");
        return Speed(body);
    }

    /// <summary>The speed in llama-server's /completion answer, its timings and its content.</summary>
    public static LlamaCppSpeed Speed(string body)
    {
        using JsonDocument json = JsonDocument.Parse(body);
        JsonElement root = json.RootElement;
        string text = root.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.String ? content.GetString()! : string.Empty;
        double read = 0, wrote = 0;
        int tokens = 0;
        if (root.TryGetProperty("timings", out JsonElement timings) && timings.ValueKind == JsonValueKind.Object)
        {
            read = timings.TryGetProperty("prompt_per_second", out JsonElement p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : 0;
            wrote = timings.TryGetProperty("predicted_per_second", out JsonElement g) && g.ValueKind == JsonValueKind.Number ? g.GetDouble() : 0;
            tokens = timings.TryGetProperty("predicted_n", out JsonElement n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : 0;
        }
        return new LlamaCppSpeed(read, wrote, tokens, text);
    }

    public void Dispose()
    {
        if (!Adopted)
            Stop();
    }
}
