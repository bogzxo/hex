using Hex.Dawn;

namespace Hex.Tests;

public sealed class HexFoldersTests
{
    // How long the watcher is given to say what happened
    private const int SETTLE_MS = 700;

    [Fact]
    public async Task A_change_in_a_folder_nobody_read_is_a_change_but_not_one_the_explorer_shows()
    {
        string folder = Path.Combine(Path.GetTempPath(), "hex-folders-" + Guid.NewGuid().ToString("N"));
        string unread = Path.Combine(folder, "obj", "Debug");
        Directory.CreateDirectory(unread);
        try
        {
            using var folders = new HexFolders();
            folders.Watch([folder]);
            _ = folders.In(folder);

            // macOS's FSEvents says what was made in the last moments too, the folders just made above; what was before the
            // watch started is let go of, so what is taken below is only what the test does
            if (OperatingSystem.IsMacOS())
            {
                await Task.Delay(SETTLE_MS);
                folders.TakeChanges();
                _ = folders.In(folder);
            }

            // Written where nothing was read, a build's obj, the languages look again but the explorer has nothing new to show
            File.WriteAllText(Path.Combine(unread, "made.txt"), "built");
            await Task.Delay(SETTLE_MS);
            var (any, read) = folders.TakeChanges();
            Assert.True(any);
            Assert.False(read);

            // Made in the folder the explorer read, it's read again
            File.WriteAllText(Path.Combine(folder, "shown.txt"), "shown");
            await Task.Delay(SETTLE_MS);
            (any, read) = folders.TakeChanges();
            Assert.True(any);
            Assert.True(read);
            Assert.Contains(Path.Combine(folder, "shown.txt"), folders.In(folder).Files);

            // Nothing since, nothing to take
            Assert.Equal((false, false), folders.TakeChanges());
        }
        finally { Directory.Delete(folder, true); }
    }
}
