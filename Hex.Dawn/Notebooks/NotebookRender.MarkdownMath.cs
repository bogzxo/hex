using System.Text;

using Hex.Core;

namespace Hex.Dawn;

internal static unsafe partial class NotebookRender
{
    /// <summary>Document prose uses the UI fonts; only formula runs go through mathematical typesetting.</summary>
    internal static NotebookPixels MarkdownMath(string source, float density, float width = 600, float zoom = 1)
    {
        EnsureEnabled();
        if (source.Length > 32_000)
            throw new ArgumentException("Math output exceeds the rendering limit.");

        string fontsDir = Path.Combine(HexHost.ProgramFolder, "Assets", "fonts");
        EnsureBackend();
        if (nativeExports is { } native)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(source);
            byte[] fontsUtf8 = Encoding.UTF8.GetBytes(fontsDir);
            fixed (byte* srcPtr = utf8)
            fixed (byte* fontsPtr = fontsUtf8)
            {
                int w = 0, h = 0, pixLen = 0, errLen = 0;
                float natW = 0, natH = 0;
                byte* pixPtr = null, errPtr = null;
                int rc = native.RenderMarkdownMath(srcPtr, utf8.Length, fontsPtr, fontsUtf8.Length, density, width, zoom,
                    &w, &h, &natW, &natH, &pixPtr, &pixLen, &errPtr, &errLen);
                return UnpackBitmap(native, rc, w, h, natW, natH, pixPtr, pixLen, errPtr, errLen);
            }
        }

        if (managedFallback is { } managed)
        {
            int rc = managed.RenderMarkdownMath(source, fontsDir, density, width, zoom,
                out int w, out int h, out float natW, out float natH, out byte[] pixels, out string? error);
            return UnpackManagedBitmap(rc, w, h, natW, natH, pixels, error);
        }

        throw new InvalidOperationException("Notebooks rendering module is not installed.");
    }
}
