using System.Globalization;
using System.Numerics;

using Horizon.Core.Threading;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// What the Local AI window does with the engine: what this machine is and llama.cpp's newest build, the build and model
// downloads (Hex's work items, cancellable from the Work panel and carried on from where they stopped), timing the server,
// asking whether Ollama answers and what the card has in use. The window's pages (DawnScene.LocalAiWindow*.cs) draw them.
internal sealed partial class DawnScene
{
    private const string LLAMA_BUILD_WORK = "llamacpp:build", LLAMA_MODEL_WORK = "llamacpp:model";

    private LlamaCppPlatform? llamaPlatform;
    private LlamaCppRelease? llamaRelease;
    private string? llamaReleaseProblem, llamaWorkProblem, llamaWorkProblemOf, llamaBackend;
    private bool llamaAsking, llamaMeasuring;
    private List<(string Model, LlamaCppSpeed Speed)> llamaSpeeds = [];
    private Label? llamaServerText, llamaLogText, localAiVramText;

    // What the download going says now, written from its thread and read by the tick
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> llamaWorkSaid = new();

    // Each download's bar and words in the window now, by its work's key, written by the tick
    private readonly Dictionary<string, (ProgressBar Bar, Label Said)> llamaProgress = [];

    // Whether Ollama answered at the URL (null: not asked or asking), and what the card has in use (MiB used, total)
    private bool? localAiOllamaUp;
    private bool localAiOllamaAsking, localAiVramAsking;
    private (long Used, long Total)? localAiVram;
    private long localAiVramAt, localAiGpuAt, localAiFoldersAt;

    private static readonly HttpClient localAiHttp = new() { Timeout = TimeSpan.FromSeconds(2) };

    // Says how far a piece of work has got, on its card and in the window
    private void LlamaReport(string key, WorkContext work, float progress, string said)
    {
        llamaWorkSaid[key] = said;
        work.Report(progress, said);
    }

