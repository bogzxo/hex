using Horizon.UI;
using Horizon.UI.Drawing;

namespace Hex.Dawn;

// What's wrong shown in the explorer, the name of a file with problems in the colour of the worst of them with how many
// there are at its end, and a dot on every folder over it, so a closed folder still says there's something in it to
// look at. Worked out when the problems change, not as the rows are painted. And the side bar's title cut short with dots
// to the room the explorer's buttons leave it, it ran under them at 200%.
internal sealed partial class DawnScene
{
    // The errors and warnings of each file with any, and the worst of everything under each folder over one (2 for an
    // error, 1 for a warning), by their full paths
    private readonly Dictionary<string, (int Errors, int Warnings)> explorerProblems = new(PlatformPaths.Comparer);
    private readonly Dictionary<string, int> explorerProblemFolders = new(PlatformPaths.Comparer);

    /// <summary>The problems of a file, or for a folder 1 error or 1 warning when the worst under it is one, none for neither.</summary>
    private (int Errors, int Warnings) ProblemsAt(string path, bool isFolder)
    {
        if (isFolder)
        {
            return explorerProblemFolders.TryGetValue(Path.TrimEndingDirectorySeparator(path), out int worst)
                ? worst > 1 ? (1, 0) : (0, 1)
                : (0, 0);
        }

        return explorerProblems.TryGetValue(path, out var counted) ? counted : (0, 0);
    }

    /// <summary>The problems counted again by file and folder after they changed, and the explorer's rows coloured for them.</summary>
    private void ExplorerProblemsChanged()
    {
        explorerProblems.Clear();
        explorerProblemFolders.Clear();
        foreach (HexProblem problem in problemList)
        {
            // What's only worth a look leaves the explorer alone, the way it isn't counted in the status bar either
            if (problem.Hint || problem.Path.Length == 0 || !Path.IsPathFullyQualified(problem.Path))
                continue;

            string full = Path.GetFullPath(problem.Path);
            var (errors, warnings) = explorerProblems.GetValueOrDefault(full);
            explorerProblems[full] = problem.Warning ? (errors, warnings + 1) : (errors + 1, warnings);

            int worst = problem.Warning ? 1 : 2;
            for (string? folder = Path.GetDirectoryName(full); folder is not null; folder = Path.GetDirectoryName(folder))
            {
                string key = Path.TrimEndingDirectorySeparator(folder);
                if (explorerProblemFolders.TryGetValue(key, out int already) && already >= worst)
                    break;
                explorerProblemFolders[key] = worst;
            }
        }

        if (Side == HexSide.Explorer)
            DecorateRows();
    }

    // The title as it was set, as it was cut to fit and the room it was cut for, so it's measured only when one changed
    private string treeTitleWhole = string.Empty, treeTitleShown = string.Empty;
    private float treeTitleRoom = -1.0f;

    // How far the title stays from the first of the buttons after it
    private const float TITLE_GAP = 6.0f;

    /// <summary>Every tick, the side bar's title cut short with dots when the buttons at the end of its row leave it too little room.</summary>
    private void FitTreeTitle(float dt)
    {
        if (compositor.Skin is not { } skin || treeTitle.Parent is not { } head || !treeTitle.Visible)
            return;

        // A title set since it was last cut is the whole of it now
        if (!ReferenceEquals(treeTitle.Text, treeTitleShown))
        {
            treeTitleWhole = treeTitle.Text;
            treeTitleRoom = -1.0f;
        }

        float right = head.Bounds.Max.X;
        IReadOnlyList<UIComponent> beside = head.Children;
        for (int i = 0; i < beside.Count; i++)
        {
            if (beside[i] != treeTitle && beside[i].Visible)
                right = MathF.Min(right, beside[i].Bounds.Min.X);
        }

        float room = MathF.Floor(right - treeTitle.Bounds.Min.X - TITLE_GAP);
        if (room == treeTitleRoom)
            return;

        treeTitleRoom = room;
        float scale = treeTitle.TextScale > 0.0f ? treeTitle.TextScale : skin.TextScale;
        string whole = treeTitleWhole, shown = HexFit.Cut(skin, whole, scale, treeTitle.Bold, room);
        treeTitle.Text = treeTitleShown = shown;
        treeTitle.Tooltip = ReferenceEquals(shown, whole) ? string.Empty : whole;
    }
}
