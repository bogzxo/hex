# Edit prediction core: state

Branch `feat/edit-prediction-core` (worktree `hexwt-predict-core`), based on `feat/edit-prediction` 88e9687.

## Done (builds; 130 Prediction tests green, the Zeta 2 golden byte for byte)

`Hex.Core/Prediction/`: PredictionStatus, PredictionException, PredictionWireRequest, IPredictionProvider (public);
PredictionOptions, PredictionEdit, PredictionEvent, PredictionExcerpt, PredictionInput, PredictionPrompt,
PredictionParsed, IPredictionFormat, PredictionTiming, PredictionResult, PredictionSnapshot, PredictionTokens,
PredictionHttp (JSON body, send with deadline, line reader, error -> status), OllamaProvider, OpenAiProvider,
LinkProvider, HexPredictionRelay, PredictionProviders (shared HttpClient, URL normalising, IsLocal), PredictionDiff
(GitLineDiff + char narrowing, IsGhost), Zeta2Format (prompt as Zed's seed_coder, internal Build overload with explicit
region/context ranges, streaming early stop, cut-short alignment), FimFormat (qwen, code_gemma, star_coder, code_llama,
deepseek_coder, codestral), PredictionFormats, PredictionHistory, PredictionCache, PredictionPrivacy, PredictionLog,
PredictionEngine.

Goldens copied: `Hex.Tests/Goldens/Prediction/zeta2.prompt`, `zeta2.output` (the model card's sample).

## Left

1. Next step: the real-model test once, then the full Hex.Tests run.
2. Hex.Tests: embed the goldens (csproj EmbeddedResource or copy to output), tests in `Prediction*Tests.cs`: formats
   (golden byte for byte, budgets, markers refused, FIM tokens, infer), answers, PredictionDiff, providers with a fake
   HttpMessageHandler (bodies, NDJSON, SSE, split chunks, errors -> status, key never in messages, cancel closes the
   response), engine with a fake TimeProvider and scripted provider, history, privacy, relay.
3. Env-gated real Zeta 2 test (HEX_TEST_OLLAMA=1), run once, record numbers.
4. Full Hex.Tests run, commit, report to #110.

## Differences from the brief so far

- Zeta 2 sends no stop sequence: `>>>>>>> UPDATED` is seen in the stream and the request closed then, so an answer cut
  short by the token limit can be told from a finished one.
- PredictionSnapshot has `Func<IReadOnlyList<PredictionExcerpt>>? Related` (asked off the editor's thread); the edit
  events come from `PredictionEngine.History`, noted on the task.
- The golden test uses Zeta2Format's internal Build overload with explicit ranges (the sample's file is only an excerpt).

## Build and test

```
dotnet build Hex.Tests/Hex.Tests.csproj -p:HorizonRoot=C:/Users/bogdan/source/repos/bogzxo/horizon/hex/Hex/bin/Debug/net11.0/data/agents/temp/scratch/predict/engine/ -p:HidlRoot=C:/Users/bogdan/source/repos/bogzxo/horizon/hex/Hex/bin/Debug/net11.0/data/agents/temp/scratch/predict/engine/Horizon.HIDL/ -v q -nologo
dotnet test Hex.Tests/Hex.Tests.csproj --filter "FullyQualifiedName~Prediction" <same -p: args>
```
