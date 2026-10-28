using System.Text.Json.Nodes;
using Hex.Core.Notebooks;
using Hex.Dawn;
using Horizon.Graphics;

namespace Hex.Tests;

public class NotebookRenderingTests
{
    [Theory]
    [InlineData(@"\delta=\frac{FL^3}{3EI}=0.0016\,\mathrm{m}")]
    [InlineData(@"V(t)=V_0\left(1-e^{-t/(RC)}\right)")]
    [InlineData(@"\Delta p=\frac{128\mu LQ}{\pi D^4}")]
    [InlineData(@"\dot Q=\frac{kA\Delta T}{L}")]
    [InlineData(@"\begin{bmatrix}2&1\\1&3\end{bmatrix}\begin{bmatrix}x\\y\end{bmatrix}=\begin{bmatrix}5\\7\end{bmatrix}")]
    [InlineData(@"\int_0^L x^2\,dx=\frac{L^3}{3}")]
    [InlineData(@"\omega_n=\sqrt{\frac{k}{m}},\quad\zeta=\frac{c}{2\sqrt{km}}")]
    [InlineData(@"$$\sigma=\frac{My}{I}$$")]
    [InlineData(@"I=\dfrac{Bh^3-(B-t_w)h_p^3}{12}")]
    [InlineData(@"\tau=\tfrac{VQ}{It}\le\tau_{web}")]
    public void Engineering_equations_have_visible_ink_and_padding(string latex)
    {
        ImagePixels image = NotebookRender.Math(latex);
        Assert.InRange(image.Width, 20, 4096);
        Assert.InRange(image.Height, 20, 4096);
        int ink = 0;
        for (int i = 0; i < image.Pixels.Length; i += 4)
            if (image.Pixels[i] < 160 && image.Pixels[i + 3] > 100) ink++;
        Assert.True(ink > 40, $"Equation has no visible ink: {latex}");
        Assert.All(image.Pixels.Take(image.Width * 4), value => Assert.Equal((byte)255, value));
    }

    [Fact]
    public void Inline_prose_and_math_render_together()
    {
        var image = NotebookRender.Math(@"Beam deflection $\delta=\frac{FL^3}{3EI}$ is in metres.", prose: true);
        Assert.True(image.Width > 200 && image.Height > 30);
        Assert.Throws<ArgumentException>(() => NotebookRender.Math(@"\unknownEngineeringMacro{x}"));
        var transparent = NotebookRender.Math(@"F=ma", transparent: true);
        Assert.Contains(transparent.Pixels.Where((_, index) => index % 4 == 3), alpha => alpha == 0);
        Assert.Contains(transparent.Pixels.Where((_, index) => index % 4 == 3), alpha => alpha > 200);
    }

    [Fact]
    public void Prose_math_reflows_and_zoom_changes_typesetting()
    {
        string source = NotebookMath.MarkdownText("**Elastic beam theory** uses `E` and ") + @"$\sigma=\dfrac{My}{I}$";
        var wide = NotebookRender.Math(source, prose: true, width: 600);
        var narrow = NotebookRender.Math(source, prose: true, width: 180);
        var zoomed = NotebookRender.Math(source, prose: true, width: 180, zoom: 1.5f);
        Assert.True(narrow.Height > wide.Height);
        Assert.True(zoomed.Height > narrow.Height);
        Assert.InRange(narrow.Width, 1, 360);
        Assert.InRange(zoomed.Width, 1, 360);
    }

    [Fact]
    public void Markdown_tables_keep_formatting_separate_from_plain_export_text()
    {
        var table = NotebookTables.Split("| Assumption | Code |\n|---|---|\n| **Uniform** material | `STRENGTH_FROM` |").Single().Table!;
        Assert.Equal("**Uniform** material", table.MarkdownRows![0][0]);
        Assert.Equal("`STRENGTH_FROM`", table.MarkdownRows[0][1]);
        Assert.Equal("Uniform material", table.Rows[0][0]);
        Assert.Equal("STRENGTH_FROM", table.Rows[0][1]);
        Assert.Equal("a|b", NotebookTables.Split("| code |\n|---|\n| `a|b` |").Single().Table!.Rows[0][0]);
        Assert.False(NotebookTables.HasTable("~~~python\n| a |\n|---|\n~~~"));
        Assert.Equal(@"Flange \(t_{f}\) and \(x^{2}\). Code: `t<sub>f</sub>`", NotebookMarkdown.Normalize("Flange *t<sub>f</sub>* and x<sup>2</sup>. Code: `t<sub>f</sub>`"));
    }

