using System.Text;

namespace Hex.Core.Projects;

/// <summary>
/// A kind of project New Project makes, what language it's in, which server of language_servers.hor reads it and how it
/// comes to be. Most are a handful of files written straight out, so a project is there without its tools being on the
/// machine yet. The .NET ones are the SDK's own templates, `dotnet new`, which know the framework better than we would.
/// </summary>
/// <param name="Id">What it's known by, csharp-console, go, the self test and the settings pick it by.</param>
/// <param name="Language">The language it's in, what the list groups it under.</param>
/// <param name="Server">The key of its server in language_servers.hor, csharp, python, java.</param>
/// <param name="Main">
/// The file opened once it's made, relative to the project's folder, {module} and {package} filled in. Its row's icon is
/// this file's.
/// </param>
/// <param name="Tool">The program it can't be made without (dotnet), empty for one that's only files.</param>
/// <param name="Optional">The program the step it can leave out runs (python for a venv), empty for no such step.</param>
/// <param name="Title">What its row says, empty for one of Hex's own, whose title is in the texts under its id.</param>
/// <param name="About">What it is, said over the form, empty for one of Hex's own (in the texts too).</param>
/// <param name="OptionalLabel">What the box of the step it can leave out says, empty for one of Hex's own.</param>
/// <param name="Owner">The extension that added it, empty for one of Hex's own.</param>
/// <param name="Source">Where an added one's files and commands come from, null for one of Hex's own.</param>
internal sealed record ProjectTemplate(string Id, string Language, string Server, string Main, string Tool = "", string Optional = "",
    string Title = "", string About = "", string OptionalLabel = "", string Owner = "", IProjectSource? Source = null)
{
    /// <summary>Whether it's one of Hex's own rather than an extension's.</summary>
    public bool BuiltIn => Owner.Length == 0;
}

/// <summary>
/// What an extension's project is made of, asked for on the editor's thread (an extension's HIDL runs nowhere else) as
/// Create is pressed, before the making goes to the background.
/// </summary>
internal interface IProjectSource
{
    /// <summary>The files, paths relative to the project's folder with forward slashes, and their text.</summary>
    IReadOnlyList<(string Path, string Text)> Files(string name);

    /// <summary>The commands run in the folder once the files are there, the step that can be left out among them when it's wanted.</summary>
    IReadOnlyList<string[]> Commands(string name, bool optional);
}

/// <summary>A project's files and commands worked out, what ProjectCreation makes it from.</summary>
internal sealed record ProjectContents(IReadOnlyList<(string Path, string Text)> Files, IReadOnlyList<string[]> Commands);

/// <summary>Why a project's name won't do, None for one that will.</summary>
internal enum ProjectNameProblem
{
    None,
    Empty,
    BadStart,
    BadCharacter,
    TooLong
}

/// <summary>
/// The projects New Project offers and what each is made of. The files are worked out here and the commands said, the
/// dialog (DawnScene.NewProject) writes and runs them off the editor's thread.
/// </summary>
internal static class ProjectTemplates
{
    // Long enough for anything sane, short enough that Windows' paths don't blow up three folders down
    private const int LONGEST_NAME = 64;

    /// <summary>Hex's own projects, grouped by language in the order the dialog lists them.</summary>
    public static IReadOnlyList<ProjectTemplate> BuiltIn { get; } =
    [
        new("csharp-console", "C#", "csharp", "Program.cs", Tool: "dotnet"),
        new("csharp-classlib", "C#", "csharp", "Class1.cs", Tool: "dotnet"),
        new("csharp-xunit", "C#", "csharp", "UnitTest1.cs", Tool: "dotnet"),
        new("csharp-horizon", "C#", "csharp", "MainScene.cs", Tool: "dotnet"),
        new("python", "Python", "python", "src/{module}/__main__.py", Optional: PythonCommand),
        new("java-maven", "Java", "java", "src/main/java/{package}/App.java"),
        new("cpp-cmake", "C++", "cpp", "src/main.cpp"),
        new("rust", "Rust", "rust", "src/main.rs"),
        new("go", "Go", "go", "main.go"),
        new("typescript", "TypeScript", "typescript", "src/index.ts"),
        new("d-dub", "D", "d", "source/app.d")
    ];

