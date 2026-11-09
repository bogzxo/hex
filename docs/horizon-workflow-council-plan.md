# Horizon development workflow qualification in Hex

Goal: an independently evidenced council score of at least 9.5/10 from every reviewer for developing the complete Horizon workspace in Hex, with performance and memory treated as acceptance criteria. A score is not proof of every feature ever shipped by Visual Studio. Unsupported capabilities must be listed explicitly and assessed against this repository's real work.

## Workload and acceptance matrix

1. Open Horizon.Workspace.sln and its nested engine, UI, HIDL, tooling, platform heads, Hex and Fighter projects; discover project membership, dependencies, configurations and startup targets without hand-maintained substitutes.
2. Navigate real C# declarations, references, implementations and diagnostics across projects; completion, signature help, rename/refactoring, formatting and quick fixes must work on changed files.
3. Edit, build and diagnose a deliberately broken temporary branch/fixture from Hex. Navigate errors, fix, rebuild, run the selected application and manage cancellation/restart without losing work.
4. Debug a real managed repository target: launch/attach where supported, bind a breakpoint, step, inspect locals/watch/call stack, evaluate and stop/restart. UI presence alone is not a pass.
5. Discover and run unit tests, show individual failures and navigate to source; assess convenience against Visual Studio Test Explorer. Include test filtering and repeat runs.
6. Project workflows: add/rename/move files, references and package changes, solution search, terminal/tasks, Git diff/stage/commit/conflict interactions in disposable fixtures, session restoration and unsaved recovery.
7. Repository-specific work: HIDL designer and values, shaders, notebook execution/export, image-backdrop wrapping and zoom/resize, input/scrolling, keyboard-only use, DPI and layout discoverability. Finish the previously requested localisation terminology rewrite and validate placeholder/key preservation.
8. Performance: record release artifact provenance and actual repository size; cold/warm startup, indexing, typing/navigation/search, build responsiveness, notebook/terminal activity, settled idle CPU/GPU, working set/private bytes/managed heap, and memory after repeated open/close cycles. Use matched workload/build configuration, warmups and repeated measurements; exclude overlapping builds from timing. Investigate material regressions rather than hide variance or reduce functionality.

## Execution order

- Preserve dirty source and capture baseline; serialize shared builds and GUI control.
- Fix known issues and complete pending validation before first scored review. Existing parity tests largely test VS Code shortcuts; they are not Visual Studio qualification.
- Three independent high-effort reviewers from distinct available providers, per council skill. Read-only review, concrete reproduction and evidence references, score/10 and explicit satisfaction. Surface provider/model availability problems rather than substitute silently.
- Reviewers must not inflate scores to meet the target. Missing hands-on evidence lowers confidence and blocks completion where essential.
- Parent implements accepted findings, reruns affected regression and performance checks, then commissions a fresh full review round. Keep every round and disagreement.
- Completion: all reviewers >=9.5 and satisfied, no critical/high workflow defects, actual workflow evidence and performance/memory results, limitations and reproducible commands documented. No commit/push/release is implied by this goal.

## Initial state

2026-11-07: active workspace C:/Users/bogdan/source/repos/bogzxo/horizon, nested Hex development checkout with many preserved uncommitted changes. No open Hex PRs. Prior middle-scroll Settings339/339 and input20/20; notebook export/resize22 focused tests. Those do not certify repository-wide workflow parity. Localisation rewrite and notebook visual/caret/scroll validation remain unfinished. Earlier rendering optimization evidence is in the separate horizon-android-performance worktree and must not be attributed to this checkout without checking provenance.
