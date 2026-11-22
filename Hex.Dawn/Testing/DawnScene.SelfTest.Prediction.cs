using System.Diagnostics;

using Hex.Core;

using Horizon.UI.Components;

namespace Hex.Dawn;

// The self test of edit prediction, against a model server of its own on a loopback port (HexFakePredictionServer), so
// it needs no model and no network. Typing in a C# file shows ghost text, Tab takes it as one undo step, typing what it
// says keeps it without asking again, typing on while a request stalls closes that request, a diff away from the caret is
// taken with Tab, Ctrl+Right takes a word and Escape lets go. A language switched off and an .env file send nothing, a
// key goes as a bearer and is said nowhere, a dead port, a refused key and a missing model are said in the status bar,
// and the frame time while predicting is held against typing with predictions off. The settings it changes are this
// run's own and put back at the end.
internal sealed partial class DawnScene
{
    // How much slower a tick may be while predicting than with predictions off, at the 95th percentile
    private const double PREDICTION_TICK_SLACK_MS = 4.0;

    private void TestPrediction(HexPointerScript test)
    {
        string folder = Path.Combine(testFolder, "prediction");
        string greeter = Path.Combine(folder, "Greeter.cs");
        string counter = Path.Combine(folder, "Counter.cs");
        string env = Path.Combine(folder, ".env");
        const string GREETER = "class Greeter\n{\n    void Hello()\n    {\n        System.Console\n    }\n\n    void Bye()\n    {\n        \n    }\n}\n";
        const string COUNTER = "class Counter\n{\n    int Add(int total, int more)\n    {\n        return total + more;\n    }\n}\n";
        const string KEY = "sk-test-not-a-key";

        HexFakePredictionServer server = null!;
        (bool On, string Provider, string Url, string Key, string Model, string Format, int Debounce, string[] Languages, string[] Exclude, bool Folder, bool Notice) kept =
            (false, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, 0, [], [], true, false);
        int requests = 0, taken = 0, notices = 0, cancelled = 0, stalled = 0;
        string before = string.Empty;

        // Text typed the way the keyboard types it, through the editor, a few updates for each character
        void Type(string text)
        {
            foreach (char each in text)
            {
                string typed = each.ToString();
                test.Run(() => Engine.Input.Post(Horizon.Input.InputEvent.Typed(typed)));
                test.Wait();
            }
        }

        // The file on screen given new text (which no request is made for) and the caret put somewhere in it
        void Start(string text, int line, int column, HexFakePredictionServer.Reply reply)
        {
            test.Run(() =>
            {
                server.Next = new HexFakePredictionServer.Reply(HexFakePredictionServer.Kind.Answer, _ => string.Empty);
                code.Document.Replace(text);
                code.Document.MoveTo(new UITextPosition(line, column));
                code.Focus();
            });
            test.Settle(0.3f);
            test.Run(() =>
            {
                code.ClearPrediction();
                server.Next = reply;
                requests = server.Requests;
            });
        }

        // The completion list kept shut, it has Tab while it's open and nothing is predicted under it
        void QuietCompletion()
        {
            code.CompletionTrigger = _ => false;
            code.CompletionAvailable = () => false;
        }

        HexFakePredictionServer.Reply Answer(string text) => new(HexFakePredictionServer.Kind.Answer, _ => text);
        string Line(int line) => code.Document.LineAt(line);
        string? Ghost() => code.Prediction is { Edits: [var only] } ? only.Text : null;
        bool ItemSays(string key) => predictionItem.Text.EndsWith(Said(key, ("language", "C#")), StringComparison.Ordinal);

        test.Run(() =>
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(greeter, GREETER);
            File.WriteAllText(counter, COUNTER);
            File.WriteAllText(env, "TOKEN=abc\n");
            explorer.OpenFolder(folder);
        });
        test.Settle(0.5f);

