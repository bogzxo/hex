# Notebook and explorer integration

## Acceptance

1. Open an isolated copy of the supplied beam notebook, inspect every saved cell, and execute its code in a separate working directory. The Downloads original must remain byte-for-byte unchanged. Check formulas, four plots, DataFrames, Markdown tables, generated CAD files, and notebook save/reopen.
2. Reuse the editor's existing zoom commands and preference. Notebook source, prose, formulas, tables, and outputs must update together. Ctrl+wheel must work over the notebook, and reset must restore the initial scale.
3. Render Markdown tables as document content: wrapped cells, variable row heights, emphasis, inline code, and formulas. Keep native sortable/scrollable tables for DataFrames. Preserve the existing CSV tools for both.
4. Keep TUI symbols on the terminal cell grid. Common triangles and arrows must remain legible when the selected font lacks them. Preserve copied terminal text and terminal input behavior.
5. Watcher refreshes must preserve explorer selection and viewport. Explicit navigation may still reveal the opened file.
6. Integrate the existing file operations into explorer-focused shortcuts, multi-selection, row and background context menus, and a clear Move To action. Reuse the existing folder picker and document-move notifications. Never silently overwrite a destination. Keep failed cut items available for retry, reject recursive copies/moves, and avoid following directory links during recursive copies.
7. Replace the Markdown preview extension with a native source/preview view, sharing notebook Markdown, math, table rendering, zoom, and texture ownership. Preserve F7 and migrate saved legacy view IDs; do not load or offer the retired extension.
8. Add a shared `scroll_speed` multiplier, default 1, configurable from 0.25 to 5 in both modes. Scale content scrolling once as wheel input bubbles. Keep zoom/value gestures in their original units and accumulate fractional scrolling in discrete controls and terminal programs.

## UX and performance

Use Ctrl+C/X/V only while the explorer owns focus; text editors and terminals keep their own clipboard commands. Show Cut as a reversible staged operation, Escape cancels it, and Paste targets the selected folder or the selected file's parent. Copy paths remains a distinct command. Ordinary refreshes preserve browsing position instead of following the active editor.

Reuse cached notebook textures when zoom changes. Reflow document content only when source, zoom, or available width changes. Keep terminal rendering allocation-free for ordinary text, and preserve virtualized rendering for large DataFrames and explorer trees.

## Validation

Add focused regression tests for zoom propagation, formatted/wrapped Markdown cells, stable explorer refresh, clipboard retry and path boundaries, and terminal symbol geometry. Run the real notebook in the native graphical harness, inspect screenshots at representative positions and zoom levels, then run the full Hex suite and relevant engine checks. Record any rendering or execution limitation instead of claiming untested compatibility.
