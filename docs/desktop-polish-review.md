# Hex desktop polish review

4 November 2026. Dawn is the editor and designer. Dusk is the terminal. Both use Horizon controls, Hex looks and a shared mode switch. The design goal for this pass is a workspace where the document stays prominent, actions can be recognized at a glance and returning to a surface preserves context.

## Inspection

Three Terra reviewers divided the editor shell, document workflows and standalone terminal. Native self-tests drove the app through its own pointer, keyboard and command paths. The Dawn baseline used the previous Agents review build. Dusk used a fresh build of the checkout. Captures were taken at 1280 × 800, including a Dawn run at 200% editor scale. A registered test or a source inspection alone does not establish that the workflow was exercised.

| Surface and way in | Evidence and findings |
| --- | --- |
| Dawn Welcome, New, Open, file history | Welcome capture has a clear action column and Recent list. Source inspection found that both cached Welcome pages build their recent rows only once. The File menu gets subsequent changes. |
| Title bar, mode switch, application and cascading menus, shortcuts | Dawn menu suite opens menus across code and designer contexts. Dusk shares the mode switch. Preserve the alignment of window actions and shortcut labels. |
| Activity rail, Explorer, Outline, side grips, inspector, bottom panel, status | In the enlarged editor capture an empty Outline takes about 46% of the window. The active file shares the remaining space with the tail of a clipped tab. |
| Document tabs, overflow picker, breadcrumbs, split editors, quick open | The active file and overflow action are visible in the narrow test. A fragment of the preceding filename reads like a broken label. Verify editor width and switching files during resize. |
| Code, syntax, completion, signature help, code lens, inline hints, minimap, sticky lines, diagnostics and diff | Code, diff and find captures establish the standard layout. Find has a fixed-width query, match count and controls. Placement adjusts the offset but cannot fit the row inside some narrow editors. Language-specific behavior was inventoried from the registered suites. |
| Search and replace in files, Git status, history, conflicts, Problems, Output, tasks, work and debugger | Source and test inventory covers query toggles, grouped rows, hunk actions, conflict lenses, task progress and debug controls. Debug adapter, tool installation and external language server paths need their own environments. |
| HIDL Design, Code and Play, layers, toolbox, Properties and values | Reviewed designer shell, menus and property inspector captures. Existing property tests exercise multi-selection and editable project metadata. Preserve inspector utility while the viewport changes. |
| Agents provider, conversation history, transcript, tool disclosure, composer, runner and inline hints | Existing and fresh narrow captures show a truncated provider name. Keep the selected provider identifiable and the composer, context actions and send button reachable. Prior work supplies the rounded input and stable welcome rows during drag. |
| Image, Markdown, notebook, table/data, scratchpad and arbitrary file viewers | Source and suite inventory covers document switching, image previews, rendered Markdown, data values, notebook cells, kernels and rich output. Screens from the image properties workflow cover the preview and inspector. |
| Extensions, workspace, file picker, settings, looks, backdrop, keyboard bindings, changelog, help and updates | Source and menu inventory, plus Dusk settings capture. Install, updater, third-party providers and native file associations were not manually commissioned. |
| Dusk tabs, panes, profiles, terminal input/output, scrollback, minimap and inline find | Native checks exercised search, splits, rename and themes. Pane focus is visible. Pane labels can coincide when shells share a folder. The find overview deserves a separate occlusion check in dense terminal output. |
| Dusk menus, command palette, themes, effects and settings | Baseline passed 45 checks. Palette labels and keycaps align, but New, Close and Split carry identical terminal glyphs although the menu differentiates them. Settings is a root-level window and needs an explicit check that background shortcuts respect its focus. |
| Dusk session restore, plugin commands, terminal TUIs, embedded Dawn and backdrop arrangement | Source and suite inventory. At baseline the autosave check missed renames. The later persistence pass compares the actual serialized session and exercises rename-only changes. The shared session format records the selected tab but does not preserve its focused split pane. |

## Implementation plan

1. **Give the document room.** Allocate sidebar space from the live window budget at enlarged scale. Recover the chosen width as space returns and keep direct manipulation usable. Improve crowded file navigation and the Agents provider header at minimum width. Compare 100% and 200% captures and test dragging inward and outward, switching provider, picking an overflowed file and retaining the active document.
2. **Make everyday commands easier to use.** Reflow editor Find and Replace for small canvases. Keep the query, dismissal, replacement and navigation reachable through resize. Retain the wide layout and its minimap clearance. Give Dusk commands the same meaningful glyphs as their menu actions, including usable defaults for extension commands. Check find, replacement, keyboard navigation and the command palette with fresh captures.
3. **Finish the return paths.** Keep the Welcome lists and empty state in sync with File history in both editor modes. Keep Dusk terminal shortcuts scoped correctly when settings owns input and restore terminal focus on dismissal. Incorporate actionable feedback from the preceding reviews. Test reopening, resizing, keyboard dismissal, focus return and cross-surface regressions.

Each stage ends with a fresh Sol reviewer. Score hierarchy and legibility, geometry and responsiveness, interaction and focus, and state feedback and motion with equal weight. Use the same anchors throughout: 5 represents Windows 10 and 9 represents macOS on the requested scale. The target band is 7.5–8.5. Ratings describe the reviewed build and sample. Record missed checks and identify source-only observations separately from interaction or screenshot evidence.

## Baseline interaction evidence

