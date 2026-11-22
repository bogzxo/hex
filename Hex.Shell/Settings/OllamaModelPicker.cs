using System.Text.Json;
using Hex.Core;
using Horizon.HIDL.Runtime;
using Horizon.UI.Components;

namespace Hex.Shell;

/// <summary>Discovers installed models only while an Ollama model setting is on the settings page.</summary>
internal sealed class OllamaModelPicker(HttpClient? client = null)
{
    private const string ModelKey = "edit_predictions_model";
    private SettingsPage? currentPage;
    private string? endpoint;
    private long nextCheck, nextPoll;
    private CancellationTokenSource? cancel;
    private Task<string[]?>? request;
    private string[]? installed;
    private UISetting? applied;
    private string? selected;

    public void Update(SettingsPage? page) => Update(page, System.Environment.TickCount64);

    internal void Update(SettingsPage? page, long now)
    {
        if (page != currentPage)
        {
            Reset();
            currentPage = page;
            nextCheck = 0;
        }
        if (HexAi.Off || page is null || now < nextCheck)
            return;
        nextCheck = now + 250;
        var row = page.Settings.FirstOrDefault(setting => setting.Key == ModelKey);
        string? url = row is not null && page.ValueOf?.Invoke("edit_predictions_provider") is StringValue { Value: "ollama" }
            && page.ValueOf("edit_predictions_api_url") is StringValue address ? address.Value : null;
        if (url != endpoint)
        {
            Reset();
            endpoint = url;
        }
        if (url is not null && request is null && now >= nextPoll)
        {
            nextPoll = now + 10_000;
            if (PredictionProviders.OllamaUrl(url) is { } api)
            {
                cancel = new();
                request = Discover(api, cancel.Token);
            }
        }
        if (request is { IsCompletedSuccessfully: true })
        {
            installed = request.Result;
            request = null;
            cancel?.Dispose();
            cancel = null;
            applied = null;
        }
        if (row is null)
            return;
        string value = page.ValueOf?.Invoke(ModelKey) is StringValue model ? model.Value : "";
        if (row == applied && selected == value)
            return;
        var next = ModelRow(row, installed, value);
        if (!ReferenceEquals(next, row))
            page.Settings = [.. page.Settings.Select(setting => setting == row ? next : setting)];
        applied = next;
        selected = value;
    }

    private void Reset()
    {
        cancel?.Cancel();
        cancel?.Dispose();
        cancel = null;
        request = null;
        installed = null;
        applied = null;
        endpoint = null;
        nextPoll = 0;
    }

    private async Task<string[]?> Discover(Uri api, CancellationToken token)
    {
        try
        {
            return await OllamaProvider.InstalledModels(client ?? PredictionProviders.Shared, api, token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException or IOException)
        {
            return null;
        }
    }

    internal static UISetting ModelRow(UISetting row, string[]? names, string configured)
    {
        if (names is null)
            return row.Kind == UIFormKind.Text ? row : row with { Kind = UIFormKind.Text, Choices = null, ChoiceLabels = null };
        string[] choices = names.Contains(configured, StringComparer.Ordinal) ? names : [configured, .. names];
        string[] labels = [.. choices.Select(name => name.Length == 0
            ? HexText.Get(names.Length == 0 ? "settings_catalog.ollama_no_models" : "settings_catalog.ollama_select_model")
            : name)];
        if (row.Kind == UIFormKind.Choice && choices.SequenceEqual(row.Choices ?? []) && labels.SequenceEqual(row.ChoiceLabels ?? []))
            return row;
        return row with { Kind = UIFormKind.Choice, Choices = choices, ChoiceLabels = labels };
    }
}
