# Edit prediction settings: state

Branch `feat/edit-prediction-settings` (worktree `hexwt-predict-settings`), based on `feat/edit-prediction` 88e9687.

## Done
- Twelve keys of plan 7: fields in `Hex.Core/Settings/HexSettings.Prediction.cs`, rows spliced into `HexSettings.Rows` as `.. PredictionRows` after `csharp_analyzers_moved`.
- Workspace keys: `edit_predictions_workspace`, `edit_predictions_exclude` only. A folder's exclude adds to the user's (`HexSettings.EditPredictionsExcludeInForce`).
- Key: `Hex.Core/Prediction/PredictionKey.cs` (Wrap / Unwrap / IsProtected / FromEnvironment), written `protected:<base64>` via HexLinkSecrets, plain hand typed key protected at next save, unreadable flagged, unavailable store keeps the blob.
- `Hex.Core/Prediction/PredictionSettings.cs`: On, LanguageOff, Exclude, Provider, ApiUrl, Model, PromptFormat, MaxTokens, Debounce, Share, ApiKey, ApiKeyUnreadable, Configured, UrlForProvider.
- Catalog section `Editor · AI` (after Run and debug), rows depend on `edit_predictions`, Usual defaults, `IsSecret` / `Masked`. Key row is `UIFormKind.Text` with one `// PREDICT-MERGE: UIFormKind.Secret` line.
- Settings window InForce / Apply (provider switch moves Ollama's URL to /v1/completions and back). MCP hex_settings_get shows the key as "set" or "", hex_settings_set refuses it.
- en-gb strings (titles, help, placeholders, choices, keywords).
- Tests: `Hex.Tests/PredictionSettingsTests.cs` (12), goldens re-recorded (only the new rows), key masked in goldens.

## Results
- Build green. Settings tests 40/40. Full Hex.Tests 1332/1332.
- Self test settings at 1600x900: 340 of 340. The check `nav: other parts folded on first open` now scrolls the nav to the Extensions heading first (the new Editor section pushed it 6 px out of sight at 900 px), as #110 agreed.

## Left / next
- Nothing of the brief left. At merge: flip the `// PREDICT-MERGE: UIFormKind.Secret` line in EditorSettingsCatalog.cs.
- Dusk's settings window (FileSectionSource) shows the key row's raw file text (`protected:...`), not the key.

## Build and test
From the worktree, E=C:/Users/bogdan/source/repos/bogzxo/horizon/hex/Hex/bin/Debug/net11.0/data/agents/temp/scratch/predict/engine/
- `dotnet build Hex/Hex.csproj -p:HorizonRoot=$E -p:HidlRoot=${E}Horizon.HIDL/ -v q -nologo`
- `dotnet test Hex.Tests/Hex.Tests.csproj --filter "FullyQualifiedName~Settings" -p:HorizonRoot=$E -p:HidlRoot=${E}Horizon.HIDL/`
- Self test: env HORIZON_AUDIO=off HEX_REMOTE_PIPE=hex-predict-settings-<random> HEX_LINK_KEYSTORE=file, `Hex/bin/Debug/net11.0/Hex.exe --selftest settings --exit --size 1600x900`
