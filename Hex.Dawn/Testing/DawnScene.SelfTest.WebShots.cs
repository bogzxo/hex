namespace Hex.Dawn;

// The page's pictures of the web in Hex (site/images/webdev-*, typescript-*), taken off a small project of a page, its
// sheet and its TypeScript, through the real servers. Only when named, and only with HEX_SELFTEST_SHOTS set is anything
// written; run with --size 1600x900 for the page's size. Each picture waits for what it's of to be on screen.
internal sealed partial class DawnScene
{
    private const string WEB_PAGE = """
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <title>Mixer</title>
          <link rel="stylesheet" href="styles.css">
          <style>
            .deck {
              display: grid;
              grid-template-columns: repeat(auto-fit, minmax(14rem, 1fr));
              gap: 1rem;

            }
          </style>
        </head>
        <body>
          <header class="bar">
            <h1>Mixer</h1>
            <button type="button" class="play">Play</button>
          </header>
          <main class="deck"></main>
          <script type="module" src="app.js"></script>
        </body>
        </html>
        """;

    private const string WEB_SHEET = """
        :root {
          color-scheme: light dark;
          --accent: light-dark(#b85200, #da854d);
        }

        .bar {
          display: flex;
          align-items: center;
          justify-content: space-between;
          padding: 0.75rem 1.25rem;
          border-block-end: 1px solid color-mix(in oklab, currentColor 15%, transparent);
        }

        .play {
          background: var(--accent);
          colr: white;
        }
        """;

    private const string WEB_SCRIPT = """
        export interface Track {
          title: string;
          seconds: number;
          gain?: number;
        }

        export function total(tracks: Track[]): number {
          return tracks.reduce((sum, track) => sum + track.seconds, 0);
        }

        const intro: Track = { title: "Intro", secs: 31 };

        export function loudest(tracks: Track[]): Track | undefined {
          const first = tracks[0];
          return tracks.reduce((best, track) => (track.gain ?? 1) > (best.gain ?? 1) ? track : best, first);
        }

        export function label(track: Track): string {
          return `${track.} (${track.seconds}s)`;
        }
        """;

    private void TestWebShots(HexPointerScript test)
    {
        string folder = Path.Combine(testFolder, "mixer-web");
        string page = Path.Combine(folder, "index.html"), sheet = Path.Combine(folder, "styles.css"), script = Path.Combine(folder, "app.ts");
        test.Run(() =>
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(page, WEB_PAGE.ReplaceLineEndings("\n") + "\n");
            File.WriteAllText(sheet, WEB_SHEET.ReplaceLineEndings("\n") + "\n");
            File.WriteAllText(script, WEB_SCRIPT.ReplaceLineEndings("\n") + "\n");
            File.WriteAllText(Path.Combine(folder, "tsconfig.json"), "{ \"compilerOptions\": { \"target\": \"es2022\", \"module\": \"es2022\", \"strict\": true } }\n");
            explorer.OpenFolder(folder);
            Open(sheet);
        });

        // The sheet with its mistake said, then the page with CSS completing inside its <style>
        test.WaitFor(() => code.Diagnostics.Any(found => found.Message.Contains("Unknown property")), 30.0f);
        test.Run(() => Open(page));
        test.WaitFor(() => languages.LinterOf(document) == "html", 10.0f);
        test.Run(() =>
        {
            code.Focus();
            code.Document.MoveTo(new Horizon.UI.Components.UITextPosition(11, 0));
            // A word with no dash in it yet, the editor narrows by the word and a dash starts another
            code.Document.Insert("      backgr");
            code.Complete(typed: true);
        });
        test.WaitFor(() => code.CompletionOpen && code.Completions.Any(item => item.Text.StartsWith("background", StringComparison.Ordinal)), 30.0f);
        test.Settle(1.0f);
        test.Run(() => Shot("webdev"));
        // The picture's taken as the frame ends, nothing's to change before it is
        test.Settle(0.5f);
        test.Run(() =>
        {
            code.CloseCompletion();
            code.Document.Undo();
        });

        // TypeScript, a property that isn't the type's underlined and a track's members offered after the dot
        test.Run(() => Open(script));
        test.WaitFor(() => code.Diagnostics.Any(found => found.Message.Contains("secs")), 60.0f);
        test.Run(() =>
        {
            code.Focus();
            int line = code.Text.Split('\n').ToList().FindIndex(text => text.Contains("${track.}", StringComparison.Ordinal));
            code.Document.MoveTo(new Horizon.UI.Components.UITextPosition(line, code.Document.LineAt(line).IndexOf("track.}", StringComparison.Ordinal) + 6));
            code.Complete(typed: true);
        });
        test.WaitFor(() => code.CompletionOpen && code.Completions.Any(item => item.Text == "seconds"), 30.0f);
        test.Settle(1.0f);
        test.Run(() => Shot("typescript"));
        test.Settle(0.5f);
        test.Check("webshots: the page's pictures were taken with what they're of on screen", () => code.CompletionOpen);
        test.Run(() =>
        {
            code.CloseCompletion();
            LanguageServers.StopAll();
        });