    // What extensions added, after Hex's own in the order they were added
    private static readonly List<ProjectTemplate> added = [];
    private static readonly Lock addedLock = new();

    /// <summary>Every project there is, Hex's own and then what extensions added.</summary>
    public static IReadOnlyList<ProjectTemplate> All
    {
        get
        {
            lock (addedLock)
                return added.Count == 0 ? BuiltIn : [.. BuiltIn, .. added];
        }
    }

    /// <summary>
    /// An extension's project added, or put in place of the one of the same id it added before (an extension started
    /// again says them all again). Why it wasn't back, null when it was: an id of Hex's own or another extension's.
    /// </summary>
    public static string? Add(ProjectTemplate template)
    {
        if (template.BuiltIn || template.Source is null)
            return "a project of an extension's needs its owner and what it's made of";
        if (BuiltIn.Any(each => each.Id.Equals(template.Id, StringComparison.OrdinalIgnoreCase)))
            return $"{template.Id} is one of Hex's own projects";

        lock (addedLock)
        {
            int at = added.FindIndex(each => each.Id.Equals(template.Id, StringComparison.OrdinalIgnoreCase));
            if (at >= 0 && added[at].Owner != template.Owner)
                return $"{template.Id} is a project of another extension's";
            if (at >= 0)
                added[at] = template;
            else
                added.Add(template);
        }

        return null;
    }

    /// <summary>Every project an extension added taken away, as it's switched off or started again.</summary>
    public static void Forget(string owner)
    {
        lock (addedLock)
            added.RemoveAll(each => each.Owner == owner);
    }

    /// <summary>A project by its id, null for none.</summary>
    public static ProjectTemplate? Find(string id) => All.FirstOrDefault(each => each.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>What runs Python here, python on Windows (the launcher's alias) and python3 everywhere else.</summary>
    public static string PythonCommand => OperatingSystem.IsWindows() ? "python" : "python3";

    /// <summary>
    /// Whether a name will do for a project, and its folder. A letter first, then letters, digits, dashes, underscores and
    /// dots, which every one of the tools takes (a crate, a module, an assembly) once it's been made lowercase or the like.
    /// </summary>
    public static ProjectNameProblem Check(string name)
    {
        if (name.Length == 0)
            return ProjectNameProblem.Empty;
        if (name.Length > LONGEST_NAME)
            return ProjectNameProblem.TooLong;
        if (!char.IsAsciiLetter(name[0]))
            return ProjectNameProblem.BadStart;
        foreach (char c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))
                return ProjectNameProblem.BadCharacter;
        }

        // A dot at the end is a name Windows quietly drops
        return name.EndsWith('.') ? ProjectNameProblem.BadCharacter : ProjectNameProblem.None;
    }

    /// <summary>The name as a Python module or a Rust crate wants it, lowercase with underscores.</summary>
    public static string Module(string name) => Lowered(name, '_');

    /// <summary>The name as a Java package, lowercase letters and digits only.</summary>
    public static string Package(string name)
    {
        var made = new StringBuilder();
        foreach (char c in name)
        {
            if (char.IsAsciiLetterOrDigit(c))
                made.Append(char.ToLowerInvariant(c));
        }

        return made.Length > 0 ? made.ToString() : "app";
    }

    // Lowercase, anything that isn't a letter or digit the separator given
    private static string Lowered(string name, char separator)
    {
        var made = new StringBuilder(name.Length);
        foreach (char c in name)
            made.Append(char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : separator);
        return made.ToString();
    }

    /// <summary>
    /// A project's files and commands, an extension's asked for there and then, so on the editor's thread. Hex's own are
    /// worked out from the name alone and can be asked for anywhere.
    /// </summary>
    public static ProjectContents Contents(ProjectTemplate template, string name, bool optional) =>
        new(Files(template, name), Commands(template, name, optional));

    /// <summary>
    /// The name put into what an extension's project says, {{name}} as it was typed, {{module}} as a Python module or a
    /// crate wants it, {{package}} as a Java package and {{lower}} lowercase with dashes.
    /// </summary>
    public static string Fill(string text, string name) => text
        .Replace("{{name}}", name, StringComparison.Ordinal)
        .Replace("{{module}}", Module(name), StringComparison.Ordinal)
        .Replace("{{package}}", Package(name), StringComparison.Ordinal)
        .Replace("{{lower}}", Lowered(name, '-'), StringComparison.Ordinal);

