using System.Numerics;
using System.Text;

using Horizon.HIDL;
using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Horizon.Hex;

// A layout out of its code and back into it, the stand-ins for what a program fills in, and a component's properties
// read and written the way a script would.
internal sealed partial class HexDocument
{
    /// <summary>
    /// Replaces the layout with what a script builds. The names the script gave its components are kept.
    /// </summary>
    /// <param name="path">Where the script is from, which is where its templates are looked for.</param>
    /// <returns>Whether the script ran, and what it had to say if it didn't.</returns>
    public (bool Success, string Message) Load(string code, string? path = null, bool keepHistory = false)
    {
        Clear();

        var result = Build(code, path);

        // A layout that was just opened has nothing to go back to
        if (!keepHistory)
            ResetHistory();

        return result;
    }

    // Where the code was said to be from when it isn't a file of its own yet (a round trip check), for its templates and includes
    private string? builtFrom;

    private (bool Success, string Message) Build(string code, string? path)
    {
        builtFrom = path;

        try
        {
            // The same way a program loads it, so what the editor shows is what the program will get
            var layout = UILayout.LoadCode(Module, code, null, path ?? string.Empty);

            // What an included file made has names of its own in that file, not this one
            foreach (var (name, component) in layout.Parts)
            {
                if (component.Module == Module && !InsideInclude(component))
                    names.Set(component, name);
            }
        }
        catch (Exception e)
        {
            return (false, e.Message);
        }

        // Whatever the script made without keeping it in a variable still needs a name to be written under.
        foreach (var (component, _) in Walk())
            NameOf(component);

        return (true, RefreshPreviews() ?? string.Empty);
    }

    /// <summary>
    /// Shows every container that names a template with as many items made from it as it asks to be previewed
    /// with, in place of the ones that were shown before.
    /// </summary>
    /// <returns>What was wrong with a template, null if nothing was.</returns>
    public string? RefreshPreviews()
    {
        foreach (var preview in previews)
            preview.Parent?.Remove(preview);
        previews.Clear();

        string directory = (Path ?? builtFrom) is { Length: > 0 } from
            ? System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(from)) ?? string.Empty
            : FallbackDirectory;

        string? problem = null;
        foreach (var (component, _) in Walk().ToArray())
        {
            if (component is Include include)
                problem = FillInclude(include, directory) ?? problem;
            else if (component is Panel { Template.Length: > 0, PreviewCount: > 0 } container)
                problem = FillPreview(container, directory, 0) ?? problem;
        }

