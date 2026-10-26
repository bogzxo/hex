using Key = Silk.NET.Input.Key;

using Horizon.HIDL.Runtime;
using Horizon.UI.Scripting;

namespace Hex.Dawn;

// The self test, the languages the code editor colours. Every one VS Code has a grammar for, and HIDL through its own
// with everything it showed before the grammars came.
internal sealed partial class DawnScene
{
    // A language server of forty lines, for the linting check. It answers initialize and publishes a warning for
    // every TODO in a file as it is opened and changed, an error for every FIXME. The same one the engine's tests use
    private const string FAKE_LANGUAGE_SERVER = """
        import sys, json, re

        def read():
            length = 0
            while True:
                line = sys.stdin.buffer.readline()
                if not line:
                    return None
                if line in (b"\r\n", b"\n"):
                    break
                if line.lower().startswith(b"content-length:"):
                    length = int(line.split(b":")[1].strip())
            return json.loads(sys.stdin.buffer.read(length))

        def send(message):
            body = json.dumps(message).encode("utf-8")
            sys.stdout.buffer.write(b"Content-Length: " + str(len(body)).encode() + b"\r\n\r\n" + body)
            sys.stdout.buffer.flush()

        def check(uri, text):
            found = []
            for number, line in enumerate(text.split("\n")):
                for match in re.finditer(r"TODO|FIXME", line):
                    found.append({"range": {"start": {"line": number, "character": match.start()}, "end": {"line": number, "character": match.end()}},
                                  "severity": 1 if match.group() == "FIXME" else 2, "message": match.group() + " left in"})
            send({"jsonrpc": "2.0", "method": "textDocument/publishDiagnostics", "params": {"uri": uri, "diagnostics": found}})

        while True:
            message = read()
            if message is None:
                break
            method = message.get("method")
            if method == "initialize":
                send({"jsonrpc": "2.0", "id": message["id"], "result": {"capabilities": {"textDocumentSync": 1}}})
            elif method == "shutdown":
                send({"jsonrpc": "2.0", "id": message["id"], "result": None})
            elif method == "exit":
                break
            elif method == "textDocument/didOpen":
                check(message["params"]["textDocument"]["uri"], message["params"]["textDocument"]["text"])
            elif method == "textDocument/didChange":
                check(message["params"]["textDocument"]["uri"], message["params"]["contentChanges"][0]["text"])
        """;

