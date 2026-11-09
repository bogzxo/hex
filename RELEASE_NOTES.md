# Hex 2.9.0 smallman

A smaller gap between writing something and getting it to run. C#, Python and C++ have more of their own tools
inside Hex now, from setting up a language server to building a project, running its tests and stopping in the
debugger. The terminal knows its way around dotnet too.

## code

- language server setup says when npm is missing, with a way to get it before trying to install a server that needs it
- C#, Python and C++ use more of what their native language servers provide: completion documentation, signature
  help and overloads, related diagnostics, folding, formatting and navigation
- go to definitions with more than one result, follow references and call or type hierarchies, and open framework
  metadata and native headers without treating them as files to save
- rename and code actions check that their edits still belong to the file and workspace they were asked for
- Python environments can be created and selected in Hex, and the editor, terminal, tests and debugger follow the choice

## projects and tests

- .NET solutions, project references and NuGet packages have their own project commands, with launch profiles,
  arguments, environment variables and target frameworks carried through to running and debugging
- the terminal completes dotnet commands, SDK options, project names and NuGet commands, and compiler errors lead
  back to their files through Problems
- Test Explorer runs .NET, Python unittest and pytest, and CMake tests, keeps results with the project and target
  framework they came from, and can retry the failed variants
- cancelling or restarting tests and debug builds no longer lets an old request start something after it was stopped

## debugging

- Pause works for a running C# program, including when the runtime first stops in its own helper code
- attach to a running .NET process from a picker or by PID, and detach without ending the program it belongs to
- inspect loaded modules, browse disassembly and memory, and use function, instruction and data breakpoints where
  the debugger supports them
- watches and variables have richer completion, editing, hexadecimal values and paging for larger results
- source supplied by the debugger opens as a read-only snapshot, with stepping and navigation kept in the same session

## fixed

- breakpoint shortcuts no longer share a key with the extensions' sort-lines command
- read-only debugger and generated source stays out of file-only commands and save dialogs
- notebook wrapping, canvas resizing and output handling behave more consistently while moving through a document
- editor navigation, dialogs and large files have had another round of fixes

**Full Changelog**: https://github.com/bogzxo/hex/compare/v2.8.8...v2.9.0
