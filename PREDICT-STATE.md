# Edit prediction, phase 2 (glue): state

Branch `feat/edit-prediction-glue` (from `feat/edit-prediction` d98c1d7), worker chat #121 for #110.

Done:
- `Hex.Dawn/Code/HexPredictionGlue.cs`: the pure parts (gates, status item state and its string key, offsets to places, caret after, excerpts).
- `Hex.Dawn/Code/DawnScene.Prediction.cs`: engine wiring (options, snapshot, edited, tick/take/show, ended, closed).
- `Hex.Dawn/Code/DawnScene.PredictionItem.cs`: commands (Edit > Edit Predictions; Alt+\ Predict Now, Alt+L Accept), status item, first-time notice.
- Setting `edit_predictions_notice_done` (row, Usual). Strings `prediction:` in en-gb.hor. Hooks in DawnScene.Code.cs, DawnScene.StatusBar.cs, DawnScene.cs.

Left:
- Goldens re-recorded for the new key.
- `Hex.Dawn/Testing/HexFakePredictionServer.cs` and `DawnScene.SelfTest.Prediction.cs`, registered in SelfTestSuites.
- `Hex.Tests/PredictionGlueTests.cs`.
- Run: Hex.Tests, self tests prediction, code, settings.

Next step: the fake server.
