namespace Hex.Core.Notebooks;

// The page's look, inline so the page needs no other file. Light, readable at any width, and printable, prompts beside
// the cells on a wide page and over them on a narrow one
internal static partial class NotebookHtmlExport
{
    private const string STYLE = """
:root {
  --ink: #1f2328; --muted: #656d76; --line: #d0d7de; --code: #f6f8fa; --error: #cf222e; --error-bg: #fff1f0;
  --stderr-bg: #fff8e6; --link: #0969da; --notice: #fff8c5; --notice-line: #d4a72c;
  --mono: ui-monospace, "Cascadia Mono", "SFMono-Regular", Menlo, Consolas, "Liberation Mono", monospace;
}
* { box-sizing: border-box; }
html { -webkit-text-size-adjust: 100%; }
body { margin: 0; background: #ffffff; color: var(--ink); font: 15px/1.6 system-ui, -apple-system, "Segoe UI", Roboto, "Noto Sans", sans-serif; }
main.notebook { max-width: 1000px; margin: 0 auto; padding: 24px 16px 64px; }
.cell { margin: 0 0 18px; }
.cell.markdown, .cell.raw { padding-left: 88px; }
.input, .output { display: flex; gap: 8px; align-items: flex-start; }
.prompt { flex: 0 0 80px; text-align: right; font: 12px/1.9 var(--mono); color: var(--muted); white-space: nowrap; user-select: none; }
.input .prompt { color: #0550ae; }
.output .prompt { color: #bc4c00; }
.input pre.source { flex: 1 1 auto; min-width: 0; margin: 0; }
.outputs { margin-top: 6px; }
.output { margin-top: 4px; }
.output > pre, .output > .result { flex: 1 1 auto; min-width: 0; margin: 0; }
pre { font: 13px/1.5 var(--mono); overflow-x: auto; white-space: pre; tab-size: 4; }
pre.source { background: var(--code); border: 1px solid var(--line); border-radius: 6px; padding: 8px 12px; }
pre.stream, pre.text, pre.error { padding: 2px 4px; white-space: pre-wrap; overflow-wrap: anywhere; }
pre.stderr { background: var(--stderr-bg); }
pre.error { background: var(--error-bg); border-left: 3px solid var(--error); padding-left: 8px; }
code { font-family: var(--mono); font-size: 0.9em; background: var(--code); padding: 0.1em 0.3em; border-radius: 4px; }
pre code { background: none; padding: 0; font-size: inherit; }
.cell.raw pre { color: var(--muted); }
h1, h2, h3, h4, h5, h6 { line-height: 1.25; margin: 1.2em 0 0.5em; }
h1 { font-size: 2em; border-bottom: 1px solid var(--line); padding-bottom: 0.2em; }
h2 { font-size: 1.5em; border-bottom: 1px solid var(--line); padding-bottom: 0.2em; }
.cell.markdown > :first-child, .markdown > :first-child { margin-top: 0; }
p, ul, ol, blockquote, table { margin: 0 0 0.8em; }
blockquote { border-left: 4px solid var(--line); color: var(--muted); padding: 0 1em; margin-left: 0; }
a { color: var(--link); }
hr { border: 0; border-top: 1px solid var(--line); margin: 1.5em 0; }
table { border-collapse: collapse; display: block; width: max-content; max-width: 100%; overflow-x: auto; font-variant-numeric: tabular-nums; }
th, td { border: 1px solid var(--line); padding: 4px 10px; text-align: left; vertical-align: top; }
thead th, tbody th { background: var(--code); }
.html table.dataframe { font-size: 13px; }
.html table.dataframe td, .html table.dataframe th { text-align: right; }
img.picture { display: block; max-width: 100%; height: auto; }
img.math { max-width: 100%; height: auto; }
.math.display { text-align: center; margin: 0.6em 0; overflow-x: auto; }
.math.source { font-family: var(--mono); }
div.math.source { white-space: pre-wrap; }
.notice { margin: 2px 0 6px; padding: 6px 10px; background: var(--notice); border-left: 3px solid var(--notice-line); font-size: 13px; color: var(--ink); }
.missing { color: var(--muted); font-style: italic; }
.ansi0 { color: #3e4451; } .ansi1 { color: #c4314b; } .ansi2 { color: #2c7a39; } .ansi3 { color: #946300; }
.ansi4 { color: #1d63c5; } .ansi5 { color: #9b3cb4; } .ansi6 { color: #0f7c86; } .ansi7 { color: #6e7781; }
.ansi8 { color: #57606a; } .ansi9 { color: #e5534b; } .ansi10 { color: #3fa04a; } .ansi11 { color: #b08800; }
.ansi12 { color: #3b82f6; } .ansi13 { color: #bf5af2; } .ansi14 { color: #14a3b0; } .ansi15 { color: #8c959f; }
@media (max-width: 700px) {
  main.notebook { padding: 16px 12px 48px; }
  .cell.markdown, .cell.raw { padding-left: 0; }
  .input, .output { display: block; }
  .prompt { text-align: left; line-height: 1.6; }
  .output > .prompt:empty { display: none; }
}
@media print {
  body { font-size: 11pt; }
  main.notebook { max-width: none; padding: 0; }
  pre, pre.source { white-space: pre-wrap; overflow-wrap: anywhere; overflow: visible; }
  table { display: table; overflow: visible; }
  .input, .output, img, tr, pre.source { break-inside: avoid; }
  h1, h2, h3, h4 { break-after: avoid; }
  a { color: inherit; }
}

""";
}
