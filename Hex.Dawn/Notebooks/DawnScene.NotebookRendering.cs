using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Hex.Core.Notebooks;
using Horizon.Graphics;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private sealed class NotebookRenderJob(JsonNode data, Func<NotebookPixels> render, float density)
    {
        public JsonNode Data { get; } = data;
        public Func<NotebookPixels> Render { get; } = render;
        public float Density { get; } = density;
        public List<NotebookPicture> Waiting { get; } = [];
    }

    // How big every picture that was rendered came out and at what density, for one shown again from what was kept
    private readonly ConditionalWeakTable<JsonNode, StrongBox<(Vector2 Natural, float Density)>> renderedSizes = new();

    /// <summary>
    /// How many pixels a unit of the layout is rendered at for a picture shown at so much of the code's zoom (a cell's
    /// output) or at its own size (math in prose, which is rendered at the zoom already), a pixel of the picture to a
    /// pixel of the screen.
    /// </summary>
    private float PictureDensity(bool zoomed) =>
        Math.Clamp(compositor.UIScale * (zoomed ? settings.CodeZoom : 1.0f), NotebookRender.LeastDensity, NotebookRender.MostDensity);

    private readonly Dictionary<JsonNode, NotebookRenderJob> notebookRenders = new(ReferenceEqualityComparer.Instance);
    private readonly Queue<NotebookRenderJob> notebookRenderQueue = [];
    private readonly Queue<(NotebookRenderJob Job, NotebookPixels? Pixels, string? Error)> notebookRendered = [];
    private bool notebookRenderWorker;
    private readonly SemaphoreSlim notebookRenderUploaded = new(0);

    /// <param name="render">How the picture is rendered at a density, see <see cref="PictureDensity"/>.</param>
    /// <param name="zoomed">Whether it's shown at the code's zoom (an output) or at its own size, see PictureDensity.</param>
    /// <param name="keepDense">Whether it renders itself again at another density as it's fitted (see NotebookPicture.KeepDense), math in prose does it as it reflows instead.</param>
    private NotebookPicture? RenderedPicture(JsonNode data, Func<float, NotebookPixels> render, List<NotebookPicture> pictures, bool zoomed, string? fallback = null, bool mathInk = false, bool keepDense = true)
    {
        if (Engine.WindowManager.Graphics is null) return null;
        float density = PictureDensity(zoomed);
        if (outputPictures.TryGetValue(data, out var texture) && renderedSizes.TryGetValue(data, out var rendered) && rendered.Value.Density == density)
        {
            var cached = new NotebookPicture(rendered.Value.Natural)
            {
                Texture = texture, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, MathInk = mathInk, Density = density
            };
            if (keepDense)
                cached.KeepDense = () => KeepDense(cached, data, render, zoomed);
            pictures.Add(cached);
            return cached;
        }
        var picture = new NotebookPicture(new Vector2(360, 28)) { Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, MathInk = mathInk };
        picture.RenderFallback = fallback;
        picture.RenderStatus = picture.Add(new Label
        {
            Text = "Rendering…", TextScale = 0.13f, Dim = true, Wrap = true,
            Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Size = new Vector2(352, 28), Align = Origin.TopLeft
        });
        picture.Tooltip = NotebookFile.Text(data);
        picture.Density = density;
        if (keepDense)
            picture.KeepDense = () => KeepDense(picture, data, render, zoomed);
        pictures.Add(picture);
        QueueNotebookRender(data, () => render(density), picture, density);
        return picture;
    }

    // Helper method for a picture rendered again, in its place, once what it's shown on wants another density of it.
    // Nothing is made while the density stays what it was, it's asked every tick a cell is fitted
    private void KeepDense(NotebookPicture picture, JsonNode data, Func<float, NotebookPixels> render, bool zoomed)
    {
        float density = PictureDensity(zoomed);
        if (density == picture.Density)
            return;

        picture.Density = density;
        QueueNotebookRender(data, () => render(density), picture, density);
    }

    private void QueueNotebookRender(JsonNode data, Func<NotebookPixels> render, NotebookPicture picture, float density)
    {
        lock (notebookRenders)
        {
            // One on its way at another density is let go of, the worker drops what it finishes for a job that's gone
            if (!notebookRenders.TryGetValue(data, out var job) || job.Density != density)
            {
                notebookRenders[data] = job = new NotebookRenderJob(data, render, density);
                notebookRenderQueue.Enqueue(job);
            }
            job.Waiting.Add(picture);
            if (!notebookRenderWorker)
            {
                notebookRenderWorker = true;
                _ = Task.Run(RenderNotebookQueue);
            }
        }
    }

    private NotebookPicture? ProseMathPicture(string latex, List<NotebookPicture> pictures)
    {
        float width = Math.Clamp(MarkdownShown ? markdownPreview.Size.X - 48 : notebookView.Size.X - CELL_GUTTER - 40, 120, 1400);
        float zoom = settings.CodeZoom;
        float firstWidth = width, firstZoom = zoom;
        var key = JsonValue.Create(latex)!;
        var picture = RenderedPicture(key, density => NotebookRender.Math(latex, density, prose: true, transparent: true, width: firstWidth, zoom: firstZoom), pictures, zoomed: false, mathInk: true, keepDense: false);
        if (picture is null) return null;
        picture.RenderKey = key;
        picture.Reflow = (available, requestedZoom) =>
        {
            available = Math.Clamp(MathF.Floor(available / 8) * 8, 40, 1400);
            if (MathF.Abs(available - width) < 8 && requestedZoom == zoom && picture.Density == PictureDensity(zoomed: false)) return;
            width = available;
            zoom = requestedZoom;
            ReflowProseMath(picture, latex, width, zoom);
        };
        return picture;
    }

    // Keep the capturing render delegate outside the per-tick callback. A nested lambda allocated its closure even
    // when the width/zoom guard returned immediately, making otherwise cached notebook fitting allocate every tick.
    private void ReflowProseMath(NotebookPicture picture, string latex, float width, float zoom)
    {
        lock (notebookRenders)
        {
            foreach (var pending in notebookRenders.Values.ToList())
            {
                pending.Waiting.Remove(picture);
                if (pending.Waiting.Count == 0) notebookRenders.Remove(pending.Data);
            }
        }
        var key = JsonValue.Create(latex)!;
        picture.RenderKey = key;
        float density = PictureDensity(zoomed: false);
        picture.Density = density;
        QueueNotebookRender(key, () => NotebookRender.Math(latex, density, prose: true, transparent: true, width: width, zoom: zoom), picture, density);
    }

    // A single worker bounds peak CPU and raster memory. Closing or replacing a cell cancels queued work.
    private async Task RenderNotebookQueue()
    {
        while (true)
        {
            NotebookRenderJob job;
            lock (notebookRenders)
            {
                if (!notebookRenderQueue.TryDequeue(out job!))
                {
                    notebookRenderWorker = false;
                    return;
                }
                if (job.Waiting.Count == 0) continue;
            }
            NotebookPixels? pixels = null;
            string? error = null;
            try { pixels = job.Render(); }
            catch (Exception e) when (e is not OutOfMemoryException)
            { error = e.Message; }
            lock (notebookRenders)
                notebookRendered.Enqueue((job, pixels, error));
            Engine.WindowManager.RequestExclusive();
            await notebookRenderUploaded.WaitAsync().ConfigureAwait(false);
        }
    }

    // Called only in the renderer's exclusive phase, like the raster-image upload path.
    private void MakeRenderedNotebookPictures()
    {
        lock (notebookRenders)
        {
            while (notebookRendered.TryDequeue(out var rendered))
            {
                var (job, pixels, error) = rendered;
                notebookRenderUploaded.Release();
                if (!notebookRenders.TryGetValue(job.Data, out var current) || !ReferenceEquals(current, job)) continue;
                notebookRenders.Remove(job.Data);
                if (job.Waiting.Count == 0) continue;
                if (pixels is { Pixels: var image, Natural: var natural })
                {
                    var replaced = new HashSet<Texture>();
                    var texture = Texture.FromPixels((uint)image.Width, (uint)image.Height, image.Pixels, TextureDefinition.RgbaUnsignedBytePicture);
                    outputPictures.AddOrUpdate(job.Data, texture);
                    renderedSizes.AddOrUpdate(job.Data, new StrongBox<(Vector2, float)>((natural, job.Density)));
                    foreach (var picture in job.Waiting)
                    {
                        if (picture.Texture is { } old) replaced.Add(old);
                        picture.Texture = texture;
                        picture.Natural = natural;
                        // A cached picture can be rerendered at a new density without a placeholder label.
                        if (picture.RenderStatus is { } status)
                            status.Visible = false;
                    }
                    foreach (var old in replaced) FreeOutputPicture(old);
                    FreeOutputPicture(texture);
                }
                else
                {
                    foreach (var picture in job.Waiting)
                    {
                        picture.Tooltip = error + "\n" + NotebookFile.Text(job.Data);
                        // A cached picture still has its old texture if the higher-density render fails. Only the
                        // uncached placeholder has a status label to replace with the readable fallback.
                        if (picture.RenderStatus is { } status)
                        {
                            status.Text = string.IsNullOrWhiteSpace(picture.RenderFallback)
                                ? "Cannot render output. " + error + "\n" + NotebookFile.Text(job.Data) : picture.RenderFallback;
                            picture.Natural = new Vector2(600, 60);
                            status.Size = new Vector2(592, 60);
                        }
                    }
                }
            }
        }
    }

    private void ReleaseNotebookPictures(List<NotebookPicture> pictures)
    {
        lock (notebookRenders)
        {
            foreach (var job in notebookRenders.Values.ToList())
            {
                job.Waiting.RemoveAll(pictures.Contains);
                if (job.Waiting.Count == 0) notebookRenders.Remove(job.Data);
            }
        }
        foreach (var texture in pictures.Select(picture => picture.Texture).OfType<Texture>().Distinct())
            FreeOutputPicture(texture);
    }
}