    /// <summary>The file opened once a project's made, relative to its folder with forward slashes.</summary>
    public static string MainFile(ProjectTemplate template, string name) =>
        template.Source is not null ? Fill(template.Main, name) : template.Main.Replace("{name}", name).Replace("{module}", Module(name)).Replace("{package}", Package(name));

    /// <summary>
    /// The commands that make a project, each the program and its arguments, run one after the other in its folder.
    /// The step that can be left out (a venv) is in them when it's wanted.
    /// </summary>
    public static IReadOnlyList<string[]> Commands(ProjectTemplate template, string name, bool optional)
    {
        if (template.Source is { } source)
            return source.Commands(name, optional);

        var commands = new List<string[]>();
        string? dotnet = template.Id switch
        {
            "csharp-console" => "console",
            "csharp-classlib" => "classlib",
            "csharp-xunit" => "xunit",
            _ => null
        };

        // A Horizon game is files of ours, the engine beside it cloned when there's no checkout of it on the machine
        // to point at. Its solution is one of its files (Solution)
        if (template.Id == "csharp-horizon" && HorizonEngine() is null)
            commands.Add(["git", "clone", "--depth", "1", HORIZON_REPOSITORY, "external/horizon"]);

        // The SDK's template writes the project, round the solution that is there already as one of the files
        if (dotnet is not null)
            commands.Add(["dotnet", "new", dotnet, "--name", name, "--output", "."]);

        if (optional && template.Optional.Length > 0 && template.Id == "python")
            commands.Add([template.Optional, "-m", "venv", ".venv"]);

        return commands;
    }

    /// <summary>The files a project is made of, their paths relative to its folder with forward slashes, and their text.</summary>
    public static IReadOnlyList<(string Path, string Text)> Files(ProjectTemplate template, string name) => template.Source is { } source ? source.Files(name) : template.Id switch
    {
        "python" => Python(name),
        "java-maven" => Java(name),
        "cpp-cmake" => Cpp(name),
        "rust" => Rust(name),
        "go" => Go(name),
        "typescript" => TypeScript(name),
        "d-dub" => D(name),
        "csharp-horizon" => [.. HorizonGame(name, HorizonEngine()), Solution(name)],

        // The SDK's templates write the rest, all we add is what git is told to leave alone and the solution
        _ => [(".gitignore", "bin/\nobj/\n.vs/\n*.user\n"), Solution(name)]
    };

    /// <summary>
    /// A project's solution, which the Solution view and csharp-ls both want, written as a file rather than made with
    /// <c>dotnet new sln</c> and <c>dotnet sln add</c>: the adding once came back saying nothing was wrong having added
    /// nothing, and a solution with no project in it leaves the language server with nothing to complete from. An
    /// .slnx with one project is these three lines, and written this way it has only the project, not the engine's
    /// projects it refers to as well.
    /// </summary>
    private static (string, string) Solution(string name) =>
        ($"{name}.slnx", $"<Solution>\n  <Project Path=\"{System.Security.SecurityElement.Escape(name)}.csproj\" />\n</Solution>\n");

    private static List<(string, string)> Python(string name)
    {
        string module = Module(name);
        return
        [
            ("pyproject.toml", $$"""
                [project]
                name = "{{name}}"
                version = "0.1.0"
                requires-python = ">=3.10"
                dependencies = []

                [project.scripts]
                {{name}} = "{{module}}.__main__:main"

                [build-system]
                requires = ["setuptools>=68"]
                build-backend = "setuptools.build_meta"

                [tool.setuptools.packages.find]
                where = ["src"]

                [tool.pyright]
                include = ["src"]
                venvPath = "."
                venv = ".venv"

                """),
            ($"src/{module}/__init__.py", string.Empty),
            ($"src/{module}/__main__.py", $$"""
                def main() -> None:
                    print("Hello from {{name}}!")


                if __name__ == "__main__":
                    main()

                """),
            ("README.md", $"# {name}\n"),
            (".gitignore", ".venv/\n__pycache__/\n*.egg-info/\ndist/\nbuild/\n")
        ];
    }

