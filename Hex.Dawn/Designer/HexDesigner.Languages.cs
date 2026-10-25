using System.Text;

using Horizon.Content;
using Horizon.HIDL.Runtime;

namespace Hex.Dawn;

// A game's texts written into its language file from the canvas, each a step of the designer's undo of its own, and
// going from a component to where it's declared in the code (F12) or to the key its text names in the language (Ctrl+F12).
internal sealed partial class HexDesigner
{
    // A text written into a language, what the key said before (null for a key that wasn't there) and after, and the
    // layout as it was noted at the time (HexDocument.Recorded, the same string until the layout changes and the same
    // one again once it's undone back to there), which is where it goes among the layout's own steps
    private readonly record struct TextStep(HexDocument Layout, string At, string Folder, string Language, string Key, string? Was, string Now);

    private readonly List<TextStep> textsDone = [], textsUndone = [];

    /// <summary>Whether Ctrl+Z in the designer is a text of the language put back rather than a step of the layout.</summary>
    internal bool CanUndoText => textsDone.Count > 0 && Due(textsDone[^1]);

    /// <summary>Whether Ctrl+Y in the designer writes a text that was put back again.</summary>
    internal bool CanRedoText => textsUndone.Count > 0 && Due(textsUndone[^1]);

    // A step is the next one for the layout on screen when the layout is as it was when the text was written, nothing of
    // its own has come since or it's been undone back to there
    private bool Due(TextStep step) => step.Layout == document && ReferenceEquals(step.At, document.Recorded) && !recordPending;

    /// <summary>
    /// Writes what a key of the game's language says into the language's file, a step to undo. A key the file hasn't got
    /// is put in, in its section.
    /// </summary>
    internal void WriteKeyText(string key, string text)
    {
        // A change of the layout that hasn't settled into a step yet is one now, so it's undone after this and not before
        if (recordPending)
        {
            recordPending = false;
            document.Record();
        }

        string folder = Localisation.Folder, language = Localisation.Language;
        string? was = Localisation.Has(key) ? Localisation.Get(key) : null;
        if (!WriteLanguage(folder, language, key, text))
            return;

        textsDone.Add(new TextStep(document, document.Recorded, folder, language, key, was, text));
        textsUndone.Clear();

        string file = language + ".hor";
        context.Notices.Say(was is null
            ? context.Strings.Said("designer_texts.key_added", ("key", key), ("file", file), ("text", text))
            : context.Strings.Said("designer_texts.key_written", ("key", key), ("file", file), ("text", text)));
    }

    /// <summary>Ctrl+Z, the last text written put back as it was. False when the next step back is the layout's.</summary>
    internal bool UndoText()
    {
        if (!CanUndoText)
            return false;

        TextStep step = textsDone[^1];
        if (WriteLanguage(step.Folder, step.Language, step.Key, step.Was))
        {
            textsDone.RemoveAt(textsDone.Count - 1);
            textsUndone.Add(step);
            context.Notices.Say(context.Strings.Said("designer_texts.key_undone", ("key", step.Key), ("file", step.Language + ".hor")));
        }

        return true;
    }

    /// <summary>Ctrl+Y, the last text put back written again. False when the next step forward is the layout's.</summary>
    internal bool RedoText()
    {
        if (!CanRedoText)
            return false;

        TextStep step = textsUndone[^1];
        if (WriteLanguage(step.Folder, step.Language, step.Key, step.Now))
        {
            textsUndone.RemoveAt(textsUndone.Count - 1);
            textsDone.Add(step);
            context.Notices.Say(context.Strings.Said("designer_texts.key_redone", ("key", step.Key), ("file", step.Language + ".hor"), ("text", step.Now)));
        }

        return true;
    }

    /// <summary>A step of the layout's own was noted, so a text that was undone before it has nothing left to come back after.</summary>
    private void ForgetUndoneTexts() => textsUndone.Clear();

    /// <summary>
    /// Helper method to change a key in a language's file (take it out for null), the rest of the file left as it was,
    /// and read the language again so the canvas says it. It's written next to the file and moved over it, so a write
    /// that fails halfway leaves the file whole. A file that's open in Hex with changes nobody saved isn't touched, Hex
    /// would only ask which of the two to keep. False with the reason said when it didn't go.
    /// </summary>
    private bool WriteLanguage(string folder, string language, string key, string? text)
    {
        if (folder.Length == 0 || language.Length == 0)
        {
            context.Notices.Say(context.Strings.Said("code_completion.no_language_loaded"), error: true);
            return false;
        }

        string file = Path.GetFullPath(Path.Combine(folder, language + ".hor"));
        string name = Path.GetFileName(file);
        if (context.Documents.Find(file) is { } open && DawnScene.HasChanges(open))
        {
            context.Notices.Say(context.Strings.Said("designer_texts.language_unsaved", ("file", name)), error: true);
            return false;
        }

        try
        {
            // A file that starts with the mark of UTF-8 keeps it
            byte[] bytes = File.ReadAllBytes(file);
            bool marked = bytes is [0xEF, 0xBB, 0xBF, ..];
            string source = Encoding.UTF8.GetString(bytes, marked ? 3 : 0, bytes.Length - (marked ? 3 : 0));

            string why;
            string? written = text is null ? HexLanguageEdits.RemoveText(source, file, key, out why) : HexLanguageEdits.SetText(source, file, key, text, out why);
            if (written is null)
            {
                context.Notices.Say(context.Strings.Said("designer_texts.not_written", ("key", key), ("file", name), ("why", why)), error: true);
                return false;
            }

            if (written != source)
            {
                string writing = file + ".hex-writing";
                File.WriteAllText(writing, written, new UTF8Encoding(marked));
                File.Move(writing, file, overwrite: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            context.Notices.Say(context.Strings.Said("designer_texts.not_written", ("key", key), ("file", name), ("why", e.Message)), error: true);
            return false;
        }

        // Read again only if it's still the one loaded, another layout may have brought in another game's since
        if (Localisation.Folder == folder && Localisation.Language == language)
            Localisation.Load(folder, language);

        return true;
    }

    /* Going from a component to where it comes from */

    /// <summary>The key of the language what's selected shows, its text or failing that its tooltip, null for a text written in the layout.</summary>
    internal string? SelectedKey()
    {
        if (document.Selected is not { } selected)
            return null;

        if (TextOf(selected) is { IsKey: true } slot)
            return slot.Key;

        return HexDocument.TryRead(selected, "tooltip", out IRuntimeValue tip) && tip is StringValue { Value: var said } && Localisation.IsKey(said) ? said[1..] : null;
    }

    /// <summary>
    /// Ctrl+F12, the language's file opened at the key the text of what's selected names. A text that's written in the
    /// layout itself is in the code, so that's where it goes then.
    /// </summary>
    internal void GoToSelectedText()
    {
        if (document.Selected is not { } selected)
            return;

        if (SelectedKey() is { } key)
        {
            window.GoToKey(key);
            return;
        }

        ShowSelectedCode();
        context.Notices.Say(context.Strings.Said("designer_texts.not_a_key", ("name", document.NameOf(selected))));
    }
}
