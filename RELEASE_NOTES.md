# Hex 2.9.1 smallman

Better scrolling and clearer answers in Agents, with a few more of the editor's details brought into line.

## agents

- replies and thinking use the document renderer for headings, lists, links and code blocks
- Markdown tables have proper columns, alignment and wrapping, including in a narrow sidebar
- file references stay clickable without rewriting links or the contents of code blocks

## scrolling

- middle-mouse scrolling moves lists, file trees and tables continuously instead of jumping through wheel notches
- middle-mouse scroll speed has its own setting under Appearance · Motion, from 0.25× to 5×, shared by Dawn and Dusk
- rapid scrolling no longer lets the overscroll animation cover a strip of content at the viewport's edge
- long words and inline code wrap within narrow Markdown table cells

## editor

- C# language-server settings enable analyzer diagnostics, including code-style and naming suggestions where the server supports them
- empty diagnostic messages stay out of the visible list while their code actions remain available
- Dawn and Dusk use a grayscale Hex logo in the title bar, tinted by the current theme

**Full Changelog**: https://github.com/bogzxo/hex/compare/v2.9.0...v2.9.1
