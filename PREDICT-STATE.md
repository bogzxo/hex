# Edit prediction core: state

Branch `feat/edit-prediction-core` (worktree `hexwt-predict-core`), based on `feat/edit-prediction` 88e9687.

## Done

Everything in the brief: `Hex.Core/Prediction/` (providers Ollama, OpenAI and Link, HexPredictionRelay,
PredictionProviders, Zeta2Format, FimFormat for qwen, code_gemma, star_coder, code_llama, deepseek_coder and codestral,
PredictionFormats, PredictionDiff, PredictionHistory, PredictionCache, PredictionPrivacy, PredictionLog,
PredictionEngine and the records), and its tests in `Hex.Tests/Prediction*Tests.cs` with the goldens embedded from
`Hex.Tests/Goldens/Prediction/`.

- Prediction tests: 131 green (one of them only asks the real model with HEX_TEST_OLLAMA=1).
- Full Hex.Tests: 1450 passed, 0 failed.
- Zeta 2's prompt for the model card's example is the card's to the byte (the card's file ends with a newline after
  `<[fim-middle]>`, which isn't sent).

Real Zeta 2 (Q4_K_M, RTX 3060, Ollama 0.40.16), the card's example, ~825 prompt tokens, max 256: first piece 2754 ms
and 8501 ms in all when Ollama had to load it again (another model had its memory), then 588 ms to the first piece and
6406 ms in all; 234 pieces, settled early by the four-lines rule, one ghost insertion at the caret
(`uct LanguageEntry {\n    \n}\n`, caret put inside the braces).

## Left

Nothing of the core. The glue (DawnScene) and settings are other workers'.

## Differences from the brief

- Zeta 2 sends no stop sequence: `>>>>>>> UPDATED` is seen in the stream and the request closed then, so an answer cut
  short by the token limit can be told from a finished one.
- Ollama's first piece may take 60 s every time, not only on the run's first request: Ollama loads the model again
  whenever another took its memory (seen here with qwen2.5-coder 14b loaded by another agent). 15 s between pieces.
- PredictionSnapshot(Document, Version, Path, Language, string[] Lines, CaretLine, CaretColumn,
  Func<IReadOnlyList<PredictionExcerpt>>? Related). The edit events come from `PredictionEngine.History`, noted on the
  task when a request is built; the glue calls `History.Accepted` and `History.Closed`.
- The golden test uses Zeta2Format's internal Build overload with explicit ranges (the sample's file is only an
  excerpt); real use takes the simple rule (`Zeta2Format.Ranges`).

## Build and test

```
dotnet build Hex.Tests/Hex.Tests.csproj -p:HorizonRoot=C:/Users/bogdan/source/repos/bogzxo/horizon/hex/Hex/bin/Debug/net11.0/data/agents/temp/scratch/predict/engine/ -p:HidlRoot=C:/Users/bogdan/source/repos/bogzxo/horizon/hex/Hex/bin/Debug/net11.0/data/agents/temp/scratch/predict/engine/Horizon.HIDL/ -v q -nologo
dotnet test Hex.Tests/Hex.Tests.csproj --filter "FullyQualifiedName~Prediction" <same -p: args>
HEX_TEST_OLLAMA=1 dotnet test ... --filter "FullyQualifiedName~PredictionOllamaTests" --logger "console;verbosity=detailed"
```

Ollama needs `OLLAMA_MODELS=D:\ollama\models` (the user's environment has it) to find the two models.
