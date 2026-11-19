using Horizon.UI.Components;

namespace Hex.Dawn;

// The self test of spelling through Windows' own spell checker, left out where it isn't there. In code only what's
// written for people is checked, a comment, and never a name or a string of the code. In a page, its words and its
// comments, the comments of the CSS and the script inside it, and never a tag, an attribute, a class or the code. A
// .txt file is prose all through and Markdown is checked outside its code.
internal sealed partial class DawnScene
{
    private const string SPELL_CSHARP = """
        namespace Mixer;

        // This coment has a speling mistake.
        public sealed class Track
        {
            private int recieveCount = 0;
            public string Title = "wrongg";
        }
        """;

    private const string SPELL_HTML = """
        <!doctype html>
        <html lang="en">
        <head>
          <style>
            .mispeled-clas { colr: red; } /* a stlye note */
          </style>
        </head>
        <body>
          <!-- a comented note -->
          <p class="intro-blok">Welcom to the mixxer page.</p>
          <script>
            const recieve = 1; // a scrpit note
          </script>
        </body>
        </html>
        """;

    private void TestSpelling(HexPointerScript test)
    {
        string folder = Path.Combine(testFolder, "spelling");
        string csharp = Path.Combine(folder, "Track.cs"), page = Path.Combine(folder, "index.html"), text = Path.Combine(folder, "notes.txt"), markdown = Path.Combine(folder, "README.md");
        bool was = settings.SpellCheck;
        string language = settings.SpellCheckLanguage;
        bool there = OperatingSystem.IsWindows() && Hex.Core.WindowsSpelling.Check("en-US", [new(0, "speling")], CancellationToken.None).Language is not null;

        test.Run(() =>
        {
            if (!there)
            {
                Console.WriteLine("[Hex self-test] Windows' spell checker for en-US isn't here, spelling is skipped");
                return;
            }
            Directory.CreateDirectory(folder);
            File.WriteAllText(csharp, SPELL_CSHARP.ReplaceLineEndings("\n") + "\n");
            File.WriteAllText(page, SPELL_HTML.ReplaceLineEndings("\n") + "\n");
            File.WriteAllText(text, "These notes have a mispelt word.\n");
            File.WriteAllText(markdown, "# Notes\n\nSome prose with a mistaek.\n\n```csharp\nvar wrongg = 1;\n```\n\nInline `badd` code.\n");
            settings.SpellCheck = true;
            settings.SpellCheckLanguage = "en-US";
        });
        if (!there)
            return;

        // The words marked in the file on screen, by its text
        string[] Marked() => [.. code.SpellingMarks.Select(mark => code.Document.LineAt(mark.Line).Substring(mark.Column, mark.Length))];
        bool Has(string word) => Marked().Contains(word, StringComparer.Ordinal);

        test.Run(() => Open(csharp));
        test.WaitFor(() => code.SpellingMarks.Count > 0, 15.0f);
        test.Check("spelling: a comment in C# is checked, its mistakes marked", () => Has("coment") && Has("speling"));
        test.Check("spelling: and nothing of the code, a name or a string", () => !Has("recieve") && !Has("recieveCount") && !Has("wrongg"));
        test.Run(() => Console.WriteLine($"[Hex self-test] spelling C#: {string.Join(", ", Marked())}"));

        test.Run(() => Open(page));
        test.WaitFor(() => code.SpellingMarks.Count > 0, 15.0f);
        test.Settle(1.0f);
        test.Run(() => Console.WriteLine($"[Hex self-test] spelling HTML: {string.Join(", ", Marked())}"));
        test.Check("spelling: an HTML comment is checked", () => Has("comented"));
        test.Check("spelling: and the page's own words", () => Has("Welcom") && Has("mixxer"));
        test.Check("spelling: and the comments of its style and its script", () => Has("stlye") && Has("scrpit"));
        test.Check("spelling: but not a tag, an attribute, a class, a property or the script's code", () =>
            !Has("doctype") && !Has("lang") && !Has("mispeled") && !Has("clas") && !Has("blok") && !Has("colr") && !Has("recieve") && !Has("const"));
        test.Run(() => Shot("spelling-html"));

        test.Run(() => Open(text));
        test.WaitFor(() => code.SpellingMarks.Count > 0, 15.0f);
        test.Check("spelling: a .txt file is prose all through", () => Has("mispelt"));

        // Its source, the preview's drawn text is nothing to mark
        test.Run(() =>
        {
            Open(markdown);
            SwitchView(HexView.Code);
        });
        test.WaitFor(() => code.SpellingMarks.Count > 0, 15.0f);
        test.Run(() => Console.WriteLine($"[Hex self-test] spelling Markdown: {string.Join(", ", Marked())} (view {document.View}, the code's on screen {codeScroll.Visible})"));
        test.Check("spelling: Markdown's prose is checked, its code isn't", () => Has("mistaek") && !Has("wrongg") && !Has("badd"));

        // Typed into, the new mistake is marked once the text's still
        test.Run(() =>
        {
            code.Focus();
            code.Document.MoveTo(new UITextPosition(2, 0));
            code.Document.Insert("Anotherr line. ");
        });
        test.WaitFor(() => Has("Anotherr"), 10.0f);
        test.Check("spelling: a mistake typed is marked once typing stops", () => Has("Anotherr") && Has("mistaek"));

        test.Run(() =>
        {
            settings.SpellCheck = was;
            settings.SpellCheckLanguage = language;
            CloseNow();
        });
    }
}
