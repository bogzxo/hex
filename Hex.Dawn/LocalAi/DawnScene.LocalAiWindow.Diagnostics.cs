using System.Globalization;
using System.Numerics;
using System.Text;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The Local AI window's Diagnostics page (the server's log, the router's models and how each stands, graphics memory, the Time It
// history and a report to copy) and its Glossary.
internal sealed partial class DawnScene
{
    // Each time a model was timed this run, for the Diagnostics page
    private readonly List<(DateTime At, string Model, LlamaCppSpeed Speed)> localAiTimes = [];

    private void DrawLocalAiDiagnostics(StackPanel body)
    {
        LlamaCppServer server = LlamaServer;
        body.Add(LinkHeading(Said("local_ai.diag_server")));
        llamaServerText = body.Add(LinkLine(settings.LocalAiEngine == HexSettings.LOCAL_AI_OLLAMA
            ? Said("local_ai.ollama_at", ("url", settings.EditPredictionsApiUrl)) : LlamaServerStateText(server)));
        StackPanel actions = LinkActionRow(body);
        actions.Add(LinkButton(Said("local_ai.diag_copy_report"), "copy", () =>
        {
            UIClipboard.Copy(LocalAiReport());
            Say(Said("local_ai.diag_copied"));
        }));
        Button measure = actions.Add(LinkButton(Said(llamaMeasuring ? "llamacpp.measuring" : "llamacpp.measure"), llamaMeasuring ? "loading~spin" : "dashboard", MeasureLlama));
        measure.Primary = true;
        measure.Enabled = !llamaMeasuring && server.State == LlamaCppServerState.Ready;

        body.Add(LinkHeading(Said("local_ai.diag_log")));
        // The log reads like the terminal: its background, the code font and its padding
        var box = body.Add(new Panel { Fill = UIFill.Horizontal, Radius = 6, Padding = new UIEdges(10, 8, 10, 8), Color = Skin is { } look ? TerminalPalette.FromPalette(look.Palette).Background : null });
        // At least three lines tall, so a short log doesn't look like a broken box; an empty one says what the box is for
        string tail = LlamaLogTail(server);
        if (tail.Count(character => character == '\n') < 2)
            box.Size = new Vector2(0, 3 * 15 + 16);
        if (tail.Length == 0)
            box.Add(new Label { Text = UIRichText.Literal(Said("local_ai.diag_log_empty")), TextScale = LINK_SMALL, Dim = true, Wrap = true, Fill = UIFill.Horizontal, Align = Origin.TopLeft, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft });
        else
        // The terminal's font once it has made one (it does so the first time a terminal is wanted), the code font until then
        llamaLogText = box.Add(new Label { Text = LlamaLogTail(server), CodeFont = terminalFontSet ? terminalPanel.Font : code.Font, TextScale = 0.11f, Wrap = true, Fill = UIFill.Horizontal, Align = Origin.TopLeft, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft });

        body.Add(LinkHeading(Said("local_ai.diag_router")));
        if (server.Config is { } config && settings.LocalAiEngine == HexSettings.LOCAL_AI_LLAMACPP)
        {
            // Every model the router lists with the state it says; the configured aliases only until it has answered
            if (localAiRouter is { Count: > 0 } router)
            {
                foreach (LlamaCppRouterModel entry in router)
                {
                    bool loaded = entry.State == "loaded";
                    string text = entry.State is "loaded" ? Said("local_ai.diag_loaded", ("model", LocalAiFriendlyName(entry.Id)))
                        : entry.State is "unloaded" ? Said("local_ai.diag_unloaded", ("model", LocalAiFriendlyName(entry.Id)))
                        : Said("local_ai.diag_state", ("model", LocalAiFriendlyName(entry.Id)), ("state", entry.State));
                    body.Add(LinkLine((loaded ? "$(check) " : "$(dash) ") + text));
                }
            }
            else
            {
                foreach (string alias in new[] { config.Alias }.Concat(config.Extras.Select(extra => extra.Alias)))
                    body.Add(LinkLine("$(dash) " + Said("local_ai.diag_unknown", ("model", LocalAiFriendlyName(alias)))));
            }
        }
        else
            body.Add(LinkLine(Said("local_ai.diag_router_none")));

        body.Add(LinkHeading(Said("local_ai.diag_vram")));
        localAiVramText = body.Add(LinkLine(LocalAiVramWords()));

        body.Add(LinkHeading(Said("local_ai.diag_times")));
        if (localAiTimes.Count == 0)
            body.Add(LinkLine(Said("local_ai.diag_times_none")));
        foreach (var (at, model, speed) in Enumerable.Reverse(localAiTimes).Take(12))
            body.Add(LinkLine(at.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + Said("local_ai.test_speed", ("model", model),
                ("read", speed.PromptPerSecond.ToString("0", CultureInfo.InvariantCulture)), ("wrote", speed.AnswerPerSecond.ToString("0.0", CultureInfo.InvariantCulture)))));
    }

