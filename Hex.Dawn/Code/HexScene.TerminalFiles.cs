using System.Text;

using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// Dusk's files known for what they are. A settings.hor, a keys.hor or a plugin of the terminal's (by its folder, by its
// name, or by #define dusk at its top) gets the terminal's own completion, hover, signatures and checks on top of what
// any HIDL file has, out of the description the hosts write (api.hor, the engine's TerminalApiDescription), which says
// what Dusk has as well as what Hex has. The knowing is the engine's (TerminalFileKnowledge), this only puts it through
// the editor's seams.
internal sealed partial class HexScene
{
    private TerminalFileKnowledge? terminalKnowledge;
    private DateTime terminalKnowledgeFrom;

    /// <summary>What the terminal's files are known by, read again when api.hor has been written since.</summary>
    private TerminalFileKnowledge TerminalKnowledge
    {
        get
        {
            string api = Path.Combine(TerminalFiles.UserFolder, TerminalApiDescription.FILE);
            DateTime written = File.Exists(api) ? File.GetLastWriteTimeUtc(api) : default;
            if (terminalKnowledge is null || written != terminalKnowledgeFrom)
            {
                terminalKnowledgeFrom = written;
                terminalKnowledge = new TerminalFileKnowledge(terminals.Describe());
            }

            return terminalKnowledge;
        }
    }

    /// <summary>Which of the terminal's files a document is, none for every other.</summary>
    private TerminalFileKind TerminalKindOf(HexDocument of) =>
        of.IsText && of.Path is { } path ? TerminalFileKnowledge.KindOf(path, of.Editor.Text) : TerminalFileKind.None;

    /// <summary>What there is to write at the caret of a terminal file, null to leave it to the editor's own.</summary>
    private UICompletionRequest? TerminalCompletions(HexDocument of, CodeEditor editor)
    {
        TerminalFileKind kind = TerminalKindOf(of);
        if (kind == TerminalFileKind.None)
            return null;

        UITextPosition caret = editor.Document.Caret;
        string before = editor.Document.LineAt(caret.Line)[..caret.Column];
        if (TerminalKnowledge.Complete(kind, before) is not var (start, items))
            return null;

        return new UICompletionRequest(start, [.. items.Select(item => new UICompletion(item.Label, item.Detail, item.Insert))]);
    }

    /// <summary>What a word of a terminal file is, null to leave it to the editor's own.</summary>
    private string? TerminalHover(HexDocument of, CodeEditor editor, UITextPosition at)
    {
        TerminalFileKind kind = TerminalKindOf(of);
        if (kind == TerminalFileKind.None)
            return null;

        var (start, end) = editor.Document.WordAt(at);
        return TerminalKnowledge.Hover(kind, editor.Document.LineAt(at.Line), start, end);
    }

    /// <summary>What a call on the terminal object takes, with the argument being typed picked out, null for any other call.</summary>
    private UISignature? TerminalSignature(HexDocument of, string name, int argument)
    {
        if (TerminalKindOf(of) != TerminalFileKind.Plugin || TerminalKnowledge.Parameters(name) is not { } parameters)
            return null;

        var text = new StringBuilder(name).Append('(');
        int start = 0, length = 0;
        for (int i = 0; i < parameters.Length; i++)
        {
            if (i > 0) text.Append(", ");
            if (i == argument)
            {
                start = text.Length;
                length = parameters[i].Length;
            }
            text.Append(parameters[i]);
        }

        return new UISignature(text.Append(')').ToString(), start, length);
    }

    /// <summary>What is wrong with a terminal file, as warnings under its code, nothing for every other file.</summary>
    private IEnumerable<UICodeDiagnostic> TerminalProblemsOf(HexDocument of)
    {
        TerminalFileKind kind = TerminalKindOf(of);
        if (kind == TerminalFileKind.None)
            return [];

        return TerminalKnowledge.Check(kind, of.Editor.Text)
            .Select(problem => new UICodeDiagnostic(problem.Line, Math.Max(0, problem.Column), Math.Max(1, problem.Length), problem.Message, Warning: problem.Warning));
    }

    /* What an extension is handed about the terminal's files, the settings form (Assets/hex/extensions/dusk_settings) */

    private (string, IRuntimeValue)[] TerminalFilesApi() =>
    [
        ("terminal_settings", Natives.Function("terminal_settings", args => SettingsFormOf(args.Has(0) ? args.Text(0) : document.Path))),
        // a field's value as the file writes it, the flags of a list back into a list
        ("terminal_setting_value", Natives.Function("terminal_setting_value", args => TerminalSettingsForm.ValueOf(args.Text(0), args[1])))
    ];

    /// <summary>
    /// The settings of the terminal as a form takes them, the engine's description of them (the same form Dusk's
    /// settings window is), with what the file says of each as its values.
    /// </summary>
    private IRuntimeValue SettingsFormOf(string? file)
    {
        string text = file is not null && documents.Find(open => open.Path is { } path && string.Equals(Path.GetFullPath(path), Path.GetFullPath(file), PlatformPaths.Comparison)) is { } open
            ? open.Editor.Text
            : file is not null && File.Exists(file) ? File.ReadAllText(file) : string.Empty;

        Dictionary<string, IRuntimeValue> said;
        try
        {
            said = TerminalSettings.ReadObject(text);
        }
        catch (Exception)
        {
            said = [];
        }

        return TerminalSettingsForm.Script(terminals.Plugins.Where(plugin => !plugin.BuiltIn).Select(plugin => plugin.Id), terminals.Panel.Profiles.Select(profile => profile.Id), said);
    }
}
