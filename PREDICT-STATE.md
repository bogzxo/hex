# Edit prediction, phase 2 (glue): state

Branch `feat/edit-prediction-glue` (from `feat/edit-prediction` d98c1d7), worker chat #121 for #110. Stopped on request
of the person, 2026-11-21. Commits: 038eaff (glue), c585a2e (self test, goldens, MCP refusals), and the one with this file.

Done:
- `Hex.Dawn/Code/HexPredictionGlue.cs`: pure parts (gates of 4.1, status item state and string key, offsets to places,
  caret after, 30-line excerpts, sent path).
- `Hex.Dawn/Code/DawnScene.Prediction.cs`: engine wiring. Options cached until a setting changes; snapshot behind the
  gates (lines as references into a new array, once per pause); Edited from CodeChanged (typed-through and Ctrl+Right
  word kept); Tick/Take/SetPrediction with version and caret check; ended -> history + Accepted / Dismissed (Escape) /
  Edited again (caret moved); closed -> Forget + History.Closed; related excerpts of two recent files.
  HexPredictionRelay.Configure once at start with live-reading lambdas.
- `Hex.Dawn/Code/DawnScene.PredictionItem.cs`: commands under Edit > Edit Predictions (Toggle, Toggle for Language,
  Predict Now Alt+\, Accept Alt+L, Accept Next Word, Dismiss, Settings); status item beside the language (off, not set
  up, off in folder, off for language, off for file, waiting after 300 ms, errors) with tooltip; first-time notice.
- Setting `edit_predictions_notice_done` (row, Usual), goldens re-recorded (diff only that row).
- hex_settings_set refuses edit_predictions, _share, _api_url, _provider (DawnScene.McpWorkbench.cs).
- `Hex.Dawn/Testing/HexFakePredictionServer.cs`, `DawnScene.SelfTest.Prediction.cs` (registered as `prediction`),
  `Hex.Tests/PredictionGlueTests.cs`.

Test lines (last runs):
- Hex.Tests: Passed 1489 of 1489.
- `--selftest prediction`: 35 of 35 checks passed (run8). Earlier runs showed the frame-time check flaky once
  (on p95 27.4 ms vs off 6.2 ms, run7; next run 4.4 vs 4.3).
- `--selftest code settings`: finished twice: 570 of 572 (FAIL "running: and isn't once it's gone", "values: and what
  is typed goes into the code where the number was"), then 572 of 572 on a rerun with the same build: flaky, not
  compared against a base build.

Left / next step:
- Run the prediction suite a few more times for the frame-time check's flakiness; if it repeats, look at
  CancellationTokenSource.Cancel on the editor's thread in PredictionEngine.Stop (core) as a cause.
- Rebase/merge onto feat/edit-prediction 02b8d6a (Zeta2Format change) and rerun.
- Diagnostic Console lines in the self test (ghost/Tab) can be trimmed.
