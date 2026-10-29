using Hex.Installation;

namespace Hex.Installer;

internal sealed record SetupOptions(string Target, bool Uninstall, bool Update, string? Version, int? Parent, long ParentStart, bool Preview, string? Package = null)
{
    public static SetupOptions Parse(string[] args)
    {
        string target = InstallPaths.DefaultRoot;
        string? version = null;
        string? package = null;
        bool uninstall = false, update = false, preview = false;
        int? parent = null;
        long start = 0;
        for (int i = 0; i < args.Length; i++)
        {
            string Value() => ++i < args.Length ? args[i] : throw new ArgumentException(InstallText.Get("errors.argument_value"));
            switch (args[i])
            {
                case "--target": target = Path.GetFullPath(Value()); break;
                case "--uninstall": uninstall = true; break;
                case "--update": update = true; break;
                case "--version": version = Value(); break;
                case "--package": package = Path.GetFullPath(Value()); break;
                case "--wait-pid": parent = int.Parse(Value()); break;
                case "--wait-start": start = long.Parse(Value()); break;
                case "--preview": preview = true; break;
                default: throw new ArgumentException(InstallText.Get("errors.option", ("option", args[i])));
            }
        }
        if (uninstall && update || parent.HasValue && (parent <= 0 || start <= 0) || update && (parent is null || version is null) || package is not null && !update)
            throw new ArgumentException(InstallText.Get("errors.handoff"));
        if (target.Contains('\n') || target.Contains('\r') || target.Contains('"')) throw new ArgumentException(InstallText.Get("errors.destination"));
        if (update || uninstall) _ = InstallTransaction.ReadManifest(target);
        return new(target, uninstall, update, version, parent, start, preview, package);
    }
}
