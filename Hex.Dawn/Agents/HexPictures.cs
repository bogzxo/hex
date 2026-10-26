using Horizon.Graphics;

namespace Hex.Dawn;

/// <summary>A screenshot cut down to the part asked for and shrunk, as a PNG, for an agent to look at.</summary>
internal static class HexPictures
{
    /// <summary>
    /// A region of a frame shrunk by a scale (1 for as it is), each pixel of what comes out the average of the ones it
    /// covers, so text stays readable rather than every other pixel thrown away.
    /// </summary>
    public static (byte[] Png, int Width, int Height) Cut(ImagePixels frame, int x, int y, int width, int height, float scale)
    {
        int outWidth = Math.Max(1, (int)MathF.Round(width * scale)), outHeight = Math.Max(1, (int)MathF.Round(height * scale));
        byte[] pixels = new byte[outWidth * outHeight * 4];
        float step = 1.0f / scale;

        for (int oy = 0; oy < outHeight; oy++)
        {
            int y0 = y + (int)(oy * step), y1 = Math.Min(y + height, Math.Max(y0 + 1, y + (int)((oy + 1) * step)));
            for (int ox = 0; ox < outWidth; ox++)
            {
                int x0 = x + (int)(ox * step), x1 = Math.Min(x + width, Math.Max(x0 + 1, x + (int)((ox + 1) * step)));
                int r = 0, g = 0, b = 0, count = 0;
                for (int sy = y0; sy < y1; sy++)
                {
                    int row = sy * frame.Width * 4;
                    for (int sx = x0; sx < x1; sx++)
                    {
                        int at = row + sx * 4;
                        r += frame.Pixels[at];
                        g += frame.Pixels[at + 1];
                        b += frame.Pixels[at + 2];
                        count++;
                    }
                }

                // The window's alpha is whatever the last thing drawn left in it, a picture of it is opaque
                int to = (oy * outWidth + ox) * 4;
                pixels[to] = (byte)(r / Math.Max(1, count));
                pixels[to + 1] = (byte)(g / Math.Max(1, count));
                pixels[to + 2] = (byte)(b / Math.Max(1, count));
                pixels[to + 3] = 255;
            }
        }

        return (ImageFile.EncodePng(pixels, outWidth, outHeight), outWidth, outHeight);
    }
}