        // This run's settings, kept to be put back, then pointed at the fake server. The notice is to be given once
        test.Run(() =>
        {
            server = new HexFakePredictionServer();
            kept = (settings.EditPredictions, settings.EditPredictionsProvider, settings.EditPredictionsApiUrl, settings.EditPredictionsApiKey,
                settings.EditPredictionsModel, settings.EditPredictionsPromptFormat, settings.EditPredictionsDebounce,
                settings.EditPredictionsDisabledLanguages, settings.EditPredictionsExclude, settings.EditPredictionsWorkspace, settings.EditPredictionsNoticeDone);
            settings.EditPredictionsProvider = PredictionProviders.OLLAMA;
            settings.EditPredictionsApiUrl = server.Url;
            settings.EditPredictionsApiKey = string.Empty;
            settings.EditPredictionsModel = "fake-coder";
            settings.EditPredictionsPromptFormat = "qwen";
            settings.EditPredictionsDebounce = 60;
            settings.EditPredictionsDisabledLanguages = HexSettings.UsualEditPredictionsDisabledLanguages;
            settings.EditPredictionsExclude = HexSettings.UsualEditPredictionsExclude;
            settings.EditPredictionsWorkspace = true;
            settings.EditPredictionsNoticeDone = false;
            settings.EditPredictions = false;
            notices = predictionNotices;
            Open(greeter);
            code.Focus();
            QuietCompletion();
        });
        test.Settle(0.5f);
        test.Check("prediction: a disabled editor has no prediction engine, editor callbacks or status item", () =>
            predictions is null && code.OnPredictionEnded is null && (predictionItem is null || predictionItem.IsVisible?.Invoke() == false));

        test.Run(TogglePredictions);
        test.Wait();
        test.Check("prediction: switched on, a notice says what is sent and where, once", () =>
            settings.EditPredictions && predictionNotices == notices + 1 && settings.EditPredictionsNoticeDone);
        test.Run(TogglePredictions);
        test.Run(TogglePredictions);
        test.Wait();
        test.Check("prediction: switched off and on again, no second notice", () => settings.EditPredictions && predictionNotices == notices + 1);

        // Ghost text after the caret, taken with Tab as one step to undo
        Start(GREETER, 4, 22, Answer("WriteLine(\"hi\");"));
        Type(".");
        test.WaitFor(() => code.HasPrediction, 10.0f);
        test.Run(() => Console.WriteLine($"[Hex self-test] prediction: ghost {Ghost()} is ghost {code.PredictionIsGhost}, {server.Requests - requests} asked, {server.Accepted} accepted, {server.Requests} in all, {predictions.Requests} sent, {predictions.Status} {predictions.StatusText}, popup {code.CompletionOpen} sig {code.Signature is not null}"));
        test.Check("prediction: typing in a C# file shows ghost text from the server", () =>
            code.PredictionIsGhost && Ghost() == "WriteLine(\"hi\");" && server.Requests == requests + 1);
        test.Check("prediction: the prompt went to Ollama's generate with the file's path", () =>
            server.LastPath == "/api/generate" && server.LastPrompt.Contains("Greeter.cs", StringComparison.Ordinal));
        test.Check("prediction: the status item says predictions are on", () => ItemSays("prediction.item_on"));

        Type("Wr");
        test.Settle(0.3f);
        test.Check("prediction: typing what it says keeps it, shorter, with no new request", () =>
            Ghost() == "iteLine(\"hi\");" && server.Requests == requests + 1);

        test.Run(() =>
        {
            server.Next = Answer(string.Empty);
            taken = predictionsTaken;
        });
        PressKeys(test, "Tab");
        test.Run(() => Console.WriteLine($"[Hex self-test] prediction: after Tab line {Line(4)} taken {predictionsTaken - taken} focused {code.IsFocused} ghost {Ghost()} focus {compositor.Focus?.GetType().Name}"));
        test.Check("prediction: Tab takes it", () => Line(4) == "        System.Console.WriteLine(\"hi\");" && predictionsTaken == taken + 1);
        test.Run(() => code.Document.Undo());
        test.Check("prediction: one undo takes it back", () => Line(4) == "        System.Console.Wr");

        // A request that stalls: the status says it's waiting, typing on closes it and no stale ghost comes
        Start(GREETER.Replace("Greeter", "Stalled"), 4, 22, new HexFakePredictionServer.Reply(HexFakePredictionServer.Kind.Stall));
        test.Run(() =>
        {
            cancelled = predictions.Cancelled;
            stalled = server.Stalled;
        });
        Type(".");
        test.WaitFor(() => server.Stalled > stalled, 10.0f);
        test.Settle(0.5f);
        test.Check("prediction: while a request is answered slowly the status item says it's waiting", () => ItemSays("prediction.item_waiting"));
        test.Run(() => server.Next = Answer("ine();"));
        Type("WriteL");
        test.WaitFor(() => server.Gone > 0 && code.HasPrediction, 10.0f);
        test.Check("prediction: typing on cancels the stalled request, the server sees it go", () =>
            server.Gone > 0 && predictions.Cancelled > cancelled);
        test.Check("prediction: and what's shown is the answer for the text as it is", () => Ghost() == "ine();" && Line(4) == "        System.Console.WriteL");