    [Fact]
    public void Svg_definitions_clips_transforms_gradients_and_text_render()
    {
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="200" height="100" viewBox="0 0 200 100">
            <defs><linearGradient id="g"><stop stop-color="red"/><stop offset="1" stop-color="blue"/></linearGradient>
            <clipPath id="c"><rect width="100" height="80"/></clipPath><path id="beam" d="M0 0H80V20H0Z"/></defs>
            <g transform="translate(10 10)" clip-path="url(#c)"><use xlink:href="#beam" fill="url(#g)"/>
            <path d="M0 60 C20 60 60 40 90 10" fill="none" stroke="green" stroke-width="3"/>
            <text x="0" y="45" font-size="12">Load = 1000 N</text></g></svg>
            """;
        var image = NotebookRender.Svg(svg);
        Assert.Equal(400, image.Width);
        Assert.Equal(200, image.Height);
        int At(int x, int y) => (y * image.Width + x) * 4;
        Assert.Equal(0, image.Pixels[At(2, 2) + 3]);
        Assert.True(image.Pixels[At(30, 30)] > image.Pixels[At(30, 30) + 2]);
        Assert.True(image.Pixels[At(170, 30) + 2] > image.Pixels[At(170, 30)]);
        Assert.Equal(0, image.Pixels[At(300, 60) + 3]);
    }

    [Theory]
    [InlineData("not svg")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><")]
    [InlineData("<html/>")]
    public void Invalid_svg_is_recoverable(string source) => Assert.ThrowsAny<Exception>(() => NotebookRender.Svg(source));

    [Fact]
    public void Math_delimiters_preserve_code_escapes_and_unclosed_formulas()
    {
        var parts = NotebookMath.Split("Load $F=10$ N\n\n$$x^2$$\n\\[y^2\\]\\(z\\)\n`$code$`\n```python\n'$no$'\n```\n\\$price and $unfinished");
        Assert.Equal(["F=10", "x^2", "y^2", "z"], parts.Where(part => part.Math).Select(part => part.Text));
        Assert.Equal(2, parts.Count(part => part.Display));
        Assert.Contains("$code$", string.Concat(parts.Where(part => !part.Math).Select(part => part.Text)));
        Assert.DoesNotContain(NotebookMath.Split("Costs $5 and $10."), piece => piece.Math);
    }

    [Fact]
    public void Attachment_references_ignore_fenced_code_and_preserve_names()
    {
        var parts = NotebookAttachments.Split("Before ![beam](attachment:beam%20plot.svg) after\n`![x](attachment:no.png)`\n```\n![x](attachment:no.svg)\n```");
        Assert.Equal("beam plot.svg", Assert.Single(parts.Where(piece => piece.Name is not null)).Name);
        Assert.Equal(3, parts.Count);
    }

    [Fact]
    public void Svg_external_entities_and_external_images_never_supply_pixels()
    {
        var image = NotebookRender.Svg("""
            <?xml version="1.0"?>
            <!DOCTYPE svg SYSTEM "http://127.0.0.1:1/never-fetch.dtd">
            <svg xmlns="http://www.w3.org/2000/svg" width="20" height="20">
            <image href="file:///never-read.png" width="20" height="20"/>
            <image href="http://127.0.0.1:1/never-fetch.png" width="20" height="20"/>
            <style>@import url('http://127.0.0.1:1/never-fetch.css');</style>
            </svg>
            """);
        Assert.All(image.Pixels.Where((_, index) => index % 4 == 3), alpha => Assert.Equal((byte)0, alpha));
        Assert.Throws<ArgumentException>(() => NotebookRender.Svg(new string('x', NotebookRender.MaxSource + 1)));
        Assert.Throws<ArgumentException>(() => NotebookRender.Math(new string('x', 32_001)));
    }

    private static KernelEvent Event(string parent, string type, JsonObject content, string channel = Kernel.IOPUB) =>
        new(channel, new JupyterMessage { Header = new JsonObject { ["msg_type"] = type }, ParentHeader = new JsonObject { ["msg_id"] = parent }, Content = content });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Both_channels_finish_before_a_cell_is_complete(bool replyFirst)
    {
        var run = new NotebookRun();
        var cell = new NotebookCell();
        run.Sent(cell, "request");
        var reply = Event("request", "execute_reply", new JsonObject { ["status"] = "ok" }, Kernel.SHELL);
        var idle = Event("request", "status", new JsonObject { ["execution_state"] = "idle" });
        run.Take(replyFirst ? reply : idle);
        Assert.True(run.IsQueued(cell));
        run.Take(Event("request", "stream", new JsonObject { ["text"] = "last output" }));
        run.Take(replyFirst ? idle : reply);
        Assert.False(run.IsQueued(cell));
        Assert.Equal("last output", cell.Outputs.Single()["text"]!.GetValue<string>());
    }

    [Fact]
    public void Completed_display_updates_keep_the_owner_and_restart_forgets_old_displays()
    {
        var run = new NotebookRun();
        var cell = new NotebookCell();
        run.Sent(cell, "request");
        run.Take(Event("request", "display_data", new JsonObject { ["transient"] = new JsonObject { ["display_id"] = "load" }, ["data"] = new JsonObject { ["text/latex"] = "F=1" } }));
        run.Take(Event("request", "execute_reply", new JsonObject { ["status"] = "ok" }));
        run.Take(Event("request", "status", new JsonObject { ["execution_state"] = "idle" }));
        var update = Event("unknown", "update_display_data", new JsonObject { ["transient"] = new JsonObject { ["display_id"] = "load" }, ["data"] = new JsonObject { ["text/latex"] = "F=2" } });
        Assert.Same(cell, run.Take(update));
        Assert.Equal("F=2", cell.Outputs.Single()["data"]!["text/latex"]!.GetValue<string>());
        run.ClearOutputs(cell);
        Assert.Null(run.Take(update));
        run.Forget();
        Assert.Null(run.Take(update));
    }
}