    private static List<(string, string)> Java(string name)
    {
        string package = Package(name);
        return
        [
            ("pom.xml", $$"""
                <?xml version="1.0" encoding="UTF-8"?>
                <project xmlns="http://maven.apache.org/POM/4.0.0"
                         xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                         xsi:schemaLocation="http://maven.apache.org/POM/4.0.0 https://maven.apache.org/xsd/maven-4.0.0.xsd">
                    <modelVersion>4.0.0</modelVersion>

                    <groupId>{{package}}</groupId>
                    <artifactId>{{name}}</artifactId>
                    <version>0.1.0</version>
                    <packaging>jar</packaging>

                    <properties>
                        <maven.compiler.release>21</maven.compiler.release>
                        <project.build.sourceEncoding>UTF-8</project.build.sourceEncoding>
                        <exec.mainClass>{{package}}.App</exec.mainClass>
                    </properties>
                </project>

                """),
            ($"src/main/java/{package}/App.java", $$"""
                package {{package}};

                public class App {
                    public static void main(String[] args) {
                        System.out.println("Hello from {{name}}!");
                    }
                }

                """),
            (".gitignore", "target/\n.classpath\n.project\n.settings/\n"),
        ];
    }

    private static List<(string, string)> Cpp(string name) =>
    [
        // compile_commands.json is what clangd reads a CMake project's flags out of
        ("CMakeLists.txt", $$"""
            cmake_minimum_required(VERSION 3.20)
            project({{Lowered(name, '_')}} LANGUAGES CXX)

            set(CMAKE_CXX_STANDARD 20)
            set(CMAKE_CXX_STANDARD_REQUIRED ON)
            set(CMAKE_EXPORT_COMPILE_COMMANDS ON)

            add_executable({{name}} src/main.cpp)

            """),
        ("src/main.cpp", $$"""
            #include <iostream>

            int main()
            {
                std::cout << "Hello from {{name}}!\n";
                return 0;
            }

            """),
        (".gitignore", "build/\nout/\n.cache/\n")
    ];

    private static List<(string, string)> Rust(string name) =>
    [
        ("Cargo.toml", $$"""
            [package]
            name = "{{Lowered(name, '-')}}"
            version = "0.1.0"
            edition = "2021"

            [dependencies]

            """),
        ("src/main.rs", $$"""
            fn main() {
                println!("Hello from {{name}}!");
            }

            """),
        (".gitignore", "/target\n")
    ];

    private static List<(string, string)> Go(string name) =>
    [
        ("go.mod", $"module {Lowered(name, '-')}\n\ngo 1.22\n"),
        ("main.go", $$"""
            package main

            import "fmt"

            func main() {
            	fmt.Println("Hello from {{name}}!")
            }

            """)
    ];

    private static List<(string, string)> TypeScript(string name) =>
    [
        ("package.json", $$"""
            {
              "name": "{{Lowered(name, '-')}}",
              "version": "0.1.0",
              "private": true,
              "type": "module",
              "scripts": {
                "build": "tsc",
                "start": "node dist/index.js"
              },
              "devDependencies": {
                "typescript": "^5.6.0"
              }
            }

            """),
        ("tsconfig.json", """
            {
              "compilerOptions": {
                "target": "ES2022",
                "module": "NodeNext",
                "moduleResolution": "NodeNext",
                "outDir": "dist",
                "rootDir": "src",
                "strict": true,
                "esModuleInterop": true,
                "skipLibCheck": true
              },
              "include": ["src"]
            }

            """),
        ("src/index.ts", $$"""
            const name: string = "{{name}}";
            console.log(`Hello from ${name}!`);

            """),
        (".gitignore", "node_modules/\ndist/\n")
    ];

    /// <summary>Where the engine is on GitHub, cloned into a Horizon game that has no checkout of it to point at.</summary>
    public const string HORIZON_REPOSITORY = "https://github.com/bogzxo/horizon";