        // More than one line of ghost text, taken with Alt+L
        Start(GREETER.Replace("Greeter", "Lines"), 9, 8, Answer("f (true)\n        {\n        }"));
        Type("i");
        test.WaitFor(() => code.HasPrediction, 10.0f);
        test.Check("prediction: an answer of several lines is ghost text over them", () => code.PredictionIsGhost && Ghost() == "f (true)\n        {\n        }");
        test.Run(() => server.Next = Answer(string.Empty));
        PressKeys(test, "Alt+L");
        test.Check("prediction: Alt+L takes it", () => Line(9) == "        if (true)" && Line(10) == "        {" && Line(11) == "        }");

        // The platform's next-word shortcut takes a word, then Escape lets go of the rest.
        Start(GREETER.Replace("Greeter", "Words"), 4, 22, Answer("Out.WriteLine(1);"));
        Type(".");
        test.WaitFor(() => code.HasPrediction, 10.0f);
        PressKeys(test, OperatingSystem.IsMacOS() ? "Alt+Right" : "Ctrl+Right");
        test.Check("prediction: the next-word shortcut takes the next word", () => Line(4) == "        System.Console.Out" && Ghost() == ".WriteLine(1);");
        test.Run(() => requests = server.Requests);
        PressKeys(test, "Escape");
        test.Settle(0.4f);
        test.Check("prediction: Escape lets go of it, and nothing is asked until something's typed", () => !code.HasPrediction && server.Requests == requests);

        // A diff away from the caret, Zeta 2's next edit: the other uses of what was renamed
        test.Run(() =>
        {
            settings.EditPredictionsPromptFormat = "zeta2";
            Open(counter);
            code.Focus();
            QuietCompletion();
        });
        test.Settle(0.3f);
        Start(COUNTER, 4, 28, new HexFakePredictionServer.Reply(HexFakePredictionServer.Kind.Answer,
            HexFakePredictionServer.Zeta2(region => region.Replace("total", "sum", StringComparison.Ordinal))));
        Type(" ");
        test.WaitFor(() => code.HasPrediction, 10.0f);
        test.Check("prediction: an edit away from the caret is a diff, not ghost text", () => code.HasPrediction && !code.PredictionIsGhost);
        test.Run(() =>
        {
            server.Next = Answer(string.Empty);
            before = code.Text;
        });
        PressKeys(test, "Tab");
        test.Check("prediction: Tab takes the diff", () => Line(2) == "    int Add(int sum, int more)" && Line(4).StartsWith("        return sum + more;", StringComparison.Ordinal));
        test.Run(() => code.Document.Undo());
        test.Check("prediction: one undo takes the whole diff back", () => code.Text == before);
        test.Run(() =>
        {
            settings.EditPredictionsPromptFormat = "qwen";
            code.Document.Replace(COUNTER);
            Save();
            CloseNow();
            Open(greeter);
            code.Focus();
            QuietCompletion();
        });
        test.Settle(0.3f);

        // The language switched off by its command: nothing asked, and the item says so
        test.Run(TogglePredictionsForLanguage);
        Start(GREETER.Replace("Greeter", "Quiet"), 4, 22, Answer("Beep();"));
        Type(".");
        test.Settle(0.4f);
        test.Check("prediction: with the language switched off nothing is asked", () =>
            settings.EditPredictionsDisabledLanguages.Contains("csharp") && server.Requests == requests && !code.HasPrediction);
        test.Check("prediction: the status item says it's off for C#", () => ItemSays("prediction.item_language_off"));
        test.Run(TogglePredictionsForLanguage);
        test.Wait();
        test.Check("prediction: and on again by the same command", () => !settings.EditPredictionsDisabledLanguages.Contains("csharp"));

        // The other states the item has
        test.Run(() => settings.EditPredictionsWorkspace = false);
        test.Wait();
        test.Check("prediction: switched off by the folder, the item says so", () => ItemSays("prediction.item_folder_off"));
        test.Run(() =>
        {
            settings.EditPredictionsWorkspace = true;
            settings.EditPredictionsModel = string.Empty;
        });
        test.Wait();
        test.Check("prediction: with no model the item says predictions aren't set up", () => ItemSays("prediction.item_not_set_up"));
        test.Run(() => settings.EditPredictionsModel = "fake-coder");