        return problem;
    }

    // What an include builds is shown and not edited, stand-ins the way a template's items are, the file it names is
    // edited on its own
    private string? FillInclude(Include include, string directory)
    {
        for (int i = include.Children.Count - 1; i >= 0; i--)
            include.Remove(include.Children[i]);

        if (include.File.Length == 0)
            return null;

        string file = System.IO.Path.Combine(directory, include.File);
        if (!File.Exists(file))
            return $"the included '{include.File}' isn't next to the layout";

        string? problem = null;
        try
        {
            UILayout inner = UILayout.Load(Module, file, include);
            foreach (var root in inner.Roots)
                previews.Add(root);

            // What the included file fills in from templates is shown filled too, the way its own designer would
            string innerDirectory = System.IO.Path.GetDirectoryName(file) ?? string.Empty;
            foreach (var part in inner.Parts.Values)
            {
                if (part is Panel { Template.Length: > 0, PreviewCount: > 0 } nested && inner.OwnerOf(nested) == inner)
                    problem = FillPreview(nested, innerDirectory, 1) ?? problem;
            }
        }
        catch (Exception e)
        {
            return e.Message;
        }

        return problem;
    }

    /// <summary>Whether a component is inside of an include, part of another file.</summary>
    public static bool InsideInclude(UIComponent component)
    {
        for (UIComponent? at = component.Parent; at is not null; at = at.Parent)
        {
            if (at is Include)
                return true;
        }

        return false;
    }

    private string? FillPreview(Panel container, string directory, int depth)
    {
        string file = System.IO.Path.Combine(directory, container.Template);
        if (!File.Exists(file))
            return $"the template '{container.Template}' isn't next to the layout";

        for (int i = 0; i < Math.Min(container.PreviewCount, MAX_PREVIEW); i++)
        {
            UILayout item;
            try
            {
                // One after the other if the container says so, the way a program filling it would have them
                item = UILayout.Load(Module, file, container, i * container.Stagger);
            }
            catch (Exception e)
            {
                return e.Message;
            }

            foreach (var root in item.Roots)
            {
                // Only the top of an item is noted, what is inside of it goes wherever it goes
                if (depth == 0)
                    previews.Add(root);
            }

            // An item can have containers of its own to fill, but a template that names itself would never end
            if (depth >= 2)
                continue;

            string itemDirectory = System.IO.Path.GetDirectoryName(file) ?? string.Empty;
            foreach (var part in item.Parts.Values)
            {
                if (part is Panel { Template.Length: > 0, PreviewCount: > 0 } nested)
                    FillPreview(nested, itemDirectory, depth + 1);
            }
        }

        return null;
    }

    /// <summary>
    /// Writes the layout as the script that builds it. One component after the other, each inside of the one
    /// before it that it belongs to, with everything about it that isn't how it starts out anyway.
    /// </summary>
    public string GenerateCode()
    {
        // A file of code is what is in its editor, there is no layout to write it out of
        if (IsText)
            return Editor.Text;

        var code = new StringBuilder();
        code.Append("// ").Append(Name).AppendLine(", laid out with Horizon.Hex.");
        code.AppendLine("// Load it with UILayout.Load, every component is there under the name it has here.");

        // What says it is a layout, which is what an editor opens it with its designer by
        code.Append('#').Append("define ").AppendLine(Horizon.HIDL.Preprocessing.HIDLPreprocessor.LAYOUT);
        code.AppendLine();
        code.Append(GenerateBody());

        return code.ToString();
    }

    // The components without the lines on top, which say what the layout is called and nothing about what is in it.
    private string GenerateBody()
    {
        var code = new StringBuilder();

        if (Design is { } design)
        {
            code.AppendLine();
            code.Append("compositor.design({ size: ").Append(HIDLWriter.Write(new Vector2Value(design)))
                .Append(", fit: \"").Append(Fit.ToString().ToLowerInvariant()).AppendLine("\" });");
        }

        foreach (var (component, _) in Walk())
        {
            code.AppendLine();
            WriteComponent(code, component);
        }

        // The layers hidden in the designer are the ones the layout starts with hidden, in the order the layout names them
        string[] hidden = [.. Module.Layers.Where(Module.IsLayerHidden)];
        if (hidden.Length > 0)
        {
            code.AppendLine();
            code.Append("compositor.hidden_layers(").Append(HIDLWriter.Write(new ListValue([.. hidden.Select(layer => (IRuntimeValue)new StringValue(layer))]))).AppendLine(");");
        }

        return code.ToString();
    }

    private void WriteComponent(StringBuilder code, UIComponent component, Func<UIComponent, string>? nameOf = null, UIComponent? top = null)
    {
        nameOf ??= NameOf;

        string? kind = UIModule.KindOf(component);
        if (kind is null)
        {
            code.Append("// A ").Append(component.GetType().Name).AppendLine(" can't be made by a script, it was left out.");
            return;
        }

        var properties = new List<string>();

        if (component != top && component.Parent is { } parent && parent != Module.Root)
            properties.Add($"parent: {nameOf(parent)}");

        // Only what was changed is written, against a component of the same kind nobody has touched
        Dictionary<string, string> usual = UsualOf(kind);
        bool hasHandlers = false;

        foreach (var (name, value) in ReadProperties(component))
        {
            if (value is FunctionValue or AnonymousFunctionValue or NativeFunctionValue)
            {
                hasHandlers = true;
                continue;
            }

            if (!HIDLWriter.CanWrite(value))
                continue;

            string written = HIDLWriter.Write(value);
            if (usual.TryGetValue(name, out string? untouched) && untouched == written)
                continue;

            properties.Add($"{name}: {written}");
        }

        if (hasHandlers)
            code.AppendLine("// The handlers this component had were functions, which can't be written back out.");

        code.Append("let ").Append(nameOf(component)).Append(" = compositor.").Append(kind).Append('(');

        if (properties.Count == 0)
        {
            code.AppendLine(");");
        }
        else if (properties.Count <= 2 && properties.Sum(property => property.Length) < 70)
        {
            code.Append("{ ").Append(string.Join(", ", properties)).AppendLine(" });");
        }
        else
        {
            code.AppendLine("{");
            for (int i = 0; i < properties.Count; i++)
                code.Append("    ").Append(properties[i]).AppendLine(i < properties.Count - 1 ? "," : string.Empty);
            code.AppendLine("});");
        }
    }

    // What a component of a kind nobody has touched writes every property as, by kind. It was a new component made and
    // every property of it written out, for every component, every time a layout was written, which is every tick of a drag
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Dictionary<string, string>> usualByKind = new();

    private static Dictionary<string, string> UsualOf(string kind) => usualByKind.GetOrAdd(kind, static made =>
    {
        var usual = new Dictionary<string, string>(StringComparer.Ordinal);
        if (UIModule.CreateDetached(made) is not { } untouched)
            return usual;

        foreach (var (name, value) in ReadProperties(untouched))
        {
            if (HIDLWriter.CanWrite(value))
                usual[name] = HIDLWriter.Write(value);
        }

        return usual;
    });

    /// <summary>
    /// Everything about a component a script can set, by name, as it is right now. This is what the inspector
    /// shows and what the code is written from.
    /// </summary>
    public static IEnumerable<(string Name, IRuntimeValue Value)> ReadProperties(UIComponent component)
    {
        foreach (var (name, property) in component.Object.Properties)
        {
            if (Unwritten.Contains(name) || property is not NativeValue native)
                continue;

            IRuntimeValue value;
            try
            {
                value = native.AccessorCallback();
            }
            catch
            {
                continue;
            }

            yield return (name, value);
        }
    }

    public static bool TryRead(UIComponent component, string name, out IRuntimeValue value)
    {
        if (component.Object.Properties.TryGetValue(name, out var property) && property is NativeValue native)
        {
            value = native.AccessorCallback();
            return true;
        }

        value = new NullValue();
        return false;
    }

    /// <summary>
    /// Sets a property of a component the way a script would.
    /// </summary>
    /// <returns>What went wrong, null if nothing did.</returns>
    public static string? Write(UIComponent component, string name, IRuntimeValue value)
    {
        if (!component.Object.Properties.TryGetValue(name, out var property) || property is not NativeValue native)
            return $"There is no property called '{name}'.";

        try
        {
            native.MutatorCallback(value);
            return null;
        }
        catch (Exception e)
        {
            return e.Message;
        }
    }
}