    /// <summary>
    /// A checkout of the Horizon engine on this machine, the folder with Horizon/Horizon.csproj in it with a separator on
    /// the end, null for none. HORIZON_ROOT says where when it's set, otherwise it's the folder over the one Hex runs
    /// from, a Hex built in the workspace of the engine and Hex together finding the workspace's engine.
    /// </summary>
    public static string? HorizonEngine()
    {
        static string? Engine(string folder) =>
            File.Exists(Path.Combine(folder, "Horizon", "Horizon.csproj")) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar : null;

        if (Environment.GetEnvironmentVariable("HORIZON_ROOT") is { Length: > 0 } said && Engine(said) is { } named)
            return named;

        for (DirectoryInfo? at = new(AppContext.BaseDirectory); at is not null; at = at.Parent)
        {
            if (Engine(Path.Combine(at.FullName, "horizon")) is { } beside)
                return beside;
        }

        return null;
    }

    /// <summary>The name as a C# namespace, what isn't a letter, digit or dot an underscore and no part starting with a digit.</summary>
    public static string Namespace(string name) => string.Join('.', name.Split('.').Select(part =>
    {
        string made = part.Length > 0 ? new string([.. part.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_')]) : "_";
        return char.IsAsciiDigit(made[0]) ? "_" + made : made;
    }));

    // A Horizon game, the engine's own projects referenced from its checkout and a scene of one line of text. HorizonRoot
    // is the workspace's when the game is made in it (its Directory.Build.props says), the checkout found when there is
    // one, and the clone in external/horizon when there isn't
    private static List<(string, string)> HorizonGame(string name, string? engine)
    {
        string space = Namespace(name);
        string root = engine ?? @"$(MSBuildThisFileDirectory)external\horizon\";
        return
        [
            ($"{name}.csproj", $$"""
                <Project Sdk="Microsoft.NET.Sdk">

                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net11.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                    <RootNamespace>{{space}}</RootNamespace>

                    <!-- Where the Horizon engine is, a workspace's Directory.Build.props can say another -->
                    <HorizonRoot Condition="'$(HorizonRoot)' == ''">{{root}}</HorizonRoot>
                  </PropertyGroup>

                  <ItemGroup>
                    <ProjectReference Include="$(HorizonRoot)Horizon\Horizon.csproj" />
                    <ProjectReference Include="$(HorizonRoot)Horizon.Platform.Desktop\Horizon.Platform.Desktop.csproj" />
                    <ProjectReference Include="$(HorizonRoot)Horizon.UI\Horizon.UI.csproj" />
                  </ItemGroup>

                </Project>

                """),
            ("Program.cs", $$"""
                using Horizon.Core;
                using Horizon.Engine;

                using {{space}};

                // The engine's skins and shaders are next to the exe, and dotnet run starts it in the project's folder
                Directory.SetCurrentDirectory(AppContext.BaseDirectory);

                // A desktop window, said before the engine is made
                Horizon.Platform.Desktop.DesktopPlatform.Install();

                using var engine = new GameEngine(WindowManagerConfiguration.Default1600x900 with { WindowTitle = "{{name}}" });
                engine.Run<MainScene>();

                """),
            ("MainScene.cs", $$"""
                using System.Numerics;

                using Horizon.Engine;
                using Horizon.Rendering;
                using Horizon.UI;
                using Horizon.UI.Components;

                namespace {{space}};

                /// <summary>The first scene, a line of text in the middle of the window. Your game starts here.</summary>
                public sealed class MainScene : Scene
                {
                    public override void Initialize()
                    {
                        Engine.Graphics.ClearColor = new Vector4(0.09f, 0.1f, 0.14f, 1.0f);

                        // A UI the size of the window, and a label in the middle of it
                        UICompositor ui = AddComponent(UICompositor.ForScreen());
                        UIModule screen = ui.CreateModule();
                        screen.AddComponent(new Label("Goodbye, sunshine!") { Anchor = Origin.Center, Pivot = Origin.Center, TextScale = 0.45f });

                        base.Initialize();
                    }
                }

                """),
            (".gitignore", "bin/\nobj/\n.vs/\n*.user\nexternal/\n")
        ];
    }

    private static List<(string, string)> D(string name) =>
    [
        ("dub.json", $$"""
            {
                "name": "{{Lowered(name, '-')}}",
                "targetType": "executable"
            }

            """),
        ("source/app.d", $$"""
            import std.stdio;

            void main()
            {
                writeln("Hello from {{name}}!");
            }

            """),
        (".gitignore", ".dub/\n*.exe\n*.o\n*.obj\n")
    ];
}