        // An .env file is never sent, whatever its language
        test.Run(() =>
        {
            settings.EditPredictionsDisabledLanguages = [];
            Open(env);
            code.Focus();
            QuietCompletion();
            code.Document.MoveTo(new UITextPosition(0, 9));
            requests = server.Requests;
            server.Next = Answer("def");
        });
        test.Settle(0.3f);
        Type("x");
        test.Settle(0.4f);
        test.Check("prediction: nothing of an .env file is sent", () => server.Requests == requests && !code.HasPrediction);
        test.Check("prediction: the status item says it's off for the file", () => ItemSays("prediction.item_file_off"));
        test.Run(() =>
        {
            settings.EditPredictionsDisabledLanguages = HexSettings.UsualEditPredictionsDisabledLanguages;
            code.Document.Replace("TOKEN=abc\n");
            Save();
            CloseNow();
            Open(greeter);
            code.Focus();
            QuietCompletion();
        });
        test.Settle(0.3f);

        // An OpenAI shaped server with a key: sent as a bearer, said nowhere
        test.Run(() =>
        {
            settings.EditPredictionsProvider = PredictionProviders.OPENAI;
            settings.EditPredictionsApiUrl = server.Url + "/v1";
            settings.EditPredictionsApiKey = KEY;
        });
        Start(GREETER.Replace("Greeter", "Keyed"), 4, 22, Answer("Error.WriteLine(2);"));
        Type(".");
        test.WaitFor(() => code.HasPrediction, 10.0f);
        test.Check("prediction: an OpenAI server is sent the key as a bearer, at its completions", () =>
            Ghost() == "Error.WriteLine(2);" && server.LastAuthorization == "Bearer " + KEY && server.LastPath == "/v1/completions");
        test.Check("prediction: the key is in no status, tooltip or prompt", () =>
            !predictionItem.Text.Contains(KEY, StringComparison.Ordinal) && !predictionItem.Tooltip.Contains(KEY, StringComparison.Ordinal)
            && !predictions.StatusText.Contains(KEY, StringComparison.Ordinal) && !server.LastPrompt.Contains(KEY, StringComparison.Ordinal));
        test.Run(() =>
        {
            settings.EditPredictionsProvider = PredictionProviders.OLLAMA;
            settings.EditPredictionsApiUrl = server.Url;
            settings.EditPredictionsApiKey = string.Empty;
        });

        TestPredictionFrameTime(test, () => server, () => Start(GREETER.Replace("Greeter", "Timed"), 4, 22, Answer("Flush();")));

        // What goes wrong said in words, and the editor typing on all the same
        test.Run(() => settings.EditPredictionsApiUrl = $"http://127.0.0.1:{HexFakePredictionServer.DeadPort()}");
        Start(GREETER.Replace("Greeter", "Dead"), 4, 22, Answer(string.Empty));
        Type(".");
        test.WaitFor(() => predictions.Status == PredictionStatus.Unreachable, 15.0f);
        test.Wait();
        test.Check("prediction: a port nothing listens on says it can't reach the server", () =>
        {
            bool unreachable = ItemSays("prediction.item_unreachable");
            if (!unreachable)
                Console.WriteLine($"[Hex self-test] prediction: dead port yielded {predictions.Status} ({predictions.StatusText}); item: {predictionItem.Text}");
            return unreachable;
        });
        Type("Out");
        test.Check("prediction: and the editor types on", () => Line(4) == "        System.Console.Out");

        test.Run(() => settings.EditPredictionsApiUrl = server.Url);
        Start(GREETER.Replace("Greeter", "Refused"), 4, 22, new HexFakePredictionServer.Reply(HexFakePredictionServer.Kind.Unauthorized));
        Type(".");
        test.WaitFor(() => predictions.Status == PredictionStatus.Unauthorized, 10.0f);
        test.Wait();
        test.Check("prediction: a 401 says the key was refused", () => ItemSays("prediction.item_unauthorized"));

        Start(GREETER.Replace("Greeter", "Missing"), 4, 22, new HexFakePredictionServer.Reply(HexFakePredictionServer.Kind.NoSuchModel));
        Type(".");
        test.WaitFor(() => predictions.Status == PredictionStatus.NoSuchModel, 10.0f);
        test.Wait();
        test.Check("prediction: a 404 says there's no such model", () => ItemSays("prediction.item_no_model"));

