using System.Numerics;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

/// <summary>A Markdown table is document content: its rows grow to fit wrapped, formatted cells.</summary>
internal sealed class NotebookMarkdownTable : StackPanel
{
    public List<StackPanel[]> Cells { get; } = [];
    public float[] Weights { get; set; } = [];
    public float FittedWidth { get; set; } = -1;
    public float FittedZoom { get; set; } = -1;

    public NotebookMarkdownTable()
    {
        Direction = UIDirection.Vertical;
        Spacing = 1;
        Anchor = Origin.TopLeft;
        Pivot = Origin.TopLeft;
    }

    public void Fit(float width, float zoom)
    {
        FittedWidth = width;
        FittedZoom = zoom;
        Size = Size with { X = width };
        float total = Weights.Sum();
        foreach (var row in Cells)
            for (int i = 0; i < row.Length; i++)
            {
                float cellWidth = MathF.Max(12, (width - row.Length + 1) * Weights[i] / total);
                row[i].Size = row[i].Size with { X = cellWidth };
                DawnScene.FitNotebookMarkdownCell(row[i], MathF.Max(4, cellWidth - 12), zoom);
            }
    }

    protected override void Update(float dt)
    {
        base.Update(dt);
        if (Module?.Compositor.Skin is not { } skin) return;
        for (int r = 0; r < Cells.Count; r++)
            foreach (var cell in Cells[r])
                cell.Color = r == 0 ? skin.PanelColor : skin.FieldColor;
    }
}
