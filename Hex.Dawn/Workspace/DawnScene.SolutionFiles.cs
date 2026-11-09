using Hex.Core.Projects;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void AddProjectFiles(UITreeItem parent, SolutionProject project) =>
        FillProjectFiles(parent, project, project.Files, "");

    private void FillProjectFiles(UITreeItem parent, SolutionProject project, IReadOnlyList<SolutionProjectFile> files, string prefix)
    {
        foreach (var group in files.Where(file => file.DisplayPath[prefix.Length..].Contains('/'))
            .GroupBy(file => file.DisplayPath[prefix.Length..].Split('/')[0]).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            string nested = prefix + group.Key + "/";
            string key = "items:" + project.Path + "|" + nested;
            var members = group.ToArray();
            var folder = new UITreeItem(group.Key, "folder")
            {
                IconColor = FolderColor,
                Expandable = true,
                Expanded = solutionFlipped.Contains(key),
                Tag = new SideTag(Toggled: row =>
                {
                    if (!solutionFlipped.Remove(key)) solutionFlipped.Add(key);
                    row.Icon = row.Expanded ? "folder-opened" : "folder";
                }, Expanding: row =>
                {
                    if (row.Children is not null) return;
                    row.Children = [];
                    FillProjectFiles(row, project, members, nested);
                })
            };
            // These are logical project folders, not paths that Delete can remove from disk.
            solutionHeads[folder] = project.Folder;
            if (folder.Expanded)
            {
                folder.Children = [];
                FillProjectFiles(folder, project, members, nested);
            }
            parent.Add(folder);
        }
        foreach (var file in files.Where(file => !file.DisplayPath[prefix.Length..].Contains('/')))
        {
            if (explorerMade >= HexExplorer.MAX_EXPLORER_ROWS) break;
            var row = FileRow(file.Path, 1);
            row.Text = file.DisplayPath[prefix.Length..];
            row.Detail = file.ItemTypes;
            if (file.Linked)
            {
                solutionHeads[row] = project.Folder;
                row.Detail = Said("solution.linked_item", ("kind", file.ItemTypes));
                // The shared source stays owned by its real project/folder. No destructive
                // filesystem menu on a logical link; project-link removal needs its own edit.
                row.Tag = new SideTag(Pressed: () => Open(file.Path));
            }
            parent.Add(row);
        }
    }
}
