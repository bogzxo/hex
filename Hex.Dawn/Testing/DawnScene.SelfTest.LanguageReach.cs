using Horizon.HIDL.Runtime;
using Horizon.UI.Scripting;

namespace Hex.Dawn;

// The self test, the languages with no grammar of their own coloured as the nearest one there is (Svelte as HTML, TOML
// as an ini, a .rej as a diff), and a server that isn't on the machine said once with how it's installed.
internal sealed partial class DawnScene
{
    private void TestLanguageReach(HexPointerScript test, string folder)
    {
        (string File, string Text, string Language)[] nearest =
        [
            ("App.svelte", "<script>\n  let count = 0;\n</script>\n\n<button on:click={() => count++}>{count}</button>\n", "html"),
            ("Cargo.toml", "[package]\nname = \"thing\"\nversion = \"0.1.0\"\n", "ini"),
            ("x.rej", "--- a/x.cs\n+++ b/x.cs\n@@ -1,2 +1,2 @@\n-old\n+new\n", "diff")
        ];

        foreach (var (file, text, language) in nearest)
        {
            string path = Path.Combine(folder, file);
            test.Run(() =>
            {
                System.IO.File.WriteAllText(path, text);
                Open(path);
            });
            test.Wait();
            test.Check($"languages: {file} is coloured as {language}", () =>
                code.Highlighter is not null && ReferenceEquals(code.Highlighter, CodeLanguages.ForLanguageId(language)) && !code.CheckSyntax);
        }

        test.Check("languages: x.tf.bak has its suffix off and is what x.tf is", () =>
            CodeLanguages.WithoutBackupSuffix("x.tf.bak") == "x.tf" && ReferenceEquals(CodeLanguages.ForFile("x.tf.bak"), CodeLanguages.ForFile("x.tf"))
            && CodeLanguages.WithoutBackupSuffix("x.rej") == "x.rej");

        test.Check("language servers: the file has gopls, rust-analyzer, bash-language-server, taplo, docker-langserver and svelteserver, and TypeScript's has its modules", () =>
            languages.ServerEntry("go") is { Command: "gopls", Install: "go install golang.org/x/tools/gopls@latest", Extensions: [".go"] }
            && languages.ServerEntry("rust") is { Command: "rust-analyzer", Install: "rustup component add rust-analyzer" }
            && languages.ServerEntry("bash") is { Command: "bash-language-server", Extensions: [".sh", ".bash"] }
            && languages.ServerEntry("toml") is { Command: "taplo", Arguments: ["lsp", "stdio"], Extensions: [".toml"] }
            && languages.ServerEntry("docker") is { Command: "docker-langserver" } && languages.ServerEntry("svelte") is { Command: "svelteserver", Extensions: [".svelte"] }
            && languages.ServerEntry("typescript") is { } typescript && new[] { ".mjs", ".cjs", ".mts", ".cts" }.All(typescript.Extensions.Contains));

        /* gopls not on the machine (set to a path that isn't there), one card with how it's installed and no second */

        string nowhere = Path.Combine(folder, "nowhere", OperatingSystem.IsWindows() ? "gopls.exe" : "gopls");
        string first = Path.Combine(folder, "a.go"), second = Path.Combine(folder, "b.go");
        int saidBefore = 0;
        test.Run(() => OpenSettings(HexLanguages.SERVER_SECTION));
        test.Run(() =>
        {
            SettingsPage?.Enter(HexLanguages.SERVER_KEY + "go", new StringValue(nowhere));
            CloseSettings();
            languages.SayMissing = true;
            saidBefore = languages.MissingNotices.Count;
            System.IO.File.WriteAllText(first, "package main\n\nfunc main() {}\n");
            System.IO.File.WriteAllText(second, "package main\n\nfunc other() {}\n");
            Open(first);
        });
        test.Wait();
        test.Check("language servers: a .go with no gopls says so once, with the command that installs it and a button to copy it", () =>
            languages.MissingNotices.Count == saidBefore + 1 && languages.MissingNotices[^1] == Said("code_server.missing", ("command", "gopls"), ("install", "go install golang.org/x/tools/gopls@latest"))
            && toasts.Cards.Any(card => card.Text.Contains("go install golang.org/x/tools/gopls@latest", StringComparison.Ordinal)
                && card.Actions.Any(action => action.Label == Said("code_server.copy_install")) && card.Actions.Any(action => action.Label == Said("code_server.open_settings"))));
        test.Run(() => Open(second));
        test.Wait();
        test.Check("language servers: and a second .go says nothing more", () => languages.MissingNotices.Count == saidBefore + 1);
        test.Run(() =>
        {
            foreach (var card in toasts.Cards.Where(card => card.Text.Contains("gopls", StringComparison.Ordinal)))
                card.Close();

            languages.SayMissing = false;
            OpenSettings(HexLanguages.SERVER_SECTION);
        });
        test.Run(() =>
        {
            SettingsPage?.Reset(HexLanguages.SERVER_KEY + "go");
            CloseSettings();
        });
        test.Wait();
    }
}
