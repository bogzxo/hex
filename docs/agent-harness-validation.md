# The agent harness, tried for real

Hex was started on its own repository (`Hex.exe --window --mcp --folder hex`, 691 files indexed) and driven by a small
client over `--mcp-connect`, saying it was `claude-code`, the way Claude Code would. Every question was answered twice,
once with the harness and once the plain way an agent goes about it (grep, find, ls, head and reading files whole the
way Claude Code's Read hands them back, with a number in front of every line). Characters are counted exactly, tokens
are about a quarter of that for code, the same for both sides so the ratio holds whatever the tokenizer is.

## Finding things

| Question | Plain way | Calls | Chars | Harness | Calls | Chars | Saved |
| --- | --- | --- | --- | --- | --- | --- | --- |
| What's in this repository | `find` of every file | 1 | 28,212 | `hex_map` depth 2 | 1 | 5,834 | 79% |
| Where's McpSchema.Check and what does it do | grep for it, read McpSchema.cs | 2 | 6,888 | `hex_read` symbol | 1 | 2,515 | 63% |
| Who calls QueueMcp | grep | 1 | 1,727 | `hex_refs` | 1 | 410 | 76% |
| What's in Hex.Core/Mcp | ls, `head -40` of every file | 2 | 18,045 | `hex_skim` a glob | 1 | 6,297 | 65% |
| Where's CloseTab used | grep | 1 | 2,341 | `hex_search` | 1 | 2,021 | 14% |
| Read HexMcpCall.Step | grep for the class, read DawnScene.Mcp.cs | 2 | 14,982 | `hex_read` symbol | 1 | 1,102 | 93% |
| Which file is the MCP session | `find -iname` | 1 | 29 | `hex_files` | 1 | 96 | -231% |
| **All of it** | | **10** | **72,224** | | **7** | **18,275** | **75%** |

That's about 18,000 tokens down to 4,600 and three calls fewer. Reading all nine files of Hex.Core/Mcp instead of their
heads, which is what an agent that wants to understand them usually ends up doing, is 63,106 characters against the
skim's 6,297.

Where it doesn't help is as plain as where it does. A grep for a rare name is already small and the harness's version
is the same lines without the long paths (14%), and finding a file whose name you know is cheaper with `find` than with
anything that says line counts. The wins are reading one declaration out of a big file, the overview of a folder or the
repository, and references grouped under their file.

## Speed

A cold index of the 691 files took about half a second as part of the first call. With the cache on disk, Hex
started again took 688 of the 691 files from it (three had changed) and answered its first query in about 120 ms, a
query of a warm index is 1 to 30 ms, and the same question again is kept and comes back in under a millisecond.

## The rest of it, driven the same way

- The server said it has resources, `hex://harness/changes` was listed and subscribed to, a file was changed on disk,
  and `notifications/resources/updated` came within the couple of seconds the watching looks every. `hex_changes` from
  the generation before said `~ Hex.Core/Mcp/McpTool.cs` and nothing else.
- A C# file opened in Hex had csharp-ls's symbols in the index within a few seconds, `hex_skim` saying so, its class's
  members one level in once the file and namespace csharp-ls wraps them in were walked through.
- The Claude Code and Codex extensions were installed next to Hex.exe and started. A client saying `claude-code` was
  told Claude Code's instructions after Hex's and given an 8000 character budget, one saying `codex-mcp-client`
  Codex's and 6000.
- `hex_terminal list` with nothing open, then `hex_terminal_run` of `dotnet --version` opened a PowerShell tab and came
  back with `Exit 0` and the version in 2.6 s, a `git status --short | head -3` with exit 0 and its three lines, and
  `dotnet nosuchthing` with `Exit 1` and dotnet's complaint, none of the prompt or the typed line in any of them.
- `hex_notebook_new` made a notebook outside the workspace, `hex_notebook_edit` with its path set cell 1 to
  `print(6*7)`, `hex_notebook_run` with its path ran it in Python 3 and said 42, all without a `hex_open` between.
- An unknown file in `hex_read` comes back as an error that says what to try instead, and the built in
  `hex_harness_stats` agreed with the table above to within its rough idea of the plain way.

## Tests

`HexHarnessTests` and `HexAgentSystemsTests` in Hex.Tests, 28 of them, and the whole of Hex.Tests passes (528). Hex's
own self test passes for mcp, notebook, terminal and extensions (one terminal check needs the test's own home and fails
only when `HEX_HOME` is set from outside, as it was for the run above, and passes without).

## What it isn't yet

- `hex_harness_stats`'s plain way is a guess (grep -rnw of the name for symbols), it overstates what's saved for a
  common name, the table above is the honest number.
- The outline's shapes see a raw string of C# inside a test as declarations, so a test full of sample code turns up in
  `hex_symbols`. A language server's symbols don't, and those come in for any file that's been open.
- References are by the word, a method called Check in two classes is one name to them. `hex_references` asks the
  server when that matters.