| Run | Actual result | Scope and observations |
| --- | --- | --- |
| Dawn shell | 136/142 | UX, Agents layout, surface, properties and menus. The six misses were Editor Scale, Appearance and Folding under View in both designer and code. The Appearance capture shows the stationary pointer highlighting Toolbox and the submenu absent. |
| Dusk | 45/45 | UX, palette, themes, panes and effects. Captures include the palette, split badges, inline find, settings, drop-down terminal and an alternate look. |
| Dawn document workflows | 398/401 | Search, Git, conflicts, Markdown, notebook, background work, Problems, Explorer, designer and file dialogs ran against temporary files. The failures were widening Properties by dragging and two assertions about how a conflicted merge appears in Git. |

The broad workflow run produced captures of selected designer controls, grouped Problems, filtered Search, the background work card, Git changes, conflict actions, word diff, native Markdown and the Explorer clipboard workflow. The designer and Properties views distinguish selected rows and focused text fields. Problems combines severity icons and labels. Git and file-dialog behavior also depends on the fixture state. A passing layout assertion cannot establish text contrast or whether a transition feels smooth.

The registered [Dawn suites](../Hex.Dawn/Testing/DawnScene.SelfTest.cs) and [Dusk suites](../Hex.Dusk/Testing/DuskScene.SelfTest.cs) identify individual entry points for the remaining inventory. Production screen-reader traversal, alternate operating systems, real third-party extension consent screens and third-party terminal TUIs are outside this desktop sample.

## Independent Sol scorecard

| Stage | Hierarchy and legibility | Geometry and responsiveness | Interaction and focus | Feedback and motion | Mean |
| --- | ---: | ---: | ---: | ---: | ---: |
| 1. Document and sidebar geometry | 7.7 | 8.0 | 7.6 | 7.4 | **7.7** |
| 2. Find, file navigation and command palettes | 7.8 | 8.0 | 8.0 | 7.5 | **7.8** |
| 3. Welcome, keyboard focus and motion | 7.3 | 8.0 | 8.4 | 8.1 | **8.0** |

**Run 1.** The reviewer compared before and after images. At 200% the Outline panel fell from about 584 to 410 physical pixels, leaving about 744 for the editor. The active tab is visible in full and the 180-unit Agents header spells out “Workspace assistant”. The four focused suites passed 49/49. The reviewer requested exercising overflow selection through a resize and noted that the inspector still takes considerable space in the three-column designer. Motion evidence came from runtime checks of drag and setting changes.

**Run 2.** Dawn passed 47/47 and Dusk 53/53. The reviewer inspected the scrollable palette at both 900 × 600 and 600 × 450. The latter also passed its separate 7/7 palette checks. Split, Close and Rename now have distinct icons and the footer shows the keyboard actions. Picking a file from the overflow list kept both its selection and editor focus across two scale changes. The narrow Find test exercised regex, tab order, navigation, replacement, undo and dismissal. It exposed an artificial title-bar collision because the test set compositor scale directly. Stage 3 uses the same scaling path as the View menu. The reviewer identified Dusk settings keyboard ownership and Welcome history as the remaining priorities.

**Run 3.** Dawn passed 91/91 checks and Dusk passed 57/57. At 200%, Find and Replace fits in three rows and the focused field remains distinguishable from diagnostic highlights. Recent files update on both Welcome pages and in File. With Dusk Settings open, its search, buttons and picker retain their keyboard input. Terminal shortcuts work again after dismissal and when a terminal is deliberately focused. Split captures with motion enabled and disabled and runtime samples of tab entry covered transition behavior. The reviewer used captures and test output to assess motion.

The final mean before rounding was 7.95. The largest remaining gap is legibility. Start and the Hex identity read clearly, but the smaller Welcome actions, recent filenames and narrow Agents details are subdued. The reviewer found no clipping in the final sample.

## Reproducing the checks

From the workspace root, build the Hex project, then use these arguments with the resulting executable. Add `--exit` for an unattended run. `HEX_SELFTEST_SHOTS` and `DUSK_SELFTEST_SHOTS` select the destination for captures.

| What to check | Arguments |
| --- | --- |
| Editor scale, file switching, Agents resize, designer and inspector | `--window --selftest layout ux agents-layout properties surface --size 1280x800` |
| Terminal palette, focus, split behavior and session restore | `--dusk --window --selftest sessions panes palette --size 1280x800` |
| Branch actions, conflict rows, commit and undo | `--window --selftest git --size 1280x800` |

## Integration checks after the three stages

The designer run passed 255/255 checks, including both sidebar drag directions and the 125% toolbar. The Dusk session, pane and palette run passed 48/48. A tab rename and a pane rename each reached disk on the autosave timer, and an unchanged session kept its modification time. The Git run passed 117/117 against temporary repositories. Its conflict checks now wait for the conflict file and notice, and identify Commit by the row the product renders. The captured merge shows the conflict in both the tree and the document.

The code-quality pass also covered workspace roots and older saved drive paths, MSBuild metadata for similarly named files, and debugger replies that arrive after changing stops, frames, sessions or watch expressions. All 565 Hex unit tests and 13 focused Horizon menu, chooser and focus tests passed on Windows.

The final integration run passed 315/315 checks across Welcome, workspace, menus, MCP and debugger availability. It exercised Explain and Change drafts, retained selections, inline hints and both menu contexts. Settings dismissal and keyboard return took 124 ms for its 120 ms animation. The checks now wait for the window to finish dismissing before sending input to the next one. This machine had no netcoredbg executable, so the native debugger suite took its availability branch. The 25 focused debugger tests include deterministic delayed replies for the affected timing cases.
