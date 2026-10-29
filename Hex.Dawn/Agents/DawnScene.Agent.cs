using System.Numerics;
using System.Text;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private readonly HexAgentConversation agentConversation = new();
    private readonly Dictionary<int, HexDocument> agentTargets = [];
    private readonly Dictionary<HexDocument, int> agentHintVersions = [];
    private WindowPanel? agentWindow;
    private Label? agentTranscript, agentStatus;
    private ScrollPanel? agentScroll;
    private TextBox? agentInput;
    private bool agentTranscriptDirty = true;

    private void BuildAgent()
    {
        commands.Add("View/agent", Said("agent.title"), OpenAgentWindow, icon: "robot");
        commands.Add("Edit/agent", Said("agent.explain"), () => RequestAgent("explain"), icon: "robot").IsEnabled = CanAskAgent;
        commands.Add("Edit/agent", Said("agent.change"), () => RequestAgent("change"), icon: "edit").IsEnabled = CanAskAgent;
    }

    private bool CanAskAgent() => document.DiffOf is null && document.Picture is null && !code.ReadOnly;

    private void AddAgentMenu(Menu menu)
    {
        menu.Add(Said("agent.explain"), () => RequestAgent("explain"), icon: "robot").IsEnabled = CanAskAgent;
        menu.Add(Said("agent.change"), () => RequestAgent("change"), icon: "edit").IsEnabled = CanAskAgent;
        menu.Add(Said("agent.title"), OpenAgentWindow, icon: "comment-discussion");
        menu.AddSeparator();
    }

    private void OpenAgentWindow()
    {
        if (agentWindow is { Parent: not null } existing)
        {
            existing.Show(true);
            existing.BringToFront();
            agentInput?.Focus();
            return;
        }
        var made = layout.Instantiate("agent.hor", chrome.Root);
        agentWindow = made.Get<WindowPanel>("agent_window");
        DressWindow(agentWindow);
        agentWindow.OnClosed = () =>
        {
            agentWindow?.Parent?.Remove(agentWindow);
            agentWindow = null;
            agentInput = null;
        };
        agentTranscript = made.Get<Label>("agent_transcript");
        agentStatus = made.Get<Label>("agent_status");
        agentScroll = made.Get<ScrollPanel>("agent_scroll");
        agentInput = made.Get<TextBox>("agent_input");
        agentInput.OnSubmitted = _ => SendAgentChat();
        made.Get<Button>("agent_send").OnPressed = SendAgentChat;
        made.Get<Button>("agent_cancel").OnPressed = () =>
        {
            foreach (var request in agentConversation.Requests.Where(request => request.Pending)) request.Cancelled = true;
            agentTranscriptDirty = true;
        };
        made.Get<Button>("agent_clear").OnPressed = () =>
        {
            agentConversation.Clear();
            agentTargets.Clear();
            agentTranscriptDirty = true;
        };
        agentTranscriptDirty = true;
        agentInput.Focus();
    }

    private void SendAgentChat()
    {
        if (agentInput is not { Text: { } text } || string.IsNullOrWhiteSpace(text))
            return;
        if (QueueAgentRequest("chat", text.Trim(), document, code.Document.Caret.Line, code.Document.Caret.Line))
            agentInput.Text = string.Empty;
    }

    private void RequestAgent(string kind)
    {
        if (!CanAskAgent()) return;
        HexDocument target = document;
        var text = code.Document;
        int line = text.HasSelection ? text.SelectionStart.Line : text.Caret.Line;
        int end = text.HasSelection ? text.SelectionEnd.Line : line;
        if (end > line && text.SelectionEnd.Column == 0) end--;
        if (kind == "explain")
            QueueAgentRequest(kind, Said("agent.explain_prompt"), target, line, end);
        else
            Context.Windows.Prompt(Said("agent.change"), Said("agent.change_prompt"), string.Empty, Said("agent.send"), string.Empty,
                prompt => { if (!string.IsNullOrWhiteSpace(prompt)) QueueAgentRequest(kind, prompt, target, line, end); });
    }

    private bool QueueAgentRequest(string kind, string prompt, HexDocument target, int first, int last)
    {
        if (!documents.Contains(target) || first < 0 || last >= target.Editor.Document.LineCount) return false;
        if (mcpHost is null || McpClients == 0)
        {
            Say(Said("agent.not_connected"));
            return false;
        }
        var text = target.Editor.Document;
        string source = string.Join('\n', Enumerable.Range(first, last - first + 1).Select(text.LineAt));
        if (source.Length > 32000 || agentConversation.Requests.Count >= HexAgentConversation.Limit)
        {
            Say(Said("agent.too_large"));
            return false;
        }
        var request = agentConversation.Add(kind, prompt, target.Path ?? target.Name, first + 1, text.Caret.Column + 1,
            last + 1, source, text.Version);
        agentTargets[request.Id] = target;
        agentTranscriptDirty = true;
        OpenAgentWindow();
        return true;
    }

    private void UpdateAgent()
    {
        foreach (var (target, version) in agentHintVersions.ToArray())
        {
            if (!documents.Contains(target) || target.Editor.Document.Version != version)
            {
                target.Editor.AgentHints = [];
                agentHintVersions.Remove(target);
            }
        }
        if (agentWindow is null) return;
        agentTranscript!.Size = agentTranscript.Size with { X = MathF.Max(100, agentScroll!.Bounds.Width - 20) };
        agentStatus!.Text = Said(McpClients > 0 ? "agent.connected" : "agent.disconnected");
        if (!agentTranscriptDirty) return;
        agentTranscriptDirty = false;
        var log = new StringBuilder();
        foreach (var request in agentConversation.Requests)
        {
            log.AppendLine($"#{request.Id}  {request.Kind} · {Path.GetFileName(request.Path)}:{request.Line}");
            log.AppendLine(request.Prompt);
            log.AppendLine(request.Cancelled ? Said("agent.cancelled") : request.Reply ?? Said("agent.waiting"));
            log.AppendLine();
        }
        agentTranscript.Text = UIRichText.Literal(log.Length == 0 ? Said("agent.welcome") : log.ToString());
        agentScroll!.ScrollToEnd();
    }
}
