# Agents that know Horizon without a clone of it

A plan, nothing of it built yet.

## Where it stands

A Horizon game is made on the engine's NuGet packages now (`bogzxo.Horizon.Engine`, `.Platform.Desktop`, `.UI`, `.HIDL`), and Hex's own template makes one that way, so a game folder has no engine source in it and nothing beside it. An agent working on the game still needs to know the engine's API: what `Scene2D` hands over, what `ShapeList` draws, which thread a member is for. Today it either guesses, or greps a checkout somebody happens to have. The ThreeBody test on 2026-11-22 showed both: the Mac agent spent most of its first minutes asking to run `grep` and `sed` over an engine checkout, and the agent in a private worktree couldn't read the engine at all.

The packages carry `lib/net11.0/*.dll` and nothing else, no XML documentation, no sources, no symbols, so what an agent could learn from them is the bare signatures, and Hex's harness (`hex_symbols`, `hex_read`, `hex_search`) indexes only the workspace's own files, so it doesn't see the packages at all.

## What an agent should be able to do

- Find an engine type or member by name and read its signature with its summary, `hex_symbols ShapeList`, `hex_read Scene2D`, the same way it reads the game's own code, from any folder that references the packages.
- Read the guide, `docs/guide`, and the examples of Horizon.Testing, as the version the game is built against, not whatever `development` is today.
- Know what's on which thread and what a member is for, which is in the engine's summaries, so the summaries have to travel with the packages.
- Do all of it offline, in a private worktree, and on a machine that has never seen the engine's repository.

## The plan

### 1. Documentation in the packages

Turn on `GenerateDocumentationFile` for the four packed projects in `horizon/Directory.Build.props`, so every package carries `lib/net11.0/<assembly>.xml` beside its DLL. The summaries are already written, every public member has one, so this is the cheapest step and the one everything else leans on. The warnings for missing summaries (CS1591) stay off for the projects that don't pack.

Also pack the guide and the examples as content that isn't copied to the output, under `docs/` and `examples/` in `bogzxo.Horizon.Engine` (a few hundred KB of Markdown and C#, the examples' art left out), so the version on disk always matches the version referenced.

### 2. Sources with symbols

Publish symbol packages (`.snupkg`, `IncludeSymbols` with `SymbolPackageFormat=snupkg`) with SourceLink (`Microsoft.SourceLink.GitHub`, `PublishRepositoryUrl`, `EmbedUntrackedSources`), from `nuget.yml` next to the packages. Every type then points at its file at the exact commit on GitHub, which is what Go to Definition and an agent want for the bodies of methods, not only their signatures. Embedding the sources in the PDB (`EmbedAllSources`) is the offline variant, at the cost of a bigger symbol package, and is the one to start with.

### 3. The harness indexes package references

Teach Hex's index (`Hex.Core/Harness/HexCodeIndex`) to take in the packages a workspace project references, read from `obj/project.assets.json` (already produced by a restore) and the global packages folder:

- Declarations from the assembly's metadata (`System.Reflection.Metadata`, no loading), each with its XML summary from step 1, indexed under a virtual path such as `package:bogzxo.Horizon.Engine/1.5.0/Horizon.Engine.Scene2D`, ranked after the workspace's own matches.
- `hex_read` on such a declaration gives the signature and summary. When the package has embedded sources (step 2) it gives the real source, otherwise the language server's decompiled source, which Hex already opens for framework types (`HexLanguages.Navigation`, `DecompiledFolder`).
- `hex_search` and `hex_files` reach the packed `docs/` and `examples/` of step 1 as files of the package, read only.
- Off by default for packages other than Horizon's, behind a setting (`harness_index_packages`: none, horizon, all), so a solution with three hundred packages isn't indexed by accident.

This works in a private worktree and over Hex Link with no change, because the packages are restored on the machine the agent runs on.

### 4. A Horizon reference tool, for agents that don't use Hex's tools

`hex_horizon` (or a section of `hex_symbols`) answers "what is X in Horizon": the summary, the members, the guide page that covers it and an example that uses it. It's a thin layer over step 3 with the guide's index added, and it gives Codex, Claude Code and Antigravity the same answer.

### 5. Agent briefs

The Claude Code, Codex and Antigravity extensions tell their agents, in the text Hex already adds, that a Horizon game's engine is readable through `hex_symbols` and `hex_read` and its guide through `hex_search`, and that grepping a checkout is the slow way.

## Order and size

| Step | Where | Size |
| --- | --- | --- |
| 1. XML docs, guide and examples in the packages | horizon, `Directory.Build.props`, `Horizon.csproj` | small, ship with the next engine version |
| 2. Symbol packages with embedded sources | horizon, `nuget.yml` | small |
| 3. Harness indexes package references | hex, `Hex.Core/Harness` | the bulk of it |
| 4. Horizon reference tool | hex, MCP tools | small, after 3 |
| 5. Agent briefs | hex-extensions | small |

Steps 1 and 2 are worth doing first and on their own: language servers pick up XML docs and embedded sources from packages already, so hovers, completions and Go to Definition in the game improve the moment they ship, before Hex's index knows anything about packages.

## Checks

- A game made with the template, in a folder with no engine anywhere on the machine, and a fresh agent asked to add a feature that needs `ShapeList` and the UI: it finds both with `hex_symbols`, reads their summaries, and builds, without running `grep`.
- The same in a private worktree and on a linked device.
- The package index updates when the referenced version changes, and drops a package that's no longer referenced.
- Indexing the packages costs under a second for the four Horizon packages, and nothing when the setting is off.
