using System.Diagnostics;

using Hex.Core;

using Horizon.UI.Components;

namespace Hex.Dawn;

// Edit prediction against a real Ollama and a real model, only when HEX_LIVE_PREDICT is set to the server's URL (and
// HEX_LIVE_PREDICT_MODEL names the model, Zeta 2 when not): the model is unloaded first, so the first prediction pays the
// load while the frame time is watched; then ghost text in C# at the end of a file, in a line and over several lines, Tab,
// Escape, typing through what it says and typing on past a request in flight. HEX_SELFTEST_SHOTS is where screenshots go.
// Without the variable the part passes at once. The settings it changes are this run's own and put back at the end.
internal sealed partial class DawnScene
{
    private void TestPredictionLive(HexPointerScript test)
    {
        if (Environment.GetEnvironmentVariable("HEX_LIVE_PREDICT") is not { Length: > 0 } url)
            return;
        string model = Environment.GetEnvironmentVariable("HEX_LIVE_PREDICT_MODEL") is { Length: > 0 } named ? named : "hf.co/bartowski/zed-industries_zeta-2-GGUF:Q4_K_M";
        string? shots = Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS");
        string folder = Path.Combine(testFolder, "prediction-live");
        string util = Path.Combine(folder, "Util.cs");
        string bank = Path.Combine(folder, "Account.cs");
        const string UTIL = "static class Util\n{\n    public static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;\n\n    public static int Max(int a, int b) => a >\n}\n";
        const string BANK = "namespace Bank;\n\npublic sealed class Account\n{\n    private decimal balance;\n\n    public void Deposit(decimal amount)\n    {\n        if (amount <= 0)\n            throw new ArgumentOutOfRangeException(nameof(amount));\n        balance += amount;\n    }\n\n    public void Withdraw(decimal amount)\n    {\n        if (amount <= 0)\n            throw new ArgumentOutOfRangeException(nameof(amount));\n        \n    }\n}\n";

        (bool On, string Provider, string Url, string Key, string Model, string Format, int Debounce, string[] Languages, string[] Exclude, bool Folder, bool Notice) kept =
            (false, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, 0, [], [], true, false);
        var frames = new List<double>();
        long last = 0;
        int requests = 0;
        double asked = 0;

        void Shot(string name)
        {
            if (shots is not { Length: > 0 })
                return;
            test.Settle(0.3f);
            test.Run(() => Engine.CaptureScreenshot(Path.Combine(shots, name + ".png")));
            test.Wait();
        }


        // The file on screen given new text and the caret put somewhere in it
        void Start(string text, int line, int column)
        {
            test.Run(() =>
            {
                code.Document.Replace(text);
                code.Document.MoveTo(new UITextPosition(line, column));
                code.Focus();
                code.ClearPrediction();
            });
            test.Settle(0.3f);
        }

        // The time from now to ghost text on screen, said with what it is
        void TimedGhost(string what, float limit)
        {
            test.Run(() =>
            {
                asked = Stopwatch.GetTimestamp();
                requests = predictions.Requests;
            });
            test.WaitFor(() => code.HasPrediction, limit);
            test.Run(() =>
            {
                double ms = (Stopwatch.GetTimestamp() - asked) * 1000.0 / Stopwatch.Frequency;
                string said = code.Prediction is { } shown ? string.Join(" | ", shown.Edits.Select(edit => edit.Text.Replace("\n", "\\n"))) : "none";
                Console.WriteLine($"[Hex self-test] prediction-live: {what}: {(code.HasPrediction ? $"{ms:0} ms" : "no suggestion")}, ghost={code.PredictionIsGhost}, {predictions.Requests - requests} sent, status {predictions.Status}: {said}");
            });
        }

        string Line(int line) => code.Document.LineAt(line);

        test.Run(() =>
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(util, UTIL);
            File.WriteAllText(bank, BANK);
            explorer.OpenFolder(folder);
        });
        test.Settle(0.5f);

