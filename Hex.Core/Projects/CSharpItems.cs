using System.Text;
using System.Text.RegularExpressions;

namespace Hex.Core.Projects;

/// <summary>What Add, New Item makes of C#, the kinds Visual Studio's templates have.</summary>
internal enum CSharpItemKind
{
    Class,
    Interface,
    Record,
    Struct,
    Enum
}

/// <summary>How a project writes its C#, a file scoped namespace or a block, how far in and how its lines end.</summary>
internal sealed record CSharpStyle(bool FileScoped, string Indent, string NewLine)
{
    /// <summary>What a project with no C# of its own yet gets, what the SDK's templates write now.</summary>
    public static readonly CSharpStyle Default = new(true, "    ", Environment.NewLine);
}

/// <summary>
/// A new file of C# for Add, Class or New Item, Visual Studio's templates. Its namespace is the project's RootNamespace
/// (its AssemblyName, or its name, when it doesn't say one) with the folders down to it after, each made a name C# takes,
/// and it's written the way the project's other files are, a file scoped namespace when they have them, their
/// indentation and their line endings.
/// </summary>
internal static partial class CSharpItems
{
    // namespace Some.Thing; at the start of a line
    [GeneratedRegex(@"^[ \t]*namespace[ \t]+[\w.@]+[ \t]*;", RegexOptions.Multiline)]
    private static partial Regex FileScopedNamespace();

    // namespace Some.Thing followed by its brace, on the line or the next
    [GeneratedRegex(@"^[ \t]*namespace[ \t]+[\w.@]+\s*\{", RegexOptions.Multiline)]
    private static partial Regex BlockNamespace();

    // How many files of the project are looked at for its style, the first few found are enough
    private const int SAMPLES = 6;

    /// <summary>A name C# takes, what it can't have made underscores and one starting with a digit given one in front.</summary>
    public static string Identifier(string name)
    {
        var made = new StringBuilder(name.Length + 1);
        foreach (char c in name.Trim())
            made.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        if (made.Length == 0 || char.IsDigit(made[0]))
            made.Insert(0, '_');
        return made.ToString();
    }

    /// <summary>A namespace C# takes, each piece between its dots a name.</summary>
    public static string NamespaceName(string name) =>
        string.Join('.', name.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Identifier));

    /// <summary>The namespace a project's files start in, its RootNamespace, else its AssemblyName, else its name.</summary>
    public static string RootNamespaceOf(string project, string projectText) =>
        NamespaceName(ProjectFileEdits.Property(projectText, "RootNamespace") is { Length: > 0 } root ? root
            : ProjectFileEdits.Property(projectText, "AssemblyName") is { Length: > 0 } assembly && !assembly.Contains("$(", StringComparison.Ordinal) ? assembly
            : Path.GetFileNameWithoutExtension(project));

    /// <summary>The namespace of a new file in a folder of a project, the root's with the folders down to it after.</summary>
    public static string NamespaceFor(string rootNamespace, string projectFolder, string folder)
    {
        string relative = Path.GetRelativePath(projectFolder, folder);
        if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return rootNamespace;

        IEnumerable<string> pieces = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries).Select(NamespaceName);
        return string.Join('.', [rootNamespace, .. pieces]);
    }

    /// <summary>How some files of C# are written, what most of them do, the default for what none of them say.</summary>
    public static CSharpStyle StyleOf(IEnumerable<string> samples)
    {
        int fileScoped = 0, block = 0;
        string? indent = null, newLine = null;
        foreach (string text in samples)
        {
            fileScoped += FileScopedNamespace().Count(text);
            block += BlockNamespace().Count(text);
            newLine ??= text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : text.Contains('\n') ? "\n" : null;
            indent ??= IndentOf(text);
        }

        return new CSharpStyle(block == 0 || fileScoped >= block, indent ?? CSharpStyle.Default.Indent, newLine ?? CSharpStyle.Default.NewLine);
    }

    // One step of a file's indentation, the first indented line's tab, or its spaces when that's a sensible step
    private static string? IndentOf(string text)
    {
        foreach (string line in text.Split('\n'))
        {
            if (line.StartsWith('\t'))
                return "\t";
            int spaces = line.Length - line.TrimStart(' ').Length;
            if (spaces is >= 2 and <= 8 && line.Trim().Length > 0 && !line.TrimStart().StartsWith('*'))
                return new string(' ', spaces);
        }

        return null;
    }

    /// <summary>
    /// How the C# of a project is written, read off a few of its files, the ones in the folder the new one goes in
    /// first. Only the first few found are read, a big project is never walked.
    /// </summary>
    public static CSharpStyle StyleIn(string projectFolder, string folder)
    {
        var samples = new List<string>();
        foreach (string file in Sample(folder, false).Concat(Sample(projectFolder, true)).Distinct(PlatformPaths.Comparer).Take(SAMPLES))
        {
            try
            {
                samples.Add(File.ReadAllText(file));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // One that can't be read says nothing of the style, the others do
            }
        }

        return StyleOf(samples);
    }

    // The .cs files of a folder, or of it and the ones in it, never what's built into bin and obj
    private static IEnumerable<string> Sample(string folder, bool deep)
    {
        if (!Directory.Exists(folder))
            return [];

        var options = new EnumerationOptions { RecurseSubdirectories = deep, IgnoreInaccessible = true, MaxRecursionDepth = 4 };
        string bin = Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, obj = Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(folder, "*.cs", options)
            .Where(file => !file.Contains(bin, StringComparison.OrdinalIgnoreCase) && !file.Contains(obj, StringComparison.OrdinalIgnoreCase))
            .Take(SAMPLES);
    }

    /// <summary>The text of a new file of C#, a type of a kind and a name in a namespace, written in a style.</summary>
    public static string Text(CSharpItemKind kind, string name, string space, CSharpStyle style)
    {
        string nl = style.NewLine;
        string keyword = kind switch
        {
            CSharpItemKind.Interface => "interface",
            CSharpItemKind.Record => "record",
            CSharpItemKind.Struct => "struct",
            CSharpItemKind.Enum => "enum",
            _ => "class"
        };

        // internal, what Visual Studio's templates have
        string[] type = [$"internal {keyword} {Identifier(name)}", "{", "}"];
        if (style.FileScoped)
            return $"namespace {space};{nl}{nl}{string.Join(nl, type)}{nl}";

        return $"namespace {space}{nl}{{{nl}{string.Join(nl, type.Select(line => style.Indent + line))}{nl}}}{nl}";
    }

    /// <summary>A file's name for a type, .cs on the end when it hasn't one.</summary>
    public static string FileName(string typed) =>
        Path.GetExtension(typed).Equals(".cs", StringComparison.OrdinalIgnoreCase) ? typed : typed + ".cs";
}
