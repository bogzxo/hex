namespace Hex.Core.Projects;

/// <summary>
/// What a task's command line says to dotnet, read well enough to know what it builds and where it puts it. `dotnet build
/// Hex/Hex.csproj -p:OutDir=..\out\` builds one project into a folder of its own choosing, `dotnet run --project X` builds
/// X where X says, a bare `dotnet build` builds the solution or the project of the folder it runs in.
/// </summary>
internal sealed record DotnetCommandLine(string Verb, string? Target, string? OutputFolder)
{
    // The verbs of dotnet that build before they do anything else
    private static readonly HashSet<string> Building = new(StringComparer.OrdinalIgnoreCase) { "build", "msbuild", "run", "test", "publish", "pack", "watch" };

    /// <summary>
    /// The dotnet commands of a command line that build, each with what it builds and where it puts it, made whole from
    /// the folder it runs in. A line chained with &amp;&amp; or ; can have more than one.
    /// </summary>
    public static List<DotnetCommandLine> Read(string command, string folder)
    {
        var found = new List<DotnetCommandLine>();
        List<string> words = Words(command);
        for (int i = 0; i < words.Count; i++)
        {
            string program = Path.GetFileNameWithoutExtension(words[i].Trim('"'));
            bool first = i == 0 || words[i - 1] is "&&" or ";" or "||" or "|";
            if (!first || !program.Equals("dotnet", StringComparison.OrdinalIgnoreCase) || i + 1 >= words.Count || !Building.Contains(words[i + 1]))
                continue;

            string verb = words[i + 1];
            string? target = null, output = null;
            int at = i + 2;
            for (; at < words.Count && words[at] is not ("&&" or ";" or "||" or "|"); at++)
            {
                string word = words[at];
                string? value = at + 1 < words.Count ? words[at + 1] : null;
                if ((word is "--project" or "-p" && verb.Equals("run", StringComparison.OrdinalIgnoreCase)) && value is not null)
                {
                    target = value;
                    at++;
                }
                else if (word is "-o" or "--output" && value is not null)
                {
                    output = value;
                    at++;
                }
                else if ((PropertyValue(word, "OutDir") ?? PropertyValue(word, "OutputPath")) is { } outDir)
                {
                    output = outDir;
                }
                else if (word == "--")
                {
                    // What comes after is the program's own arguments
                    for (; at + 1 < words.Count && words[at + 1] is not ("&&" or ";" or "||" or "|"); at++)
                    {
                    }
                }
                else if (!word.StartsWith('-') && !word.StartsWith('/') && target is null && LooksLikeProject(word))
                {
                    target = word;
                }
            }

            found.Add(new DotnetCommandLine(verb, Whole(target, folder), Whole(output, folder)));
            i = at;
        }

        return found;
    }

    private static bool LooksLikeProject(string word) =>
        word.EndsWith("proj", StringComparison.OrdinalIgnoreCase) || word.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
        || word.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) || word.Contains('/') || word.Contains('\\') || word == ".";

    // A property set on the command line, -p:Name=value, /p:Name=value, -property:Name=value, null when it isn't that one
    private static string? PropertyValue(string word, string name)
    {
        foreach (string prefix in (string[])["-p:", "/p:", "-property:", "/property:", "--property:"])
        {
            if (!word.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (string pair in word[prefix.Length..].Split(';'))
            {
                int equals = pair.IndexOf('=');
                if (equals > 0 && pair[..equals].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                    return pair[(equals + 1)..].Trim().Trim('"');
            }
        }

        return null;
    }

    private static string? Whole(string? path, string folder)
    {
        if (path is null)
            return null;

        path = path.Trim('"').TrimEnd('\\', '/');
        try
        {
            return Path.GetFullPath(Path.Combine(folder, path.Length == 0 ? "." : path));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// A command line cut into its words the way a shell would, quotes kept together, &amp;&amp; and ; words of their own. A
    /// semicolon inside an option is MSBuild's list of properties and stays in it.
    /// </summary>
    internal static List<string> Words(string command)
    {
        var words = new List<string>();
        var word = new System.Text.StringBuilder();
        char quote = '\0';
        void End()
        {
            if (word.Length > 0)
                words.Add(word.ToString());
            word.Clear();
        }

        for (int i = 0; i < command.Length; i++)
        {
            char c = command[i];
            if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
                else
                    word.Append(c);
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (char.IsWhiteSpace(c))
            {
                End();
            }
            else if ((c == ';' && !(word.Length > 0 && word[0] is '-' or '/')) || (c is '&' or '|' && i + 1 < command.Length && command[i + 1] == c))
            {
                End();
                words.Add(c == ';' ? ";" : new string(c, 2));
                if (c != ';')
                    i++;
            }
            else
            {
                word.Append(c);
            }
        }

        End();
        return words;
    }
}