    // A server's alias for a model as the library names it, the alias itself when the library doesn't know it
    private string LocalAiFriendlyName(string alias)
    {
        LocalModelLibrary library = LocalModelLibrary.Shared;
        string? known = library.Find(alias)?.Name ?? LlamaCppModels.Find(alias)?.Title ?? library.Models.FirstOrDefault(model => string.Equals(model.Name, alias, StringComparison.OrdinalIgnoreCase)
            || (model.Path is { } file && string.Equals(Path.GetFileNameWithoutExtension(file), alias, StringComparison.OrdinalIgnoreCase)))?.Name;
        if (known is not null)
            return known;
        // A router alias ("local") is named by the file its status args load, or by the file the server was configured with
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (LlamaCppRouterModel entry in localAiRouter ?? [])
        {
            if (entry.File is { Length: > 0 } file)
                files[entry.Id] = file;
        }
        if (llamaServer?.Config is { } config && !files.ContainsKey(config.Alias) && config.Model.Length > 0)
            files[config.Alias] = Path.GetFileName(config.Model.Replace('\\', '/'));
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string id, string file) in files)
        {
            string name = library.Models.FirstOrDefault(model => model.Path is { } path && string.Equals(Path.GetFileName(path), file, StringComparison.OrdinalIgnoreCase))?.Name
                ?? Path.GetFileNameWithoutExtension(file);
            names[id] = name;
        }
        return LocalAiWindowState.DisplayNameOf(alias, names);
    }

    // Everything someone helping would ask for, as text
    private string LocalAiReport()
    {
        LocalAiRoleModels roles = LocalAiRoles.Resolve(settings, LocalModelLibrary.Shared);
        var report = new StringBuilder();
        report.AppendLine("Hex local AI report");
        report.AppendLine($"Engine: {settings.LocalAiEngine}");
        report.AppendLine($"Setup: {(settings.LocalAiSetup.Length > 0 ? settings.LocalAiSetup : "none")}  Mode: {LocalAiMode}");
        report.AppendLine($"Predictions model: {roles.Predict?.Name ?? "none"}");
        report.AppendLine($"Chat model: {roles.Chat?.Name ?? "none"}");
        report.AppendLine($"Server: {settings.LlamaCppServer}  port {settings.LlamaCppPort}  state {llamaServer?.State.ToString() ?? "not made"}");
        if (llamaServer?.Problem is { Length: > 0 } problem)
            report.AppendLine($"Problem: {problem}");
        report.AppendLine($"Graphics memory: {LocalAiVramWords()}");
        if (llamaPlatform is { } platform)
            report.AppendLine($"Machine: {platform.Os} {platform.Arch}, {platform.MemoryMiB / 1024} GB memory, {platform.Gpu?.Name ?? "no GPU"}");
        foreach (var (at, model, speed) in localAiTimes)
            report.AppendLine($"Time It {at:HH:mm:ss} {model}: read {speed.PromptPerSecond:0} tok/s, wrote {speed.AnswerPerSecond:0.0} tok/s");
        if (llamaServer is { } server)
        {
            report.AppendLine("Log (last lines):");
            foreach (string line in server.Log.TakeLast(40))
                report.AppendLine(line);
        }
        return report.ToString();
    }

    /* Glossary */

    private void DrawLocalAiGlossary(StackPanel body)
    {
        LocalAiHeading(body, Said("local_ai.glossary_heading"));
        LocalAiNote(body, Said("local_ai.glossary_intro"));
        foreach (string key in LocalAiGlossaryKeys)
        {
            // A term is a row without a control: its short line is the definition and the "?" says more
            LocalAiExplainedRow(body, key, new Spacer { Size = new Vector2(1, LINK_ROW) });
        }
    }
}
