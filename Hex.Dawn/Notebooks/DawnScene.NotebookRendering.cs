using System.Numerics;
using System.Text.Json.Nodes;
using Hex.Core.Notebooks;
using Horizon.Graphics;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private sealed class NotebookRenderJob(JsonNode data, Func<ImagePixels> render)
    {
        public JsonNode Data { get; } = data;
        public Func<ImagePixels> Render { get; } = render;
        public List<NotebookPicture> Waiting { get; } = [];
    }

    private readonly Dictionary<JsonNode, NotebookRenderJob> notebookRenders = new(ReferenceEqualityComparer.Instance);
    private readonly Queue<NotebookRenderJob> notebookRenderQueue = [];
    private readonly Queue<(NotebookRenderJob Job, ImagePixels? Pixels, string? Error)> notebookRendered = [];
    private bool notebookRenderWorker;
    private readonly SemaphoreSlim notebookRenderUploaded = new(0);

    private NotebookPicture? RenderedPicture(JsonNode data, Func<ImagePixels> render, List<NotebookPicture> pictures, string? fallback = null, bool mathInk = false)
    {
        if (Engine.WindowManager.Graphics is null) return null;
        if (outputPictures.TryGetValue(data, out var texture))
        {
            var cached = new NotebookPicture(new Vector2(texture.Width, texture.Height) / NotebookRender.Density)
            {
                Texture = texture, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, MathInk = mathInk
            };
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
        pictures.Add(picture);
        QueueNotebookRender(data, render, picture);
        return picture;
    }

    private void QueueNotebookRender(JsonNode data, Func<ImagePixels> render, NotebookPicture picture)
    {
        lock (notebookRenders)
        {
            if (!notebookRenders.TryGetValue(data, out var job))
            {
                notebookRenders[data] = job = new NotebookRenderJob(data, render);
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
        var picture = RenderedPicture(key, () => NotebookRender.Math(latex, prose: true, transparent: true, width: firstWidth, zoom: firstZoom), pictures, mathInk: true);
        if (picture is null) return null;
        picture.RenderKey = key;
        picture.Reflow = (available, requestedZoom) =>
        {
            available = Math.Clamp(MathF.Floor(available / 8) * 8, 40, 1400);
            if (MathF.Abs(available - width) < 8 && requestedZoom == zoom) return;
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
        QueueNotebookRender(key, () => NotebookRender.Math(latex, prose: true, transparent: true, width: width, zoom: zoom), picture);
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
            ImagePixels? pixels = null;
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
                if (pixels is { } image)
                {
                    var replaced = new HashSet<Texture>();
                    var texture = Texture.FromPixels((uint)image.Width, (uint)image.Height, image.Pixels);
                    outputPictures.AddOrUpdate(job.Data, texture);
                    foreach (var picture in job.Waiting)
                    {
                        if (picture.Texture is { } old) replaced.Add(old);
                        picture.Texture = texture;
                        picture.Natural = new Vector2(image.Width, image.Height) / NotebookRender.Density;
                        picture.RenderStatus!.Visible = false;
                    }
                    foreach (var old in replaced) FreeOutputPicture(old);
                    FreeOutputPicture(texture);
                }
                else
                {
                    foreach (var picture in job.Waiting)
                    {
                        picture.RenderStatus!.Text = string.IsNullOrWhiteSpace(picture.RenderFallback)
                            ? "Cannot render output. " + error + "\n" + NotebookFile.Text(job.Data) : picture.RenderFallback;
                        picture.Tooltip = error + "\n" + NotebookFile.Text(job.Data);
                        picture.Natural = new Vector2(600, 60);
                        picture.RenderStatus.Size = new Vector2(592, 60);
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
