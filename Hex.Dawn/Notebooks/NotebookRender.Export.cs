using System.Text;

using Hex.Core.Modules;
using Hex.Core.Notebooks;

namespace Hex.Dawn;

internal static unsafe partial class NotebookRender
{
    /// <summary>
    /// A formula drawn as SVG for an HTML export, its glyphs as paths so it needs no font and stays sharp at any zoom or
    /// print size. Null when CSharpMath can't read it or when the Notebooks module is disabled/uninstalled, so the export
    /// shows its TeX instead.
    /// </summary>
    public static NotebookHtmlMath? MathSvg(string tex, bool display)
    {
        if (!HexModules.IsEnabled(HexModuleId.Notebooks) || tex.Length > 32_000)
            return null;

        EnsureBackend();
        if (nativeExports is { } native)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(tex);
            fixed (byte* srcPtr = utf8)
            {
                byte* svgPtr = null;
                int svgLen = 0;
                float width = 0, height = 0, descent = 0;
                int rc = native.RenderMathSvg(srcPtr, utf8.Length, display ? 1 : 0,
                    &svgPtr, &svgLen, &width, &height, &descent);
                try
                {
                    if (rc != 0 || svgPtr == null || svgLen <= 0)
                        return null;
                    string svg = Encoding.UTF8.GetString(new ReadOnlySpan<byte>(svgPtr, svgLen));
                    return new NotebookHtmlMath(svg, width, height, descent);
                }
                finally
                {
                    if (svgPtr != null)
                        native.Free(svgPtr);
                }
            }
        }

        if (managedFallback is { } managed
            && managed.RenderMathSvg(tex, display, out string? managedSvg, out float w, out float h, out float d)
            && managedSvg is not null)
        {
            return new NotebookHtmlMath(managedSvg, w, h, d);
        }

        return null;
    }
}
