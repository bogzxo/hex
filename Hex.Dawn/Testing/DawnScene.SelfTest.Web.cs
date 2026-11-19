using Horizon.UI.Scripting;

namespace Hex.Dawn;

// The self test of the web through its real servers (TypeScript 7's tsc --lsp, and vscode-langservers-extracted's HTML
// and CSS ones, see language_servers.hor), each part left out when its server isn't on this machine. A file goes to one
// server by its extension and is opened with the language id that server reads it by: a .js is JavaScript to the
// TypeScript server (a type written in one is an error a .ts wouldn't have), a .scss is SCSS to the CSS server. A page's
// own <style> is the HTML server's, CSS completes in it, and none of it is said twice.
internal sealed partial class DawnScene
{
    private void TestWeb(HexPointerScript test)
    {
        string folder = Path.Combine(testFolder, "selftest_web");
        // TypeScript 7's tsc is the server, an older one on the PATH has no --lsp and the part is left out for it
        bool typescript = HexServerCatalog.Locate("tsc") is { } tsc && (HexServerCatalog.VersionOf(tsc, ["--version"]) is { } version && !HexServerCatalog.IsNewer("7", version));
        bool css = HexServerCatalog.Locate("vscode-css-language-server") is not null;
        bool html = HexServerCatalog.Locate("vscode-html-language-server") is not null;
        test.Run(() =>
        {
            Directory.CreateDirectory(folder);
            if (!typescript) Console.WriteLine("[Hex self-test] TypeScript 7's tsc isn't on this machine, TypeScript and JavaScript are skipped");
            if (!css) Console.WriteLine("[Hex self-test] vscode-css-language-server isn't on this machine, CSS is skipped");
            if (!html) Console.WriteLine("[Hex self-test] vscode-html-language-server isn't on this machine, HTML is skipped");
        });

        if (typescript)
        {
            string ts = Path.Combine(folder, "app.ts"), js = Path.Combine(folder, "app.js");
            test.Run(() =>
            {
                File.WriteAllText(ts, "const count: number = \"three\";\nexport { count };\n");
                Open(ts);
            });
            test.WaitFor(() => languages.LinterOf(document) is not null, 10.0f);
            test.Check("web: a .ts file is opened in the TypeScript server", () => languages.LinterOf(document) == "typescript");
            test.WaitFor(() => code.Diagnostics.Any(found => found.Message.Contains("not assignable")), 60.0f);
            test.Check("web: and a type that's wrong is underlined", () => code.Diagnostics.Any(found => found.Line == 0 && !found.Warning && found.Message.Contains("not assignable")));
            test.Run(CloseNow);

            test.Run(() =>
            {
                File.WriteAllText(js, "let width: number = 3;\nexport { width };\n");
                Open(js);
            });
            test.WaitFor(() => languages.LinterOf(document) is not null, 10.0f);
            test.Check("web: a .js file goes to the same server", () => languages.LinterOf(document) == "typescript");
            test.WaitFor(() => code.Diagnostics.Any(found => found.Message.Contains("TypeScript files")), 60.0f);
            test.Check("web: which reads it as JavaScript, a type written in it is one only TypeScript files have", () =>
                code.Diagnostics.Any(found => found.Line == 0 && found.Message.Contains("TypeScript files")));
            test.Run(CloseNow);
        }

        if (css)
        {
            string sheet = Path.Combine(folder, "style.css"), scss = Path.Combine(folder, "style.scss");
            test.Run(() =>
            {
                File.WriteAllText(sheet, "a {\n    colr: red;\n    \n}\n");
                Open(sheet);
            });
            test.WaitFor(() => languages.LinterOf(document) is not null, 10.0f);
            test.Check("web: a .css file is opened in the CSS server", () => languages.LinterOf(document) == "css");
            test.WaitFor(() => code.Diagnostics.Any(found => found.Message.Contains("Unknown property")), 30.0f);
            test.Check("web: a property that isn't one is said", () => code.Diagnostics.Any(found => found.Line == 1 && found.Message.Contains("Unknown property")));
            test.Run(() =>
            {
                code.Focus();
                code.Document.MoveTo(new Horizon.UI.Components.UITextPosition(2, 4));
                code.Document.Insert("disp");
                code.Complete(typed: true);
            });
            test.WaitFor(() => code.CompletionOpen && code.Completions.Any(item => item.Text.StartsWith("display", StringComparison.Ordinal)), 30.0f);
            test.Check("web: and a few letters of a property complete it", () =>
                code.CompletionOpen && code.Completions.Any(item => item.Text.StartsWith("display", StringComparison.Ordinal)));
            test.Run(() =>
            {
                code.CloseCompletion();
                CloseNow();
            });

            // SCSS's own variables are nothing CSS knows, read as CSS they were errors
            test.Run(() =>
            {
                File.WriteAllText(scss, "$gap: 4px;\n.card {\n    margin: $gap;\n    &:hover { colr: red; }\n}\n");
                Open(scss);
            });
            test.WaitFor(() => languages.LinterOf(document) is not null, 10.0f);
            test.Check("web: a .scss file goes to the CSS server too", () => languages.LinterOf(document) == "css");
            test.WaitFor(() => code.Diagnostics.Any(found => found.Message.Contains("Unknown property")), 30.0f);
            test.Check("web: which reads it as SCSS, its variables and nesting fine and only the property that isn't one said", () =>
                code.Diagnostics.Any(found => found.Line == 3 && found.Message.Contains("Unknown property"))
                && !code.Diagnostics.Any(found => found.Line is 0 or 2));
            test.Run(CloseNow);
        }

        if (html)
        {
            string page = Path.Combine(folder, "index.html");
            test.Run(() =>
            {
                File.WriteAllText(page, "<!doctype html>\n<html>\n<head>\n<style>\nbody {\n    \n}\n</style>\n</head>\n<body>\n\n</body>\n</html>\n");
                Open(page);
            });
            test.WaitFor(() => languages.LinterOf(document) is not null, 10.0f);
            test.Check("web: a .html file is opened in the HTML server", () => languages.LinterOf(document) == "html");

            // In the page's own <style> it's CSS that completes, the HTML server has the CSS server's knowledge inside it
            test.Run(() =>
            {
                code.Focus();
                code.Document.MoveTo(new Horizon.UI.Components.UITextPosition(5, 4));
                code.Document.Insert("backgr");
                code.Complete(typed: true);
            });
            test.WaitFor(() => code.CompletionOpen && code.Completions.Any(item => item.Text.StartsWith("background", StringComparison.Ordinal)), 30.0f);
            test.Check("web: in a page's <style> CSS properties complete", () =>
                code.CompletionOpen && code.Completions.Any(item => item.Text.StartsWith("background", StringComparison.Ordinal)));

            // And in the body, elements
            test.Run(() =>
            {
                code.CloseCompletion();
                code.Document.MoveTo(new Horizon.UI.Components.UITextPosition(10, 0));
                code.Document.Insert("<but");
                code.Complete(typed: true);
            });
            test.WaitFor(() => code.CompletionOpen && code.Completions.Any(item => item.Text.Contains("button", StringComparison.Ordinal)), 30.0f);
            test.Check("web: and in its body, elements", () =>
                code.CompletionOpen && code.Completions.Any(item => item.Text.Contains("button", StringComparison.Ordinal)));
            test.Run(() => Shot("web-html-completion"));
            test.Run(() =>
            {
                code.CloseCompletion();
                CloseNow();
            });
        }

        test.Run(LanguageServers.StopAll);
    }
}