    private void TestLanguages(HexPointerScript test)
    {
        /* A file of another language opens coloured by its grammar, and isn't parsed as HIDL */

        string folder = Path.Combine(testFolder, "selftest_languages");
        HexDocument? before = null;

        (string File, string Text, string Language, (int Line, int Column, SyntaxKind Kind)[] Kinds)[] files =
        [
            ("thing.cs", "using System;\n\n// a comment\npublic class Thing\n{\n    public int Count { get; set; } = 3;\n    private string name = \"hello\";\n}\n", "C#",
                [(0, 0, SyntaxKind.Keyword), (2, 3, SyntaxKind.Comment), (3, 13, SyntaxKind.Type), (5, 37, SyntaxKind.Number), (6, 27, SyntaxKind.Text)]),
            ("make.py", "import os\n\ndef greet(name):\n    # say hello\n    print(len(name), 42)\n    return f\"hi {name}\"\n", "Python",
                [(0, 0, SyntaxKind.Control), (2, 4, SyntaxKind.Call), (3, 4, SyntaxKind.Comment), (4, 4, SyntaxKind.Builtin), (4, 21, SyntaxKind.Number), (5, 13, SyntaxKind.Text)]),
            ("data.json", "{\n    \"name\": \"hex\",\n    \"count\": 3,\n    \"on\": true\n}\n", "JSON",
                [(1, 5, SyntaxKind.Property), (1, 13, SyntaxKind.Text), (2, 13, SyntaxKind.Number), (3, 10, SyntaxKind.Constant)]),
            ("sprites.slang", "#include <common/camera.slang>\n\nstruct Vertex { float4 position : SV_Position; };\n\n// the vertex stage\n", "HLSL",
                [(0, 0, SyntaxKind.Directive), (2, 0, SyntaxKind.Keyword), (2, 16, SyntaxKind.Keyword), (4, 0, SyntaxKind.Comment)]),
            ("notes.md", "# Title\n\nSome *words* and `code` here.\n\n- item\n", "Markdown",
                [(0, 0, SyntaxKind.Declaration), (2, 6, SyntaxKind.Property), (2, 18, SyntaxKind.Text), (4, 0, SyntaxKind.Operator)])
        ];

        test.Run(() =>
        {
            before = document;
            Directory.CreateDirectory(folder);
        });

        foreach (var (file, text, language, kinds) in files)
        {
            string path = Path.Combine(folder, file);
            test.Run(() =>
            {
                System.IO.File.WriteAllText(path, text);
                Open(path);
            });
            test.WaitFor(() => code.KindAt(kinds[0].Line, kinds[0].Column) is not null, 3.0f);
            test.Check($"languages: a {Path.GetExtension(file)} file is coloured as {language} and not parsed as HIDL", () =>
                code.Highlighter?.Name == language && !code.CheckSyntax && code.SyntaxError is null
                && kinds.All(expected => code.KindAt(expected.Line, expected.Column) == expected.Kind));
        }

        /* HIDL goes through its own grammar and has everything it had, the keywords, the texts and their markup, the directives, the swatches and the art */

        string hor = Path.Combine(folder, "everything.hor");
        test.Run(() =>
        {
            System.IO.File.WriteAllText(hor,
                "#define debug\n"
                + "// a comment\n"
                + "let title = \"[b]Hi[/b] $(save) \\n\";\n"
                + "let key = \"@main_menu.quit\";\n"
                + "let card = compositor.panel({ color: vec(0.2, 0.4, 0.9, 1), icon: \"save\", visible: true });\n");
            Open(hor);
        });
        test.WaitFor(() => code.KindAt(4, 83) is not null && code.Swatches.Count == 1 && code.Art.Count == 2, 3.0f);
        test.Check("languages: a .hor is HIDL, parsed, with its directives, keywords, names, texts, markup, escapes and keys coloured as before", () =>
            ReferenceEquals(code.Highlighter, CodeLanguages.Hidl) && code.CheckSyntax && code.SyntaxError is null
            && code.KindAt(0, 0) == SyntaxKind.Directive && code.KindAt(0, 8) == SyntaxKind.Constant
            && code.KindAt(1, 0) == SyntaxKind.Comment
            && code.KindAt(2, 0) == SyntaxKind.Keyword && code.KindAt(2, 4) == SyntaxKind.Declaration
            && code.KindAt(2, 13) == SyntaxKind.Markup && code.KindAt(2, 16) == SyntaxKind.Text && code.KindAt(2, 23) == SyntaxKind.Markup && code.KindAt(2, 31) == SyntaxKind.Escape
            && code.KindAt(3, 10) == SyntaxKind.Reference
            && code.KindAt(4, 11) == SyntaxKind.Builtin && code.KindAt(4, 22) == SyntaxKind.Type && code.KindAt(4, 30) == SyntaxKind.Property
            && code.KindAt(4, 37) == SyntaxKind.Builtin && code.KindAt(4, 83) == SyntaxKind.Constant
            && code.KindAt(4, 27) == SyntaxKind.Bracket1 && code.KindAt(4, 28) == SyntaxKind.Bracket2 && code.KindAt(4, 40) == SyntaxKind.Bracket3);
        test.Check("languages: and its swatch and its art", () =>
            code.Swatches is [{ Line: 4, Column: 37 }] && code.Art is [{ Kind: HidlArtKind.Symbol, Name: "save", Line: 2 }, { Kind: HidlArtKind.Symbol, Name: "save", Line: 4 }]
            && code.ArtBounds(0).Width > 0.0f);

        /* A file of another language is linted by its language server, a pretend one here, written in Python on the spot */

        string server = Path.Combine(folder, "fake_server.py"), linted = Path.Combine(folder, "notes.faketest");
        bool python = LanguageServers.IsAvailable("python");
        test.Run(() =>
        {
            if (!python)
            {
                Console.WriteLine("[Hex self-test] python isn't on this machine, the linting check is skipped");
                return;
            }

            System.IO.File.WriteAllText(server, FAKE_LANGUAGE_SERVER);
            LanguageServers.Register("fake", "python", [server], "fake", [".faketest"]);
            CodeLanguages.Register(".faketest", "text.html.markdown");
            System.IO.File.WriteAllText(linted, "fine\na TODO here\n");
            Open(linted);
        });
        if (python)
        {
            test.WaitFor(() => code.Diagnostics.Count == 1, 10.0f);
            test.Check("linting: a file of another language is opened in its language server and what it says is under the code and in the status bar", () =>
                code.Diagnostics is [{ Line: 1, Column: 2, Length: 4, Warning: true }] && languages.LinterOf(document) == "fake" && languageItem.Text.Contains("fake")
                && problemList.Any(problem => problem.Text.Contains("TODO left in")));
            test.Run(() =>
            {
                code.Document.MoveToEnd(false);
                code.Document.Insert("FIXME too\n");
            });
            test.WaitFor(() => code.Diagnostics.Count == 2, 10.0f);
            test.Check("linting: typing sends the text again a moment later and the new finding comes back", () =>
                code.Diagnostics.Count == 2 && code.Diagnostics.Any(found => !found.Warning && found.Line == 2));
            test.Run(() =>
            {
                code.Document.Undo();
                LanguageServers.StopAll();
            });
        }

        /* The language servers' settings, what language_servers.hor says of each and where it is */

        test.Check("language servers: the file says how each is installed, where its releases are and how it says its version", () =>
            languages.ServerEntry("csharp") is { Command: "csharp-ls", Repo: "razzmatazz/csharp-language-server", Install.Length: > 0, Update: "dotnet tool update -g csharp-ls", VersionArguments: ["--version"] }
            && languages.ServerEntry("markdown") is { Install: "", Update: "", Repo.Length: > 0 } && languages.ServerEntries.Count >= 9
            && languages.ServerEntry("d") is { Command: "serve-d", Repo: "Pure-D/serve-d", VersionArguments: ["--version"] });
        // A server added in the settings, through the form under the servers, for a language with none and for one with
        test.Run(() => OpenSettings());
        test.Settle(0.3f);
        string NameOf(string id) => CodeLanguages.Languages().First(language => language.Id == id).Name;
        test.Check("language servers: one added for a language with none, a bar between its files, is a server of its own and in the list", () =>
        {
            languages.FillServerForm(NameOf("rust"), "rs| .rlib ", "rust-analyzer-selftest");
            languages.PressAddServer();
            return languages.ServerEntry("rust") is { Command: "rust-analyzer-selftest", Extensions: [".rs", ".rlib"] } rust && languages.IsAdded(rust)
                && LanguageServers.RegistrationFor("main.rs") is { Name: "rust" } && settings.AddedServers.Any(server => server.Language == "rust")
                && languages.serverStatus.ContainsKey("rust");
        });
        test.Run(() => languages.FocusServerForm());
        test.Settle(0.5f);
        test.Run(() => Shot("language-servers"));
        test.Settle(0.2f);
        test.Check("language servers: one added for a language the file has a server for is used instead, and taken away the file's is back", () =>
        {
            languages.FillServerForm(NameOf("python"), ".py", "pylsp-selftest");
            languages.PressAddServer();
            bool instead = languages.ServerEntry("python") is { Command: "pylsp-selftest" } && LanguageServers.RegistrationFor("x.py") is { Command: "pylsp-selftest" };
            languages.RemoveServer("python");
            languages.RemoveServer("rust");
            return instead && languages.ServerEntry("python") is { Command: "pyright-langserver" } && languages.ServerEntry("rust") is null
                && LanguageServers.RegistrationFor("x.py") is { Name: "python" } && LanguageServers.RegistrationFor("main.rs") is null
                && settings.AddedServers.Count == 0 && !languages.serverStatus.ContainsKey("rust");
        });
        test.Check("language servers: the files typed are read with a bar between two and a dot on each", () =>
            HexLanguages.ParseExtensions(" d | .di|*.dd||. ") is [".d", ".di", ".dd"]);
        test.Run(CloseSettings);

        test.Check("language servers: versions are found in what a program prints and put in order by their numbers", () =>
            HexServerCatalog.FindVersion("clangd version 19.1.2 (https://github.com/llvm/llvm-project 7ba7d8e)") == "19.1.2"
            && HexServerCatalog.FindVersion("2024-12-18\n") == "2024-12-18" && HexServerCatalog.FindVersion("no version") is null
            && HexServerCatalog.IsNewer("v0.20.0", "0.13.0") && !HexServerCatalog.IsNewer("0.13.0", "v0.13") && HexServerCatalog.Same("v0.13", "0.13.0")
            && HexServerCatalog.IsNewer("2025-01-02", "2024-12-18") && !HexServerCatalog.IsNewer("release", "1.0"));
        test.Check("language servers: what changed is the releases newer than the one on this machine, the newest when none is", () =>
        {
            HexRelease[] releases =
            [
                new("v1.2.0", "1.2", "Two", "", new DateTimeOffset(2025, 3, 1, 0, 0, 0, TimeSpan.Zero)),
                new("v1.1.0", "1.1", "One", "", new DateTimeOffset(2025, 2, 1, 0, 0, 0, TimeSpan.Zero)),
                new("v1.0.0", "1.0", "Zero", "", new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero))
            ];
            string since = HexReleases.Changelog(releases, "1.0.0", UIRichText.Literal), newest = HexReleases.Changelog(releases, "1.2.0", UIRichText.Literal);
            return since.Contains("## 1.2 · 2025-03-01") && since.Contains("One") && !since.Contains("Zero") && newest.Contains("Two") && !newest.Contains("One");
        });

