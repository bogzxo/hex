using System.Numerics;

using Horizon.HIDL.Runtime;

namespace Hex.Dawn;

/// <summary>
/// What of the extensions is on screen and is the editor's to put there: the views of a file next to its code, the pages
/// of the panel, the little windows, a note in the title bar, and the file on screen as an extension sees it. The scene
/// does these (HexScene.UIScriptedLayout), HexExtensions asks for them.
/// </summary>
internal interface IExtensionScreens
{
    /// <summary>A view of files an extension has, the one of the same id replaced.</summary>
    void AddView(ExtensionViewKind kind);

    /// <summary>A page of the panel out of an extension's layout, null (said) when it doesn't build.</summary>
    UIScriptedLayout? MakePanelPage(HexExtension owner, string title, string icon, string layout);

    /// <summary>A little window over everything out of an extension's layout, null (said) when it doesn't build.</summary>
    UIScriptedLayout? MakeWindow(HexExtension owner, string title, string layout, Vector2 size);

    /// <summary>What an extension has in the title bar after Hex, empty for nothing.</summary>
    void TitleNote(HexExtension by, string text);

    /// <summary>The note of an extension taken out of the title bar, if it has one there, as it is switched off.</summary>
    void ForgetTitleNote(HexExtension by);

    /// <summary>The file on screen as an extension sees it, its path, name, text, #defines and what it last came to.</summary>
    IRuntimeValue DocumentObject();

    /// <summary>
    /// An extension read again: what it put on screen comes down, <paramref name="start"/> starts it anew, and its views go
    /// back on the files that were showing them.
    /// </summary>
    void Reload(HexExtension extension, Action start);
}
