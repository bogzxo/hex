namespace Hex.Dawn;

// The llama-server Hex runs for the llama.cpp provider. Ticked with the status bar: while something wants the model
// (predictions, Link Chat's answers, a paired device Hex shares with) and it's set up and may start by itself, it's
// started, once per setup (a server that failed isn't started again until the settings change or Start is pressed);
// when nothing wants it any more and Hex started it, it's stopped, so its memory and the card are let go.
internal sealed partial class DawnScene
{
    private LlamaCppServer? llamaServer;

    // What it was last started for, so a failure isn't retried every tick, and whether it was started by hand
    private LlamaCppServerConfig? llamaTried;
    private bool llamaByHand;
    private int llamaChangedPosted;
    private LlamaCppServerState llamaShown = LlamaCppServerState.Stopped;

    /// <summary>The server, made the first time it's wanted.</summary>
    internal LlamaCppServer LlamaServer
    {
        get
        {
            if (llamaServer is null)
            {
                llamaServer = new LlamaCppServer();
                // Every line llama-server prints raises Changed: one post waits at a time, and it reads the state when it runs
                llamaServer.Changed += () =>
                {
                    if (Interlocked.Exchange(ref llamaChangedPosted, 1) == 0)
                        PostLink(() =>
                        {
                            Volatile.Write(ref llamaChangedPosted, 0);
                            LlamaServerChanged();
                        });
                };
            }
            return llamaServer;
        }
    }

    // Whether anything wants the model now
    private bool LlamaWanted() =>
        !HexAi.Off && settings.EditPredictionsProvider == PredictionProviders.LLAMACPP
        && (PredictionSettings.On(settings) || settings.EditPredictionsLinkChat || settings.EditPredictionsShare);

    private void UpdateLlamaServer()
    {
        // A role assigned from outside the window (the status bar's picker, the settings) draws it again; the roles' ids are part of its signature
        if (localAiWindow is not null && (settings.LocalAiPredictModel != localAiPredictDrawn || settings.LocalAiChatModel != localAiChatDrawn))
            RefreshLlamaSetup(onlyIfChanged: true);
        else
            TickLlamaSetup();
        // The library of local models (LocalModelLibrary.Shared) reads this scene's settings, scanned once as they appear
        if (!HexAi.Off && !ReferenceEquals(LocalModelLibrary.Shared.Settings, settings))
        {
            LocalModelLibrary.Shared.Settings = settings;
            _ = LocalModelLibrary.Shared.Refresh();
        }
        // Nothing wants the model and nothing was started: there is no setup to look at (reading it checks the disk now and then)
        bool wantedAtAll = LlamaWanted();
        LlamaCppServer? server = llamaServer;
        if (!wantedAtAll && llamaTried is null && server is null)
            return;
        if (settings.LlamaCppConfig is not { } config)
            return;
        bool wanted = wantedAtAll && settings.LlamaCppAutostart;

        // A change the running server needn't be started again for (a sampling field, a model no role uses) is only noted
        if (llamaTried is not null && config != llamaTried && config.SameServer(llamaTried))
            llamaTried = config;
        if (wanted && config != llamaTried && server?.State is not LlamaCppServerState.Starting)
        {
            llamaTried = config;
            StartLlamaServer(byHand: false);
            return;
        }

        // Nothing wants it now: one Hex started by itself is stopped, one started by hand is left to the person
        if (!wantedAtAll && !llamaByHand && server is { State: LlamaCppServerState.Ready or LlamaCppServerState.Starting, Adopted: false })
        {
            server.Stop();
            llamaTried = null;
        }
    }

    /// <summary>Starts the server with the settings' setup, off the editor's thread, and says how it went.</summary>
    internal void StartLlamaServer(bool byHand = true)
    {
        if (settings.LlamaCppConfig is not { } config)
        {
            Say(Said("llamacpp.not_set_up"), error: true);
            return;
        }
        llamaByHand = byHand;
        llamaTried = config;
        LlamaCppServer server = LlamaServer;
        _ = Task.Run(async () =>
        {
            bool ready = await server.Start(config).ConfigureAwait(false);
            PostLink(() =>
            {
                if (ready)
                {
                    // The predictions asked while it loaded were refused, the next keystroke asks again
                    predictionSession?.Engine.LinkSourceChanged();
                    predictionItemWritten = false;
                }
            });
        });
    }

    /// <summary>Stops the server Hex runs, and doesn't start it again by itself until the setup changes.</summary>
    internal void StopLlamaServer()
    {
        llamaByHand = false;
        llamaServer?.Stop();
    }

    // Said once as it becomes ready or fails, on the editor's thread
    private void LlamaServerChanged()
    {
        if (llamaServer is not { } server || server.State == llamaShown)
            return;
        llamaShown = server.State;
        predictionItemWritten = false;
        SettingsPage?.Refresh("llamacpp_setup");
        RefreshLlamaSetup(onlyIfChanged: true);
        switch (server.State)
        {
            case LlamaCppServerState.Ready:
                Say(Said("llamacpp.ready", ("model", server.Config?.Alias ?? string.Empty)));
                break;
            case LlamaCppServerState.Failed:
                Notify(Said("llamacpp.failed", ("why", server.Problem ?? string.Empty)), error: true);
                break;
        }
    }

    /// <summary>How the server stands, in words.</summary>
    internal string LlamaServerStateText(LlamaCppServer server) => server.State switch
    {
        LlamaCppServerState.Ready => Said(server.Adopted ? "llamacpp.state_adopted" : "llamacpp.state_ready", ("port", (server.Config?.Port ?? settings.LlamaCppPort).ToString(System.Globalization.CultureInfo.InvariantCulture))),
        LlamaCppServerState.Starting => Said("llamacpp.state_starting"),
        LlamaCppServerState.Failed => Said("llamacpp.state_failed", ("why", server.Problem ?? string.Empty)),
        _ => Said("llamacpp.state_stopped"),
    };

    private void ShutDownLlamaServer() => llamaServer?.Dispose();
}
