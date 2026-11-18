using System.Numerics;

using Horizon.Input;
using Horizon.Rendering;

namespace Hex.Dawn;

// Pictures pasted into the agents' box. Control with V over a clipboard that holds a picture and no text keeps it as a
// PNG in Hex's folder and puts a chip of it over the pickers, a cross on the chip taking it off again. Sent, the
// pictures go with the message, handed to the agent system's extension (chat.images) to give the agent in its own way,
// their paths said at the end of the message too so an agent whose system can't take them still finds the files.
internal sealed partial class DawnScene
{
    // How tall the row of chips is, and how long a pasted picture is kept before it's tidied away
    private const float AGENT_IMAGES_TALL = 56.0f, AGENT_IMAGE_THUMB = 50.0f;
    private static readonly TimeSpan AGENT_IMAGES_KEPT = TimeSpan.FromDays(14);

    private readonly List<string> agentImages = [];
    private StackPanel agentsImages = null!;
    private int agentImagesMade, agentImagesDrawn = -1, agentImagesVersion;
    private bool agentImagesTidied;

    /// <summary>The row of chips in the card, over the pickers, and the box asked first with every paste.</summary>
    private void BuildAgentImages()
    {
        agentsImages = agentsCompose.Add(new StackPanel
        {
            Anchor = Origin.BottomLeft, Pivot = Origin.BottomLeft, Position = new Vector2(0, 36), Direction = UIDirection.Horizontal,
            Spacing = 6, Size = new Vector2(0, AGENT_IMAGE_THUMB), Fill = UIFill.Horizontal, Clip = true, Visible = false
        });
        agentsInput.OnPasting = text => text.Length == 0 && PasteAgentImage();
    }

    /// <summary>The picture on the clipboard kept and put on the message, false when there's none.</summary>
    private bool PasteAgentImage()
    {
        if (ClipboardPicture.Png() is not { Length: > 0 } png)
            return false;

        string folder = Path.Combine(TerminalFiles.UserFolder, "agent_images");
        try
        {
            Directory.CreateDirectory(folder);
            TidyAgentImages(folder);
            string path = Path.Combine(folder, $"pasted-{DateTime.Now:yyyyMMdd-HHmmss}-{++agentImagesMade}.png");
            File.WriteAllBytes(path, png);
            agentImages.Add(path);
            agentImagesVersion++;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Say(Said("agents_panel.image_failed", ("why", e.Message)), error: true);
            return true;
        }
    }

    // The pictures pasted a fortnight ago and more, gone the first time one's pasted in a run
    private void TidyAgentImages(string folder)
    {
        if (agentImagesTidied)
            return;
        agentImagesTidied = true;
        foreach (string old in Directory.EnumerateFiles(folder, "pasted-*.png"))
        {
            try
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(old) > AGENT_IMAGES_KEPT)
                    File.Delete(old);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>The chips made again when the pictures changed, a cross pressed taking effect on the tick after it.</summary>
    private void DrawAgentImages()
    {
        if (agentImagesDrawn == agentImagesVersion)
            return;
        agentImagesDrawn = agentImagesVersion;
        Empty(agentsImages);
        agentsImages.Visible = agentImages.Count > 0;
        for (int i = 0; i < agentImages.Count; i++)
        {
            string path = agentImages[i];
            // The picture itself small, a press opening it full screen, its cross in its corner. A chip of its number alone
            // didn't say which picture it was
            var chip = agentsImages.Add(new Panel { Size = new Vector2(AGENT_IMAGE_THUMB, AGENT_IMAGE_THUMB), Radius = 5, Color = Skin?.Palette.Track ?? new Vector4(1, 1, 1, 0.08f), Outline = Skin?.BorderColor ?? Vector4.Zero, Tooltip = path });
            if (ChatPicture(path) is { } picture)
            {
                float fit = MathF.Min(AGENT_IMAGE_THUMB / picture.Width, AGENT_IMAGE_THUMB / picture.Height);
                chip.Add(new Image { Texture = picture, Size = new Vector2(MathF.Round(picture.Width * fit), MathF.Round(picture.Height * fit)), Anchor = Origin.Center, Pivot = Origin.Center });
            }
            else
                chip.Add(new Label { Text = "$(file-media)", TextScale = 0.2f, Dim = true, Anchor = Origin.Center, Pivot = Origin.Center });
            chip.Add(new Button
            {
                Style = "button_flat", Fill = UIFill.Both, Label = string.Empty, Animated = false,
                Tooltip = Said("agents_panel.picture_open"), OnPressed = () => ShowPictureFull(path)
            });
            chip.Add(new Button
            {
                Icon = "close", Style = "button_flat", Anchor = Origin.TopRight, Pivot = Origin.TopRight, Size = new Vector2(18, 18), Animated = false,
                Tooltip = Said("agents_panel.image_remove"),
                OnPressed = () =>
                {
                    agentImages.Remove(path);
                    agentImagesVersion++;
                }
            });
        }
    }

    /// <summary>The room the chips take in the card, nothing while there are none.</summary>
    private float AgentImagesTall => agentImages.Count > 0 ? AGENT_IMAGES_TALL : 0.0f;

    /// <summary>
    /// The pictures in the box taken for a message, their paths put at the end of what the agent is told, and a word of
    /// them at the end of what the chat shows as asked. The box's chips go with them.
    /// </summary>
    private string[] TakeAgentImages(ref string prompt, ref string asked)
    {
        if (agentImages.Count == 0)
            return [];

        string[] taken = [.. agentImages.Where(File.Exists)];
        agentImages.Clear();
        agentImagesVersion++;
        if (taken.Length == 0)
            return taken;

        prompt += "\n\n" + string.Join("\n", taken.Select(path => $"[Pasted image: {path}]"));
        string count = Said(taken.Length == 1 ? "agents_panel.with_image" : "agents_panel.with_images", ("count", taken.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        asked = asked.Length == 0 ? count : $"{asked}\n\n{count}";
        return taken;
    }
}