        string nowhere = Path.Combine(folder, "csharp-ls.exe");
        test.Run(() => OpenSettings(HexLanguages.SERVER_SECTION));
        test.Check("language servers: a row a server in the settings, with what was found of each under them", () =>
            SettingsPage?.Enter(HexLanguages.SERVER_KEY + "csharp", new StringValue(nowhere)) == true && languages.serverStatus.ContainsKey("csharp")
            && SettingsPage.Settings.Any(setting => setting.Key == HexLanguages.SERVER_KEY + "markdown" && setting.Buttons is [HexLanguages.CHOOSE, HexLanguages.DOWNLOAD, HexLanguages.CHANGES]));
        test.Check("language servers: where a server is set in the settings is what it is started with, and empty is back to the PATH", () =>
        {
            bool set = settings.ServerPaths["csharp"] == nowhere && LanguageServers.Registered.Any(entry => entry.Name == "csharp" && entry.Command == nowhere);
            SettingsPage!.Reset(HexLanguages.SERVER_KEY + "csharp");
            return set && !settings.ServerPaths.ContainsKey("csharp") && LanguageServers.Registered.Any(entry => entry.Name == "csharp" && entry.Command == "csharp-ls");
        });
        test.Run(CloseSettings);

        TestLanguageModes(test, folder);

