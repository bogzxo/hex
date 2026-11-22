using System.Text;

using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private readonly HashSet<string> linkIgnorePrompted = new(PlatformPaths.Comparer);

    private void OfferLinkIgnoreFiles(IEnumerable<string> folders, Action? done = null)
    {
        if (options.SelfTest)
        {
            done?.Invoke();
            return;
        }

        var pending = new Queue<string>(folders.Select(folder => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)))
            .Distinct(PlatformPaths.Comparer));
        Next();

        void Next()
        {
            while (pending.TryDequeue(out string? folder))
            {
                string path = Path.Combine(folder, HexLinkIgnore.FileName);
                string identity = PlatformPaths.Comparison == StringComparison.OrdinalIgnoreCase ? folder.ToUpperInvariant() : folder;
                string marker = Path.Combine(linkData, "ignore-prompts", HexLinkFiles.Hash(Encoding.UTF8.GetBytes(identity)));
                if (!Directory.Exists(folder) || File.Exists(path) || File.Exists(marker) || !linkIgnorePrompted.Add(folder))
                    continue;

                UIDialog.Show(chrome, Said("link.ignore.title"), Said("link.ignore.body", ("folder", Path.GetFileName(folder))),
                    new DialogChoice(Said("link.ignore.create"), () =>
                    {
                        CreateLinkIgnore(path);
                        Next();
                    }),
                    new DialogChoice(Said("link.ignore.later"), () =>
                    {
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
                            HexLinkFiles.WriteAtomic(marker, []);
                        }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                        {
                            Say(Said("link.ignore.remember_failed", ("why", error.Message)), error: true);
                        }
                        Next();
                    }));
                return;
            }

            done?.Invoke();
        }
    }

    private void CreateLinkIgnore(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                using var writer = new StreamWriter(stream);
                writer.NewLine = "\n";
                writer.WriteLine(Said("link.ignore.file_about"));
                writer.WriteLine(Said("link.ignore.file_patterns"));
                writer.WriteLine(Said("link.ignore.file_exceptions"));
                writer.WriteLine();
                writer.WriteLine(Said("link.ignore.file_defaults"));
                writer.WriteLine("""
                    bin/
                    obj/
                    .vs/
                    node_modules/
                    build/
                    out/
                    target/
                    dist/
                    *.pdb
                    *.user
                    .DS_Store
                    """);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (!File.Exists(path))
            {
                Say(Said("link.ignore.create_failed", ("why", error.Message)), error: true);
                return;
            }
        }

        Open(path);
    }
}
