using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;

namespace Hex.Core;

/// <summary>
/// What F5 and the Run button run in a workspace, picked from the arrow beside the button and kept with the workspace. A
/// run task of a tasks.hor (its folder and its name) or a .NET project that makes a program (its file). None at all is
/// the file on screen, the way F5 always went.
/// </summary>
internal sealed record HexRunTarget(string Kind, string Path, string Name)
{
    /// <summary>What <see cref="Kind"/> says for a task and for a project.</summary>
    public const string TASK = "task", PROJECT = "project";

    /// <summary>Read out of what a workspace file says, null for anything that isn't one.</summary>
    public static HexRunTarget? From(IRuntimeValue? said, string relativeTo)
    {
        if (said is not ObjectValue { Properties: var values }
            || values.GetValueOrDefault("kind") is not StringValue { Value: TASK or PROJECT } kind
            || values.GetValueOrDefault("path") is not StringValue { Value.Length: > 0 } path)
            return null;

        string full = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(path.Value) ? path.Value : System.IO.Path.Combine(relativeTo, path.Value));
        return new HexRunTarget(kind.Value, full, values.GetValueOrDefault("name") is StringValue name ? name.Value : string.Empty);
    }

    /// <summary>As a workspace file says it, the path relative to a folder when it's on the same drive.</summary>
    public ObjectValue Said(string? relativeTo)
    {
        string path = relativeTo is not null && string.Equals(System.IO.Path.GetPathRoot(Path), System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(relativeTo)), PlatformPaths.Comparison)
            ? System.IO.Path.GetRelativePath(relativeTo, Path)
            : Path;
        return Natives.Object(("kind", new StringValue(Kind)), ("path", new StringValue(path.Replace('\\', '/'))), ("name", new StringValue(Name)));
    }

    /// <summary>Whether two say the same, the paths compared the way the system does.</summary>
    public bool Same(HexRunTarget? other) =>
        other is not null && other.Kind == Kind && other.Name == Name && PlatformPaths.Same(other.Path, Path);
}