        test.Run(() =>
        {
            foreach (HexDocument open in documents.Where(open => open.Path is { } path && path.StartsWith(folder, PlatformPaths.Comparison)).ToList())
            {
                Show(open);
                CloseNow();
            }

            // A server started in the folder has it as its process's, which Windows won't delete from under it
            LanguageServers.StopUnder(folder).Wait(TimeSpan.FromSeconds(6));
            Directory.Delete(folder, recursive: true);
            if (before is not null && documents.Contains(before))
                Show(before);
        });
        test.Wait();
    }

    /// <summary>
    /// Change Language Mode, Ctrl+K M and the status bar listing every language, one picked colouring the file and kept
    /// by its path, Configure File Association writing files_associations, and a backup the language of what it was a
    /// copy of.
    /// </summary>
    private void TestLanguageModes(HexPointerScript test, string folder)
    {
        string weird = Path.Combine(folder, "notes.weird"), file = Path.Combine(folder, "selftest_language_settings.hor");
        HexSettings? was = null;
        ListBox Chooser() => Descendants(textDialog!.Body).OfType<ListBox>().First();
        bool Lists(string row) => textDialog is { IsOpen: true } && Chooser().Items.Contains(row);
        void Pick(Func<string, bool> row)
        {
            ListBox list = Chooser();
            int at = Array.FindIndex(list.Items, text => row(text));
            list.Choose(at);
            list.OnActivated!(list.Items[at]);
        }

        // Settings of the test's own, no folder's file written by a language picked and the user's file left alone
        test.Run(() =>
        {
            was = settings;
            if (File.Exists(file))
                File.Delete(file);
            settings = new HexSettings(file);
            System.IO.File.WriteAllText(weird, "import os\n\ndef greet(name):\n    return 42\n");
            Open(weird);
        });
        test.Wait();
        test.Check("language mode: a file of an extension nobody knows is plain text", () =>
            code.Highlighter is null && languageItem.Text.Contains(Said("status_bar.kind_plain_text")));

        test.Run(() =>
        {
            commands.Keys.Update(pressed => pressed == Key.K, control: true, shift: false, alt: false, typing: false);
            commands.Keys.Update(pressed => pressed == Key.M, control: false, shift: false, alt: false, typing: false);
        });
        test.WaitFor(() => textDialog is { IsOpen: true }, 3.0f);
        test.Check("language mode: Ctrl+K M lists Auto Detect, Plain Text, Configure File Association and every language", () =>
            textDialog is { IsOpen: true } && Chooser().Items is var rows
            && rows[0] == Said("language_mode.auto_detect", ("language", Said("language_mode.plain_text")))
            && rows[1] == Said("language_mode.current", ("row", Said("language_mode.plain_text")))
            && rows.Contains(Said("language_mode.configure", ("extension", ".weird")))
            && CodeLanguages.Languages().All(language => rows.Contains($"{language.Name} ({language.Id})")) && rows.Contains("HIDL (hidl)"));
        test.Run(() => Pick(row => row == "Python (python)"));
        test.WaitFor(() => code.KindAt(2, 0) is not null, 3.0f);
        test.Check("language mode: picking Python colours the file as Python, the status bar says so and the folder's settings keep it", () =>
            code.Highlighter?.Name == "Python" && code.KindAt(2, 0) == SyntaxKind.Keyword && languageItem.Text.Contains("Python")
            && settings.LanguageOverrides.TryGetValue(Path.GetFullPath(weird), out string? kept) && kept == "python"
            && CodeLanguages.OverrideOf(weird) == "python");

        test.Click(() => chrome.ToWorld(statusBar.ItemBounds(languageItem).Center));
        test.WaitFor(() => textDialog is { IsOpen: true }, 3.0f);
        test.Check("language mode: a click on the language in the status bar opens the same list, Python marked", () =>
            Lists(Said("language_mode.current", ("row", "Python (python)"))) && Lists(Said("language_mode.plain_text")));
        string autoDetect = Said("language_mode.auto_detect", ("language", "|"));
        test.Run(() => Pick(row => row.StartsWith(autoDetect[..autoDetect.IndexOf('|')], StringComparison.Ordinal)));
        test.Wait();
        test.Check("language mode: Auto Detect lets it go, plain text again and nothing kept", () =>
            code.Highlighter is null && CodeLanguages.OverrideOf(weird) is null && settings.LanguageOverrides.Count == 0);

        // Configure File Association, every .weird Python, in the user's settings and after a restart
        test.Run(ChangeLanguageMode);
        test.WaitFor(() => textDialog is { IsOpen: true }, 3.0f);
        test.Run(() => Pick(row => row == Said("language_mode.configure", ("extension", ".weird"))));
        test.WaitFor(() => Lists("Python (python)"), 3.0f);
        test.Run(() => Pick(row => row == "Python (python)"));
        test.Wait();
        test.Check("language mode: Configure File Association writes files_associations to the user's settings and colours the file", () =>
            File.ReadAllText(file) is var text && text.Contains("files_associations", StringComparison.Ordinal)
            && text.Contains("\"*.weird\": \"python\"", StringComparison.Ordinal)
            && code.Highlighter?.Name == "Python" && settings.FileAssociations["*.weird"] == "python");
        test.Run(() =>
        {
            CloseNow();
            CodeLanguages.SetAssociations([]);
            settings = new HexSettings(file);
        });
        test.Wait();
        test.Run(() => Open(weird));
        test.Wait();
        test.Check("language mode: and read again as Hex starts, the file opens as Python", () =>
            settings.FileAssociations.GetValueOrDefault("*.weird") == "python" && code.Highlighter?.Name == "Python" && languageItem.Text.Contains("Python"));

        // A file set to a language is kept by the folder's own settings, by its path in the folder
        string workspace = Path.Combine(folder, "language_workspace"), inside = Path.Combine(workspace, "src", "tool.weird");
        test.Check("language mode: a language picked is kept in the folder's file by its path there, and read back", () =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(inside)!);
            var kept = new HexSettings(null);
            kept.LoadWorkspace(workspace);
            kept.SetLanguageOverride(inside, "rust");
            bool written = File.ReadAllText(HexSettings.WorkspacePathOf(workspace)).Contains("\"src/tool.weird\": \"rust\"", StringComparison.Ordinal);
            var read = new HexSettings(null);
            read.LoadWorkspace(workspace);
            bool back = read.LanguageOverrides.GetValueOrDefault(Path.GetFullPath(inside)) == "rust";
            read.LoadWorkspace(null);
            return written && back && read.LanguageOverrides.Count == 0;
        });

        // Backups, the inner extension decides, and a .hor set to plain text says so
        string orig = Path.Combine(folder, "Foo.cs.orig"), menu = Path.Combine(folder, "menu.hor.orig"), plain = Path.Combine(folder, "plain.hor");
        test.Run(() =>
        {
            System.IO.File.WriteAllText(orig, "public class Foo { }\n");
            System.IO.File.WriteAllText(menu, "let title = \"menu\";\n");
            System.IO.File.WriteAllText(plain, "let title = \"plain\";\n");
            Open(orig);
        });
        test.Wait();
        test.Check("language mode: Foo.cs.orig is C#, with the C# icon in the explorer and its tab", () =>
            code.Highlighter?.Name == "C#" && HexExplorer.FileIcon(orig) == HexExplorer.FileIcon("Foo.cs") && HexExplorer.FileIcon(orig) != "file"
            && explorer.FileIconColor(orig) == explorer.FileIconColor("Foo.cs") && IconOfDocument(document) == HexExplorer.FileIcon("Foo.cs"));
        test.Run(() => Open(menu));
        test.Wait();
        test.Check("language mode: menu.hor.orig is HIDL in the status bar", () =>
            ReferenceEquals(code.Highlighter, CodeLanguages.Hidl) && languageItem.Text.EndsWith(" HIDL", StringComparison.Ordinal));
        test.Run(() => Open(plain));
        test.Wait();
        test.Run(() => PickLanguage(document, CodeLanguages.PLAIN_TEXT));
        test.Wait();
        test.Check("language mode: a .hor set to plain text is Plain text in the status bar and not parsed", () =>
            code.Highlighter is null && !code.CheckSyntax && languageItem.Text.Contains(Said("status_bar.kind_plain_text")));
        test.Run(() =>
        {
            PickLanguage(document, null);
            settings = was!;
        });
        test.Wait();
        test.Check("language mode: the settings back, the association goes with them", () =>
            CodeLanguages.Detect(weird) is null && ReferenceEquals(CodeLanguages.ForFile(plain), CodeLanguages.Hidl));
    }
}
