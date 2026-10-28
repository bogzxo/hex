namespace Hex.Dawn;

/// <summary>How a file is looked at, the designer of a layout (the canvas and the tools of one), its code, its table, or a view an extension made.</summary>
internal enum HexView
{
    Design,
    Code,

    // A file of code as a table, an object of objects or a file of texts, see DawnScene.Table
    Table,

    // A view an extension made for the file, which one in HexDocument.ExtensionView, see DawnScene.UIScriptedLayout
    Extension,

    // A Jupyter notebook as its cells and what running them came to, see DawnScene.Notebook
    Notebook,

    // Native Markdown preview, sharing document rendering with notebook Markdown cells.
    Markdown
}
