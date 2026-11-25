namespace Hex.Dawn;

// The four-step first run is retired (docs/local-ai-window-plan.md, 2): the setup gallery is the first run, see
// DawnScene.LocalAiWindow.Setups.cs. What stays is what a role's model does to the engine's own settings.
internal sealed partial class DawnScene
{
    // The engine's own settings made to match the model of the Predictions role (the server's model file and alias, Ollama's model name)
    private void ApplyPredictModel(LocalModel predict)
    {
        if (settings.LocalAiEngine == HexSettings.LOCAL_AI_OLLAMA)
        {
            settings.EditPredictionsProvider = "ollama";
            settings.EditPredictionsModel = predict.Id.StartsWith("ollama:", StringComparison.OrdinalIgnoreCase) ? predict.Id[7..] : predict.Name;
            return;
        }
        settings.EditPredictionsProvider = PredictionProviders.LLAMACPP;
        // A model downloaded a moment ago may not be in the library's scan yet: its file is looked for by its id as well
        if ((predict.Path ?? settings.LocalAiFileOf(predict.Id)) is { } path && File.Exists(path))
        {
            settings.LlamaCppModel = path;
            settings.LlamaCppAlias = predict.CatalogId ?? Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        }
        settings.EditPredictionsPromptFormat = predict.CatalogId is { } catalogId && LlamaCppModels.Find(catalogId) is { } known ? known.PromptFormat : PredictionFormats.INFER;
    }
}
