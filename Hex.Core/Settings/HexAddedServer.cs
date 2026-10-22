namespace Hex.Core;

/// <summary>
/// A language server added in the settings (Editor, Language servers), for a language Hex colours that
/// language_servers.hor has nothing for, or to be used instead of the one it has.
/// </summary>
/// <param name="Language">The language's id, d, rust, which the protocol knows the files by and the settings keep it under.</param>
/// <param name="Name">What the language is called, D, Rust.</param>
/// <param name="Extensions">The files it's for, ".d" and the like.</param>
/// <param name="Command">The program, a full path or one on the PATH.</param>
/// <param name="Arguments">What it's started with, --stdio for the ones that want asking, none for most.</param>
internal sealed record HexAddedServer(string Language, string Name, string[] Extensions, string Command, string[] Arguments);