        // Predict Now (Alt+\) asks at once, with a debounce that would otherwise wait two seconds
        Start(GREETER.Replace("Greeter", "Garbled"), 4, 22, new HexFakePredictionServer.Reply(HexFakePredictionServer.Kind.Garbage));
        test.Run(() => settings.EditPredictionsDebounce = 2000);
        Type(".");
        PressKeys(test, "Alt+\\");
        test.WaitFor(() => predictions.Status == PredictionStatus.BadAnswer, 1.5f);
        test.Check("prediction: Predict Now asks at once, and an answer that isn't JSON is said", () =>
            server.Requests == requests + 1 && ItemSays("prediction.item_bad_answer"));
        test.Run(() => settings.EditPredictionsDebounce = 60);
        Type("Out");
        test.Check("prediction: the editor still types fine", () => Line(4) == "        System.Console.Out");

        // Everything as it was
        test.Run(() =>
        {
            code.ClearPrediction();
            (settings.EditPredictions, settings.EditPredictionsProvider, settings.EditPredictionsApiUrl, settings.EditPredictionsApiKey,
                settings.EditPredictionsModel, settings.EditPredictionsPromptFormat, settings.EditPredictionsDebounce,
                settings.EditPredictionsDisabledLanguages, settings.EditPredictionsExclude, settings.EditPredictionsWorkspace, settings.EditPredictionsNoticeDone) = kept;
            predictions.Forget(document);
            code.Document.Replace(GREETER);
            Save();
            CloseNow();
            server.Dispose();
        });
        test.Wait();
    }

    // Ticks timed while typing with predictions off and then on, a prediction asked and shown after every word, the 95th
    // percentile of each said and the one on held to the one off with a little slack
    private void TestPredictionFrameTime(HexPointerScript test, Func<HexFakePredictionServer> server, Action start)
    {
        var off = new List<double>();
        var on = new List<double>();
        int shown = 0;
        long last = 0;
        int collections = 0;
        var slow = new List<string>();

        void Typing(List<double> into, string what)
        {
            string[] words = ["Write", "Line", "Out", "Error", "Flush", "Read", "Key", "Beep", "Clear", "Title"];
            foreach (string word in words)
            {
                test.Run(() => last = 0);
                foreach (char each in word)
                {
                    char key = each;
                    test.Run(() =>
                    {
                        long now = Stopwatch.GetTimestamp();
                        int gcs = GC.CollectionCount(0);
                        if (last != 0)
                        {
                            double ms = (now - last) * 1000.0 / Stopwatch.Frequency;
                            into.Add(ms);
                            if (ms > 8.0)
                                slow.Add($"{what} {ms:0.0} ms gc {gcs - collections} shown {code.HasPrediction}");
                        }
                        last = now;
                        collections = gcs;
                        code.Document.Type(key);
                    });
                }
                test.Settle(0.25f);
            }
        }

        start();
        test.Run(() => settings.EditPredictions = false);
        Typing(off, "off");
        test.Run(() =>
        {
            settings.EditPredictions = true;
            shown = predictionsShown;
        });
        start();
        Typing(on, "on");
        test.Run(() =>
        {
            foreach (string each in slow)
                Console.WriteLine($"[Hex self-test] prediction slow tick: {each}");
            double offP95 = Percentile(off, 0.95), onP95 = Percentile(on, 0.95);
            Console.WriteLine($"[Hex self-test] prediction frame time: off p95 {offP95:0.0} ms, on p95 {onP95:0.0} ms over {on.Count} keys, {predictionsShown - shown} shown, {server().Requests} asked in all");
        });
        test.Check($"prediction: ticks while predicting are no more than {PREDICTION_TICK_SLACK_MS} ms slower than with predictions off (p95)", () =>
            on.Count > 10 && off.Count > 10 && predictionsShown > shown
            && Percentile(on, 0.95) <= Math.Max(Percentile(off, 0.95) + PREDICTION_TICK_SLACK_MS, Percentile(off, 0.95) * 1.5));
    }

    private static double Percentile(List<double> values, double at)
    {
        if (values.Count == 0)
            return 0;
        var sorted = values.Order().ToList();
        return sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * at))];
    }
}
