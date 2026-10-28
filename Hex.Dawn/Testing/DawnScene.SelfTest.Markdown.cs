using Horizon.HIDL.Runtime;
using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestNativeMarkdown(HexPointerScript test)
    {
        string file = Path.Combine(testFolder, "native-preview.md");
        float speed = settings.ScrollSpeed;
        test.Run(() =>
        {
            File.WriteAllText(file, "# Native Markdown\n\nA **bold** paragraph with `code` and $\\sigma=\\dfrac{My}{I}$.\n\n| Input | Meaning |\n|---|---|\n| `E` | **Elastic** modulus |\n\n```python\nprint('native preview')\n```\n" + string.Concat(Enumerable.Repeat("\n\nMore document content to scroll.\n", 30)));
            Open(file);
            SwitchView(HexView.Markdown);
        });
        test.Settle(0.3f);
        test.WaitFor(() => notebookRenders.Count == 0, 20);
        test.Check("native markdown: preview is built in and the old extension is absent", () => MarkdownShown && !codeScroll.Visible
            && !extensions.All.Any(extension => extension.Id == "markdown_preview") && document.View == HexView.Markdown);
        test.Check("native markdown: formatted tables, code and math share notebook rendering", () =>
            Descendants(markdownWritten).OfType<NotebookMarkdownTable>().Any()
            && previewPictures.All(picture => picture.Texture is { IsValid: true })
            && Descendants(markdownWritten).OfType<Label>().Any(label => label.Text.Contains("print('native preview')")));
        test.Run(() => { document.Editor.Document.MoveToEnd(false); document.Editor.Document.Insert("\n\nA live change.\n"); });
        test.Settle(0.25f);
        test.WaitFor(() => notebookRenders.Count == 0, 20);
        test.Check("native markdown: unsaved changes update the preview", () => Descendants(markdownWritten).OfType<Label>().Any(label => label.Text.Contains("A live change.")));
        test.Run(() => Shot("native-markdown-preview"));
        test.Settle(0.2f);
        PressKeys(test, "F7");
        test.Check("native markdown: F7 returns to the source editor", () => document.View == HexView.Code && codeScroll.Visible && !MarkdownShown);
        PressKeys(test, "F7");
        test.WaitFor(() => notebookRenders.Count == 0, 20);
        test.Check("native markdown: F7 returns to native preview", () => MarkdownShown);
        test.Run(() => { document.View = HexView.Extension; document.ExtensionView = "markdown_preview.preview"; ApplyView(); });
        test.Check("native markdown: saved legacy preview state migrates to the native view", () => document.View == HexView.Markdown && document.ExtensionView is null);
        test.Run(OpenSettings);
        test.Wait();
        test.Run(() => SettingsPage!.Enter("scroll_speed", new NumberValue(2.5f)));
        test.Check("shared scroll: settings update the compositor immediately", () => settings.ScrollSpeed == 2.5f && compositor.ScrollSpeed == 2.5f);
        test.Run(() => { SettingsPage!.Enter("scroll_speed", new NumberValue(speed)); CloseSettings(); });
        test.Wait();
        test.Run(() => { Show(document); CloseNow(); });
        test.Check("native markdown: closing cancels render work and releases preview ownership", () => !MarkdownShown && previewPictures.Count == 0);
    }
}
