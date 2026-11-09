using System.Text;
using Hex.Core;
using Hex.Core.Projects;
using Hex.Dawn;
using Horizon.UI.Scripting;

namespace Hex.Tests;

public sealed class AtomicSaveWatcherTests
{
    [Fact]
    public void Transient_save_files_and_replacement_events_do_not_hide_real_directory_changes()
    {
        string folder = Path.Combine(Path.GetTempPath(), "hex-membership-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string subfolder = Path.Combine(folder, "Sub"), source = Path.Combine(subfolder, "Source.cs");
            Directory.CreateDirectory(subfolder);
            File.WriteAllText(source, "class Source {}");
            var project = new SolutionProject { Path = Path.Combine(folder, "Test.csproj"), Files = [new(source, "Sub/Source.cs", "Compile", false)] };
            string temporary = Path.Combine(folder, ".hex-save-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(temporary, "in-progress save");
            Assert.False(DawnScene.SolutionMembershipChanged(project, temporary, LanguageServerFileChange.Created));
            Assert.False(DawnScene.SolutionMembershipChanged(project, source, LanguageServerFileChange.Deleted));
            string ordinary = Path.Combine(folder, ".hex-save-user.tmp");
            File.WriteAllText(ordinary, "a real user file");
            Assert.True(DawnScene.SolutionMembershipChanged(project, ordinary, LanguageServerFileChange.Created));
            Directory.Delete(subfolder, true);
            Assert.True(DawnScene.SolutionMembershipChanged(project, subfolder, LanguageServerFileChange.Deleted));
            Directory.CreateDirectory(subfolder);
            Assert.True(DawnScene.SolutionMembershipChanged(project, subfolder, LanguageServerFileChange.Created));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task Saving_existing_source_does_not_invalidate_evaluated_membership()
    {
        string folder = Path.Combine(Path.GetTempPath(), "hex-save-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "Source.cs");
            File.WriteAllText(path, "class Before {}");
            var project = new SolutionProject { Path = Path.Combine(folder, "Test.csproj"),
                Files = [new(path, "Source.cs", "Compile", false)] };
            using var folders = new HexFolders();
            folders.Watch([folder]);
            DocumentFile.Write(path, "class After {}", new UTF8Encoding(false));
            await Task.Delay(700);
            var changes = folders.TakeFileChanges();
            Assert.Contains(changes, change => change.Path == path);
            Assert.All(changes, change => Assert.False(DawnScene.SolutionMembershipChanged(project, change.Path, change.Change),
                $"Unnecessary evaluation: {Path.GetFileName(change.Path)}:{change.Change}"));
            string added = Path.Combine(folder, "Added.cs");
            DocumentFile.Write(added, "class Added {}", new UTF8Encoding(false));
            await Task.Delay(700);
            Assert.Contains(folders.TakeFileChanges(), change => change.Path == added && DawnScene.SolutionMembershipChanged(project, change.Path, change.Change));
            File.Delete(path);
            await Task.Delay(700);
            Assert.Contains(folders.TakeFileChanges(), change => change.Path == path && DawnScene.SolutionMembershipChanged(project, change.Path, change.Change));
        }
        finally { Directory.Delete(folder, true); }
    }
}