        TestCompletionShot(test);
    }

    private const string SHOT_MIXER = """
        namespace Signal;

        public sealed class Voice(float frequency, float gain, float pan)
        {
            public float Frequency { get; } = frequency;
            public float Gain { get; } = gain;
            public float Pan { get; } = pan;
            public float Phase;
        }

        public sealed class Mixer(int sampleRate)
        {
            private readonly List<Voice> voices = [];

            public int SampleRate { get; } = sampleRate;

            public float Peak { get; private set; }

            public event Action<float>? Clipped;

            public Voice Add(float frequency, float gain = 0.5f, float pan = 0f)
            {
                var voice = new Voice(frequency, gain, Math.Clamp(pan, -1f, 1f));
                voices.Add(voice);
                return voice;
            }

            public bool Remove(Voice voice) => voices.Remove(voice);

            public float Render(Span<float> buffer)
            {
                buffer.Clear();
                Peak = 0f;
                return Peak;
            }
        }
        """;

    private const string SHOT_PROGRAM = """
        using Signal;

        var mixer = new Mixer(sampleRate: 48_000);
        mixer.Add(frequency: 220f, gain: 0.4f, pan: -0.5f);
        mixer.Add(frequency: 330f, gain: 0.3f, pan: 0.5f);
        mixer.Add(frequency: 440f, gain: 0.2f);

        float[] buffer = new float[mixer.SampleRate / 10 * 2];
        float peak = mixer.Render(buffer);


        Console.WriteLine($"{mixer.SampleRate} Hz");
        Console.WriteLine($"peak {20 * Math.Log10(peak):0.0} dBFS");
        """;

    // The gallery's Completion picture, a C# project like the one it was first taken of, the list after mixer. with each
    // member in its own colour, a method, a property and an event told apart
    private void TestCompletionShot(HexPointerScript test)
    {
        if (HexServerCatalog.Locate("csharp-ls") is null)
        {
            test.Run(() => Console.WriteLine("[Hex self-test] csharp-ls isn't on this machine, the completion picture is skipped"));
            return;
        }

        string folder = Path.Combine(testFolder, "signal"), program = Path.Combine(folder, "Program.cs");
        test.Run(() =>
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "Signal.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <OutputType>Exe</OutputType>\n    <TargetFramework>net11.0</TargetFramework>\n    <ImplicitUsings>enable</ImplicitUsings>\n    <Nullable>enable</Nullable>\n  </PropertyGroup>\n</Project>\n");
            File.WriteAllText(Path.Combine(folder, "Mixer.cs"), SHOT_MIXER.ReplaceLineEndings("\n") + "\n");
            File.WriteAllText(program, SHOT_PROGRAM.ReplaceLineEndings("\n") + "\n");
            explorer.OpenFolder(folder);
            Open(program);
        });

        // csharp-ls loads the project once it's restored, a minute at the most cold
        test.WaitFor(() => languages.LinterOf(document) == "csharp" && !languages.Starting(document), 120.0f);
        test.Run(() =>
        {
            code.Focus();
            code.Document.MoveTo(new Horizon.UI.Components.UITextPosition(9, 0));
            code.Document.Insert("mixer.");
            code.Complete(typed: true);
        });
        test.WaitFor(() => code.CompletionOpen && code.Completions.Any(item => item.Text == "SampleRate") && code.Completions.Any(item => item.Text == "Clipped"), 120.0f);
        test.Settle(2.0f);
        test.Run(() => Shot("completion"));
        test.Settle(0.5f);
        // What csharp-ls said to itself, for a run where the list never came
        test.Run(() =>
        {
            if (!code.CompletionOpen || !code.Completions.Any())
                foreach (LanguageServer server in LanguageServers.Running.Where(each => each.Name == "csharp"))
                    Console.WriteLine($"[Hex self-test] csharp-ls in {server.RootDirectory}, ready {server.IsReady}, failure {server.Failure ?? "none"}, said:\n{server.ErrorOutput}");
        });
        test.Check("webshots: the completion picture has methods, properties and an event", () =>
            code.Completions.Any(item => item.Kind == "method") && code.Completions.Any(item => item.Kind == "property") && code.Completions.Any(item => item.Kind == "event"));
        test.Run(() =>
        {
            code.CloseCompletion();
            LanguageServers.StopAll();
        });
    }
}
