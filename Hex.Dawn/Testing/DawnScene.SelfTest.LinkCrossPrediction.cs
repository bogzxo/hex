using Hex.Core;
using Horizon.Input;
using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private const string LINK_PREDICTION_SECRET = "CROSS_PREDICTION_SECRET_1964";
    private const string LINK_PREDICTION_CODE = "class LinkPredictionProbe { int Value() => ";
    private const string LINK_PREDICTION_ANSWER = "42; }";

    private void TestLinkCrossPrediction(HexPointerScript test, Func<HexLinkPeer?> peer, bool client, string work)
    {
        if (!client)
        {
            HexFakePredictionServer? server = null;
            string firstPrompt = string.Empty;
            int requestsAtRevocation = 0;

            test.Run(() =>
            {
                File.WriteAllText(Path.Combine(work, ".env"), "TOKEN=" + LINK_PREDICTION_SECRET);
                var model = new HexFakePredictionServer();
                server = model;
                model.Next = new HexFakePredictionServer.Reply(HexFakePredictionServer.Kind.Answer, prompt =>
                {
                    Volatile.Write(ref firstPrompt, prompt);
                    model.Next = new HexFakePredictionServer.Reply(HexFakePredictionServer.Kind.Stall);
                    return LINK_PREDICTION_ANSWER;
                });
                ConfigureCrossPrediction(PredictionProviders.OLLAMA, model.Url);
                // A machine that only lends its model, its own predictions off, still serves (refused before 3.4.2)
                settings.EditPredictions = false;
            });
            test.Check("link-cross-prediction: serving requires explicit opt-in", () => !LinkPredictServes());
            test.Run(() =>
            {
                settings.EditPredictionsShare = true;
                ApplyLinkPredictions();
            });
            test.WaitFor(() => server?.Stalled > 0, 25.0f);
            test.Check("link-cross-prediction: the loopback model got the synthetic code and no excluded text", () =>
            {
                string prompt = Volatile.Read(ref firstPrompt);
                return prompt.Contains("LinkPredictionProbe", StringComparison.Ordinal)
                    && !prompt.Contains(LINK_PREDICTION_SECRET, StringComparison.Ordinal)
                    && server is { Stalled: > 0, LastPath: "/api/generate" };
            });
            test.WaitFor(() => server?.Gone > 0, 15.0f);
            test.Check("link-cross-prediction: typing on the client closed the stalled model stream", () => server?.Gone > 0);
            test.Run(() => requestsAtRevocation = server?.Requests ?? 0);
            test.Check("link-cross-prediction: server revoked Predict", () =>
                peer() is { } linked && LinkSetRights(linked.Device.Id, HexLinkRights.None));
            test.WaitFor(() => peer()?.IsClosed == true, 15.0f);
            test.Check("link-cross-prediction: revoked requests did not reach the model before the client finished", () =>
                peer()?.IsClosed == true && server?.Requests == requestsAtRevocation);
            test.Run(() =>
            {
                settings.EditPredictionsShare = false;
                server?.Dispose();
            });
            return;
        }

        Task<Exception?>? read = null, denied = null;
        int requests = 0, cancelled = 0;
        string excluded = Path.Combine(work, ".env");
        string fixture = Path.Combine(work, "LinkPredictionProbe.cs");
        test.Run(() =>
        {
            ConfigureCrossPrediction(PredictionProviders.LINK, string.Empty);
            File.WriteAllText(excluded, "TOKEN=" + LINK_PREDICTION_SECRET);
            File.WriteAllText(fixture, LINK_PREDICTION_CODE);
            if (peer() is { } linked)
                read = CrossPredictionFailure(async () => { await linked.Read($"{LINK_CROSS_FOLDER}/.env"); });
        });
        test.WaitFor(() => read?.IsCompleted == true && LinkPredictSource is not null, 15.0f);
        test.Check("link-cross-prediction: Predict pairs alone and cannot read host files", () =>
            peer() is { Granted: HexLinkRights.Predict } && LinkPredictSource is not null
            && read is { IsCompletedSuccessfully: true, Result: HexLinkException { Code: HexLinkErrors.NOT_ALLOWED } });

        test.Run(() => OpenCrossPredictionFile(excluded));
        test.Wait();
        test.Run(() =>
        {
            requests = predictions.Requests;
            Engine.Input.Post(InputEvent.Typed("x"));
        });
        test.Settle(1.0f);
        test.Check("link-cross-prediction: a local .env edit never requests a prediction", () =>
            code.Text.Contains(LINK_PREDICTION_SECRET + "x", StringComparison.Ordinal)
            && predictions.Requests == requests && !code.HasPrediction);

        test.Run(() => OpenCrossPredictionFile(fixture));
        test.Wait();
        test.Run(PredictNow);
        test.WaitFor(() => code.PredictionIsGhost, 15.0f);
        test.Check("link-cross-prediction: streamed model text appears as editor ghost text", () =>
            code.PredictionIsGhost && code.Prediction is { Edits: [var edit] } && edit.Text == LINK_PREDICTION_ANSWER);
        test.Run(() => cancelled = predictions.Cancelled);
        PressKeys(test, "Tab");
        test.Check("link-cross-prediction: Tab accepts the linked suggestion in the editor", () =>
            code.Text == LINK_PREDICTION_CODE + LINK_PREDICTION_ANSWER);
        test.WaitFor(() => predictions.Status == PredictionStatus.Waiting, 10.0f);
        test.Settle(1.0f);
        test.Run(() =>
        {
            // Give revocation time to reach the client before the edit's replacement request.
            settings.EditPredictionsDebounce = 4000;
            Engine.Input.Post(InputEvent.Typed("x"));
        });
        test.WaitFor(() => peer() is { Granted: HexLinkRights.None } && LinkPredictSource is null, 15.0f);
        test.Check("link-cross-prediction: a typed edit cancelled the pending prediction and revocation removed the source", () =>
            predictions.Cancelled > cancelled && !code.HasPrediction && LinkPredictSource is null
            && peer() is { Granted: HexLinkRights.None });
        test.Run(() =>
        {
            if (peer() is { } linked)
                denied = CrossPredictionFailure(async () =>
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    await foreach (string _ in linked.Predict(new HexLinkPrediction("synthetic revocation probe", "", 8, [], "qwen"), deadline.Token))
                    {
                    }
                });
        });
        test.WaitFor(() => denied?.IsCompleted == true, 10.0f);
        test.Check("link-cross-prediction: the wire refuses Predict after revocation", () =>
            denied is { IsCompletedSuccessfully: true, Result: HexLinkException { Code: HexLinkErrors.NOT_ALLOWED } });
    }

    private void ConfigureCrossPrediction(string provider, string url)
    {
        settings.EditPredictions = true;
        settings.EditPredictionsWorkspace = true;
        settings.EditPredictionsNoticeDone = true;
        settings.EditPredictionsShare = false;
        settings.EditPredictionsProvider = provider;
        settings.EditPredictionsApiUrl = url;
        settings.EditPredictionsApiKey = string.Empty;
        settings.EditPredictionsModel = "cross-fixture";
        settings.EditPredictionsPromptFormat = "qwen";
        settings.EditPredictionsDebounce = 60;
        settings.EditPredictionsDisabledLanguages = [];
        settings.EditPredictionsExclude = HexSettings.UsualEditPredictionsExclude;
    }

    private void OpenCrossPredictionFile(string path)
    {
        Open(path);
        code.Focus();
        code.CompletionTrigger = _ => false;
        code.CompletionAvailable = () => false;
        code.Document.MoveTo(new UITextPosition(0, code.Document.LineAt(0).Length));
    }

    private static async Task<Exception?> CrossPredictionFailure(Func<Task> work)
    {
        try
        {
            await work();
            return null;
        }
        catch (HexLinkException error)
        {
            return error;
        }
    }
}
