using Hex.Core.Projects;

using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;

namespace Hex.Dawn;

// What an extension adds to New Project. hex.project_template({ id, name, language, about, main, files, folder,
// commands, optional, tool, server }) puts a kind of project in the window's list after Hex's own, made of the files it
// says (written out with {{name}} and the like filled in, or handed back by a function of the project) and the
// commands run in its folder after. Taken away again as the extension is switched off or started again.
internal sealed partial class HexExtensions
{
    private (string, IRuntimeValue)[] Projects(HexExtension extension) =>
    [
        ("project_template", Natives.Function("project_template", args =>
        {
            ObjectValue said = args.Object(0);
            string id = Text(said, "id") ?? throw args.Wrong("a project needs an id.");
            string main = Text(said, "main") ?? throw args.Wrong("a project needs its main file, the one opened once it's made.");
            if (!said.Properties.ContainsKey("files") && Text(said, "folder") is null)
                throw args.Wrong("a project needs files, or a folder of the extension's to copy.");

            ObjectValue? optional = said.Properties.TryGetValue("optional", out IRuntimeValue? step) && step is ObjectValue leftOut ? leftOut : null;
            var template = new ProjectTemplate(
                id,
                Text(said, "language") ?? string.Empty,
                Text(said, "server") ?? string.Empty,
                main,
                Tool: Text(said, "tool") ?? string.Empty,
                Optional: optional is { } toolOf ? Text(toolOf, "tool") ?? string.Empty : string.Empty,
                Title: Text(said, "name") ?? id,
                About: Text(said, "about") ?? string.Empty,
                OptionalLabel: optional is { } labelOf ? Text(labelOf, "label") ?? id : string.Empty,
                Owner: extension.Id,
                Source: new ExtensionProject(this, extension, said, optional));
            return ProjectTemplates.Add(template) is { } why ? new StringValue(why) : Values.Null;
        }))
    ];

    /// <summary>
    /// An extension's project as New Project asks for it, on the editor's thread as Create is pressed. Its files are what
    /// it said, a function of the project's or a folder of the extension's, and the commands the same. A function that
    /// goes wrong switches the extension off the way any does, and the project isn't made.
    /// </summary>
    private sealed class ExtensionProject(HexExtensions extensions, HexExtension extension, ObjectValue said, ObjectValue? optional) : IProjectSource
    {
        public IReadOnlyList<(string Path, string Text)> Files(string name)
        {
            var files = new List<(string, string)>();
            if (Text(said, "folder") is { } folder)
                files.AddRange(FolderFiles(PathFor(extension, folder), name));

            if (said.Properties.TryGetValue("files", out IRuntimeValue? listed))
            {
                bool made = Values.IsFunction(listed);
                IRuntimeValue given = made ? Call(listed, Project(name)) : listed;
                if (given is not ObjectValue paths)
                    throw new InvalidOperationException($"{extension.Name}'s files for {Text(said, "id")} aren't an object of paths and their text");

                foreach (var (path, text) in paths.Properties)
                {
                    string written = text is StringValue words ? words.Value : text.ToString() ?? string.Empty;
                    files.Add((ProjectTemplates.Fill(path, name), made ? written : ProjectTemplates.Fill(written, name)));
                }
            }

            return files;
        }

        public IReadOnlyList<string[]> Commands(string name, bool wanted)
        {
            var commands = new List<string[]>(CommandsOf(said, name));
            if (wanted && optional is { } step)
                commands.AddRange(CommandsOf(step, name));
            return commands;
        }

        // A list of commands, each a list of the program and its arguments, or a function of the project handing them back
        private IEnumerable<string[]> CommandsOf(ObjectValue of, string name)
        {
            if (!of.Properties.TryGetValue("commands", out IRuntimeValue? listed))
                yield break;

            bool made = Values.IsFunction(listed);
            if ((made ? Call(listed, Project(name)) : listed) is not ListValue commands)
                throw new InvalidOperationException($"{extension.Name}'s commands for {Text(said, "id")} aren't a list");

            foreach (IRuntimeValue command in commands.Items)
            {
                if (command is not ListValue parts)
                    throw new InvalidOperationException($"{extension.Name}'s commands for {Text(said, "id")} are each a list, the program and its arguments");
                yield return [.. parts.Items.Select(part => part is StringValue text ? (made ? text.Value : ProjectTemplates.Fill(text.Value, name)) : part.ToString() ?? string.Empty)];
            }
        }

        // What a function of the extension's is handed, the name every way a project's files want it
        private static ObjectValue Project(string name) => Natives.Object(
            ("name", new StringValue(name)),
            ("module", new StringValue(ProjectTemplates.Module(name))),
            ("package", new StringValue(ProjectTemplates.Package(name))),
            ("lower", new StringValue(ProjectTemplates.Fill("{{lower}}", name))));

        private IRuntimeValue Call(IRuntimeValue function, ObjectValue project)
        {
            if (!extension.Invoke(function, [project], out IRuntimeValue returned))
            {
                extensions.Failed(extension);
                throw new InvalidOperationException($"{extension.Name} went wrong making the project. {extension.CallError}");
            }

            return returned;
        }

        // Every file of a folder of the extension's, its path and text with the name filled in
        private static IEnumerable<(string, string)> FolderFiles(string folder, string name)
        {
            if (!Directory.Exists(folder))
                throw new InvalidOperationException($"{folder} isn't there");

            foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
                yield return (ProjectTemplates.Fill(relative, name), ProjectTemplates.Fill(File.ReadAllText(file), name));
            }
        }
    }
}