    // What this machine is and the newest release, asked once each, off the editor's thread
    private void AskLlamaMachine()
    {
        if (llamaAsking || llamaPlatform is not null && llamaRelease is not null)
            return;
        llamaAsking = true;
        llamaReleaseProblem = null;
        _ = Task.Run(async () =>
        {
            LlamaCppPlatform platform = llamaPlatform ?? LlamaCppPlatform.Detect();
            LlamaCppRelease? release = null;
            string? problem = null;
            try { release = await LlamaCppBuilds.Latest(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { problem = e.Message; }
            PostLink(() =>
            {
                llamaAsking = false;
                llamaPlatform = platform;
                llamaRelease = release ?? llamaRelease;
                llamaReleaseProblem = problem;
                if (llamaRelease is { } known && (llamaBackend is null || LlamaCppBuilds.Choose(platform, llamaBackend, known.Assets) is null))
                    llamaBackend = LlamaCppBuilds.Backends(platform, known.Assets).FirstOrDefault();
                RefreshLlamaSetup(onlyIfChanged: true);
            });
        });
    }

    // Whether Ollama answers at its URL, asked off the editor's thread
    private void AskOllama()
    {
        if (localAiOllamaAsking)
            return;
        localAiOllamaAsking = true;
        string url = settings.EditPredictionsApiUrl.TrimEnd('/') + "/api/tags";
        _ = Task.Run(async () =>
        {
            bool up;
            try { up = (await localAiHttp.GetAsync(url).ConfigureAwait(false)).IsSuccessStatusCode; }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException) { up = false; }
            PostLink(() =>
            {
                localAiOllamaAsking = false;
                localAiOllamaUp = up;
                RefreshLlamaSetup(onlyIfChanged: true);
            });
        });
    }

    // The card's memory in use, asked at most every few seconds while the window is up
    private void PollLocalAiVram()
    {
        // Only the pages that show the card's memory or what's loaded ask for it
        if (localAiVramText is null && localAiLoadedText is null && localAiPage != LocalAiPage.Diagnostics)
            return;
        long now = Environment.TickCount64;
        if (localAiVramAsking || now - localAiVramAt < 4000)
            return;
        localAiVramAsking = true;
        localAiVramAt = now;
        // nvidia-smi is a process: asked every 30 s, or at once when the router's loaded models changed; the cheap router poll is every few seconds
        bool askCard = localAiGpuAt == 0 || now - localAiGpuAt >= 30000;
        if (askCard)
            localAiGpuAt = now;
        (long Used, long Total)? lastUsage = localAiVram;
        LlamaCppServer? server = llamaServer is { State: LlamaCppServerState.Ready } ready ? ready : null;
        int port = server?.Config?.Port ?? settings.LlamaCppPort;
        _ = Task.Run(async () =>
        {
            (long, long)? usage = askCard ? LocalAiGpu.Usage() : lastUsage;
            // What the server says it has loaded, so the line and the memory figure agree
            List<LlamaCppRouterModel>? router = server is null ? null : await server.RouterAt(port, CancellationToken.None).ConfigureAwait(false);
            List<string>? loaded = router?.Where(model => model.State == "loaded").Select(model => model.Id).ToList();
            PostLink(() =>
            {
                localAiVramAsking = false;
                if (localAiWindow is null)
                    return;
                // A model loaded or let go moves the card's memory: the next poll asks it again
                if (!(loaded ?? []).SequenceEqual(localAiLoaded ?? []))
                    localAiGpuAt = 0;
                localAiVram = usage;
                localAiLoaded = loaded;
                localAiRouter = router;
                if (localAiVramText is not null)
                    localAiVramText.Text = UIRichText.Literal(LocalAiVramWords());
                if (localAiLoadedText is not null && llamaServer?.Config is { } config)
                    localAiLoadedText.Text = UIRichText.Literal(LocalAiLoadedWords(config.Alias));
            });
        });
    }

    private string LocalAiVramWords() => localAiVram is { } usage
        ? Said("local_ai.vram", ("used", (usage.Used / 1024.0).ToString("0.0", CultureInfo.InvariantCulture)), ("total", (usage.Total / 1024.0).ToString("0.0", CultureInfo.InvariantCulture)))
        : Said("local_ai.vram_unknown");

    // The llama-server the settings name, or one in the folder builds go in
    private string? FoundLlamaServer() =>
        settings.LlamaCppServer.Length > 0 && File.Exists(settings.LlamaCppServer) ? settings.LlamaCppServer : LlamaCppDownload.FindServer(settings.LlamaCppFolderInForce);

    // Takes the llama-server found on disk into the settings, so the config exists without the Engine page having been drawn
    private string? AdoptLlamaServer()
    {
        if (settings.LocalAiEngine == HexSettings.LOCAL_AI_OLLAMA || FoundLlamaServer() is not { } found)
            return null;
        if (settings.LlamaCppServer != found)
        {
            settings.LlamaCppServer = found;
            SaveSettings();
        }
        return found;
    }

    // How much room the disk of a folder has left, in words
    private string LlamaFree(string folder)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(folder));
            return root is null ? "?" : Said("local_ai.gib", ("size", (new DriveInfo(root).AvailableFreeSpace / (double)(1L << 30)).ToString("0", CultureInfo.InvariantCulture)));
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return "?";
        }
    }

    // The build's archives downloaded and unpacked into a folder of their own (b12651), llama-server found in it
    private void DownloadLlamaBuild(LlamaCppRelease release, LlamaCppBuild build)
    {
        string into = Path.Combine(settings.LlamaCppFolderInForce, release.Tag + "-" + build.Backend);
        string downloads = Path.Combine(settings.LlamaCppFolderInForce, "downloads");
        llamaWorkProblem = null;
        LlamaCppAsset[] files = build.Runtime is { } runtime ? [build.Archive, runtime] : [build.Archive];
        long total = files.Sum(file => file.Size);
        string? server = null;
        Context.Work.Start(LLAMA_BUILD_WORK, Said("llamacpp.work_build", ("tag", release.Tag)), async work =>
        {
            long before = 0;
            foreach (LlamaCppAsset file in files)
            {
                string path = Path.Combine(downloads, file.Name);
                long done = before;
                await LlamaCppDownload.File(file.Url, path, file.Size, null, (have, _) =>
                    LlamaReport(LLAMA_BUILD_WORK, work, total > 0 ? (float)(done + have) / total : 0, Said("llamacpp.work_downloading", ("file", file.Name), ("mb", ((done + have) / 1e6).ToString("0", CultureInfo.InvariantCulture)),
                        ("of", (total / 1e6).ToString("0", CultureInfo.InvariantCulture)))), work.Token).ConfigureAwait(false);
                before += file.Size;
                LlamaReport(LLAMA_BUILD_WORK, work, (float)before / total, Said("llamacpp.work_unpacking", ("file", file.Name)));
                await Task.Run(() => LlamaCppDownload.Unpack(path, into), work.Token).ConfigureAwait(false);
            }
            server = LlamaCppDownload.FindServer(into) ?? throw new InvalidDataException(Said("llamacpp.build_no_server"));
        }, item =>
        {
            if (item.State == WorkState.Succeeded && server is not null)
            {
                settings.LlamaCppServer = server;
                SaveSettings();
                Say(Said("llamacpp.build_done", ("tag", release.Tag)));
            }
            else if (item.State == WorkState.Failed)
                (llamaWorkProblem, llamaWorkProblemOf) = (item.Error, LLAMA_BUILD_WORK);
            RefreshLlamaSetup();
        });
        RefreshLlamaSetup();
    }

    /// <summary>The catalog model's file in the models folder, when it's there whole.</summary>
    private string? CatalogFileOnDisk(LlamaCppModel model)
    {
        string path = Path.Combine(settings.LlamaCppModelsInForce, model.File);
        return File.Exists(path) && new FileInfo(path).Length == model.Size ? path : null;
    }

    // A catalog model downloaded to the models folder, checked; the library is scanned again once it's there
    private void DownloadCatalogModel(LlamaCppModel model, Action? done = null)
    {
        string path = Path.Combine(settings.LlamaCppModelsInForce, model.File);
        llamaWorkProblem = null;
        Context.Work.Start(LLAMA_MODEL_WORK + model.Id, Said("llamacpp.work_model", ("model", model.Title)), async work =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            long first = File.Exists(path + ".part") ? new FileInfo(path + ".part").Length : 0;
            await LlamaCppDownload.File(model.Url, path, model.Size, model.Sha256, (have, total) =>
            {
                double rate = (have - first) / Math.Max(1, clock.Elapsed.TotalSeconds) / 1e6;
                LlamaReport(LLAMA_MODEL_WORK + model.Id, work, total > 0 ? (float)have / total : 0, Said("llamacpp.work_model_progress", ("model", model.Title), ("gb", (have / 1e9).ToString("0.0", CultureInfo.InvariantCulture)),
                    ("of", (model.Size / 1e9).ToString("0.0", CultureInfo.InvariantCulture)), ("rate", rate.ToString("0", CultureInfo.InvariantCulture))));
            }, work.Token).ConfigureAwait(false);
        }, item =>
        {
            if (item.State == WorkState.Succeeded)
            {
                Say(Said("llamacpp.model_done", ("model", model.Title)));
                _ = LocalModelLibrary.Shared.Refresh().ContinueWith(_ => PostLink(() =>
                {
                    done?.Invoke();
                    RefreshLlamaSetup();
                }), TaskScheduler.Default);
            }
            else if (item.State == WorkState.Failed)
                (llamaWorkProblem, llamaWorkProblemOf) = (item.Error, LLAMA_MODEL_WORK + model.Id);
            RefreshLlamaSetup();
        });
        RefreshLlamaSetup();
    }

    // A piece of work's bar and what it says, written by the tick; what went wrong once it's over
    private void LlamaWorkRow(StackPanel body, string key)
    {
        if (Context.Work.Find(key) is { } going)
        {
            ProgressBar bar = body.Add(new ProgressBar { Size = new Vector2(0, 8), Fill = UIFill.Horizontal, ShowText = false, Progress = going.Progress ?? 0, FillColor = Skin?.Palette.Accent });
            llamaProgress[key] = (bar, body.Add(LinkLine(llamaWorkSaid.GetValueOrDefault(key) ?? string.Empty)));
            LinkActionRow(body).Add(LinkButton(Said("llamacpp.cancel"), "debug-stop", () =>
            {
                going.Cancel();
                RefreshLlamaSetup();
            }));
        }
        else if (llamaWorkProblem is { } why && llamaWorkProblemOf == key)
            body.Add(LinkLine("$(error) " + why));
    }

    private bool AnyModelWork() => LlamaCppModels.All.Any(model => Context.Work.Find(LLAMA_MODEL_WORK + model.Id) is not null);

    // The model asked to write a few lines, timed
    private void MeasureLlama()
    {
        if (settings.LlamaCppConfig is not { } config)
            return;
        llamaMeasuring = true;
        LlamaCppServer server = LlamaServer;
        // Each role's model is timed, and only those: timing another of the router's models would load it
        string[] models = [.. config.RoleAliases];
        _ = Task.Run(async () =>
        {
            var speeds = new List<(string Model, LlamaCppSpeed Speed)>();
            string? problem = null;
            try
            {
                foreach (string model in models)
                    speeds.Add((model, await server.Measure(config.Port, model, CancellationToken.None).ConfigureAwait(false)));
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { problem = e.Message; }
            PostLink(() =>
            {
                llamaMeasuring = false;
                llamaSpeeds = speeds;
                foreach (var (model, speed) in speeds)
                    localAiTimes.Add((DateTime.Now, model, speed));
                if (problem is not null)
                    Say(Said("llamacpp.measure_failed", ("why", problem)), error: true);
                // Timing loads the model, so what the router holds is asked again now rather than in a few seconds
                localAiVramAt = localAiGpuAt = 0;
                RefreshLlamaSetup();
            });
        });
        RefreshLlamaSetup();
    }

    // The last lines of the log, rebuilt only when a line came since (the tick asks every frame the window is up)
    private string LlamaLogTail(LlamaCppServer server)
    {
        int version = server.LogVersion;
        if (llamaLogTail is null || version != llamaLogTailVersion || !ReferenceEquals(server, llamaLogTailOf))
        {
            llamaLogTail = UIRichText.Literal(string.Join("\n", server.LogTail(8, 160)));
            (llamaLogTailVersion, llamaLogTailOf) = (version, server);
        }
        return llamaLogTail;
    }

    private (LlamaCppServerState, bool, string?, LlamaCppServerConfig?) llamaStateKey;
    private string? llamaStateWords, llamaLogTail;
    private int llamaLogTailVersion;
    private LlamaCppServer? llamaLogTailOf;

    /// <summary>Every tick while the window is open: a download's bar and words, and the server's state and log, written in place.</summary>
    private void TickLlamaSetup()
    {
        // A setup being used goes on with its window closed
        AdvanceLocalAiRun();
        if (localAiWindow is null)
            return;
        if (localAiDirty && !LocalAiEditing())
            RefreshLlamaSetup();
        PollLocalAiVram();
        // The card of a run just started is brought to the top once it has been laid out
        if (localAiScrollPending && localAiScrollTo is { } card && localAiScroll is { } scroll && !card.Bounds.IsEmpty)
        {
            localAiScrollPending = false;
            localAiScrollTo = null;
            scroll.ScrollToTop(card);
        }
        // The models folder or the extra folders changed in the settings: scan again, so the lists aren't of the old folders
        // (asked at most every two seconds: it normalises every folder path, which is no work for each frame)
        long clock = Environment.TickCount64;
        if (clock - localAiFoldersAt >= 2000)
        {
            localAiFoldersAt = clock;
            if (LocalModelLibrary.Shared.FoldersStale())
                _ = LocalModelLibrary.Shared.Refresh();
        }
        foreach (var (key, (bar, label)) in llamaProgress)
        {
            if (Context.Work.Find(key) is not { } going)
                continue;
            bar.Progress = going.Progress ?? 0;
            string said = UIRichText.Literal(llamaWorkSaid.GetValueOrDefault(key) ?? string.Empty);
            if (label.Text != said)
                label.Text = said;
        }
        if (llamaServer is { } server)
        {
            if (llamaServerText is not null)
            {
                // Worded again only when what the words are made of changed, not every frame
                var key = (server.State, server.Adopted, server.Problem, server.Config);
                if (llamaStateKey != key || llamaStateWords is null)
                {
                    llamaStateKey = key;
                    llamaStateWords = UIRichText.Literal(LlamaServerStateText(server));
                }
                if (llamaServerText.Text != llamaStateWords)
                    llamaServerText.Text = llamaStateWords;
            }
            if (llamaLogText is not null)
            {
                string tail = LlamaLogTail(server);
                if (llamaLogText.Text != tail)
                    llamaLogText.Text = tail;
            }
        }
    }
}