        test.Run(() =>
        {
            kept = (settings.EditPredictions, settings.EditPredictionsProvider, settings.EditPredictionsApiUrl, settings.EditPredictionsApiKey,
                settings.EditPredictionsModel, settings.EditPredictionsPromptFormat, settings.EditPredictionsDebounce,
                settings.EditPredictionsDisabledLanguages, settings.EditPredictionsExclude, settings.EditPredictionsWorkspace, settings.EditPredictionsNoticeDone);
            settings.EditPredictionsProvider = PredictionProviders.OLLAMA;
            settings.EditPredictionsApiUrl = url;
            settings.EditPredictionsApiKey = string.Empty;
            settings.EditPredictionsModel = model;
            settings.EditPredictionsPromptFormat = "infer";
            settings.EditPredictionsDebounce = 200;
            settings.EditPredictionsDisabledLanguages = HexSettings.UsualEditPredictionsDisabledLanguages;
            settings.EditPredictionsExclude = HexSettings.UsualEditPredictionsExclude;
            settings.EditPredictionsWorkspace = true;
            settings.EditPredictionsNoticeDone = true;
            settings.EditPredictions = false;
            Open(util);
            code.Focus();
            code.CompletionTrigger = _ => false;
            code.CompletionAvailable = () => false;
            // The model out of memory, so what follows pays the load
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                var body = new StringContent("{\"model\":\"" + model + "\",\"keep_alive\":0}", System.Text.Encoding.UTF8, "application/json");
                http.PostAsync(url.TrimEnd('/') + "/api/generate", body).GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[Hex self-test] prediction-live: unloading failed: {exception.Message}");
            }
        });
        test.Settle(0.5f);
        Shot("live-0-off");

        // Switched on: the model is asked to load in the background. The editor's updates are timed for the next seconds
        // while it loads (typing nothing), then a prediction is asked for while it may still be loading
        Start(UTIL, 4, 46);
        test.Run(() =>
        {
            frames.Clear();
            last = 0;
            settings.EditPredictions = true;
        });
        test.Run(() => predictionTickProbe = () =>
        {
            long now = Stopwatch.GetTimestamp();
            if (last != 0)
                frames.Add((now - last) * 1000.0 / Stopwatch.Frequency);
            last = now;
        });
        // Typing in a comment line while the model loads: the updates between the keys are the editor's frame time
        Start("// " + new string('x', 4) + "\n", 0, 7);
        for (int i = 0; i < 80; i++)
        {
            test.Run(() => Engine.Input.Post(Horizon.Input.InputEvent.Typed("a")));
            test.Settle(0.1f);
        }
        test.Run(() => predictionTickProbe = null);
        test.Run(() => Console.WriteLine($"[Hex self-test] prediction-live: frames while the model loads: p50 {Percentile(frames, 0.5):0.0} ms, p95 {Percentile(frames, 0.95):0.0} ms, max {frames.DefaultIfEmpty(0).Max():0.0} ms over {frames.Count}, status {predictions.Status} {predictions.StatusText}"));
        test.Check("prediction-live: no editor update stalled while typing as the model loaded (worst under 100 ms)", () => frames.Count > 100 && frames.Max() < 100.0);

        // End of the file's line: typing " b" after "a >"
        Start(UTIL, 4, 46);
        test.Run(() => code.Document.Type(' '));
        TimedGhost("end of line, C# Max", 30.0f);
        test.Check("prediction-live: ghost text appears", () => code.HasPrediction);
        Shot("live-1-ghost");
        PressKeys(test, "Tab");
        test.Wait();
        test.Run(() => Console.WriteLine($"[Hex self-test] prediction-live: after Tab: {Line(4)}"));
        test.Check("prediction-live: Tab takes it, the line is longer", () => Line(4).Length > "    public static int Max(int a, int b) => a > ".Length && !code.HasPrediction);
        Shot("live-2-accepted");
        test.Run(() => code.Document.Undo());
        test.Check("prediction-live: one undo takes it back", () => Line(4) == "    public static int Max(int a, int b) => a > ");

        // Escape lets go, nothing new is asked until typing
        Start(UTIL, 4, 46);
        test.Run(() => code.Document.Type(' '));
        TimedGhost("again, for Escape", 15.0f);
        test.Run(() => requests = predictions.Requests);
        PressKeys(test, "Escape");
        test.Settle(0.5f);
        test.Check("prediction-live: Escape lets go and nothing is asked", () => !code.HasPrediction && predictions.Requests == requests);

        // Typing through: what it says typed keeps it, shorter
        Start(UTIL, 4, 46);
        test.Run(() => code.Document.Type(' '));
        TimedGhost("again, for typing through", 15.0f);
        string ghost = string.Empty;
        test.Run(() =>
        {
            ghost = code.Prediction is { Edits: [var only] } ? only.Text : string.Empty;
            requests = predictions.Requests;
        });
        {
            test.Run(() =>
            {
                if (ghost.Length > 1)
                    Engine.Input.Post(Horizon.Input.InputEvent.Typed(ghost[0].ToString()));
            });
            test.Settle(0.4f);
            test.Check("prediction-live: typing the first character it says keeps it, no new request", () =>
                ghost.Length < 2 || (code.HasPrediction && predictions.Requests == requests));
        }

        // In the middle of a method: after throw new
        Start(BANK, 17, 8);
        test.Run(() => code.Document.Type('i'));
        TimedGhost("several lines, C# Withdraw", 30.0f);
        Shot("live-3-multiline");
        PressKeys(test, "Tab");
        test.Wait();
        test.Run(() => Console.WriteLine($"[Hex self-test] prediction-live: after Tab the method: {Line(16)} / {Line(17)} / {Line(18)} / {Line(19)}"));
        Shot("live-4-multiline-accepted");

        // Typing on past requests in flight: ten keys 40 ms apart, one result for the last text, none for the others
        Start(UTIL, 4, 46);
        test.Run(() =>
        {
            requests = predictions.Requests;
            asked = predictions.Cancelled;
        });
        foreach (char each in " b ? a ")
        {
            string typed = each.ToString();
            test.Run(() => Engine.Input.Post(Horizon.Input.InputEvent.Typed(typed)));
            test.Settle(0.25f);
        }
        test.WaitFor(() => code.HasPrediction, 30.0f);
        test.Run(() => Console.WriteLine($"[Hex self-test] prediction-live: typing on: {predictions.Requests - requests} sent, {predictions.Cancelled - (int)asked} cancelled, ghost now {(code.Prediction is { } now ? string.Join(" | ", now.Edits.Select(edit => edit.Text.Replace("\n", "\\n"))) : "none")}"));
        Shot("live-5-typing-on");

        // Everything as it was
        test.Run(() =>
        {
            code.ClearPrediction();
            (settings.EditPredictions, settings.EditPredictionsProvider, settings.EditPredictionsApiUrl, settings.EditPredictionsApiKey,
                settings.EditPredictionsModel, settings.EditPredictionsPromptFormat, settings.EditPredictionsDebounce,
                settings.EditPredictionsDisabledLanguages, settings.EditPredictionsExclude, settings.EditPredictionsWorkspace, settings.EditPredictionsNoticeDone) = kept;
            predictions.Forget(document);
            code.Document.Replace(UTIL);
            Save();
            CloseNow();
        });
        test.Wait();
    }
}
