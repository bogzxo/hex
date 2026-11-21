using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Horizon.Rendering;
using Horizon.UI.Components;

namespace Hex.Dawn;

// Runs on (docs/hex-link-plan.md, 5.5): among the pickers over the box, while a new chat is being written, which machine
// it runs on. This device, or any linked device that lets this one steer. With another device picked the first message
// starts the chat there (hex_agent_spawn over the link), the agent system and model pickers list what that device
// offers (read from its hex_agent_list, defensively, with a box to type one when it says none), and the new chat is
// opened on screen as a remote chat. The choice is kept per workspace for the session. Hidden while no device offers
// Steer, so with nothing linked the pickers are what they always were.
internal sealed partial class DawnScene
{
    /// <summary>An agent system another device offers, its id, its name and the models it lists.</summary>
    internal sealed record LinkRemoteSystem(string Id, string Name, bool Missing, string[] Models);

    // The device picked to run new chats on, by workspace, for this session
    private readonly Dictionary<string, string> linkRunsOn = new(StringComparer.OrdinalIgnoreCase);

    // What each device offers, as its hex_agent_list said, and the devices being asked now
    private readonly Dictionary<string, List<LinkRemoteSystem>> linkRemoteSystems = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> linkSystemsAsking = new(StringComparer.OrdinalIgnoreCase);

    // The agent system and model picked for a device, by device
    private readonly Dictionary<string, (string? Agent, string? Model)> linkRemotePicked = new(StringComparer.OrdinalIgnoreCase);

    // The workspace the choice is kept under
    private string LinkWorkspaceKey => explorer.workspace.FilePath ?? explorer.workspace.Folders.FirstOrDefault() ?? string.Empty;

    /// <summary>The linked devices a new chat can run on: those that let this one steer.</summary>
    internal List<HexLinkPeer> LinkRunsOnDevices()
    {
        var devices = new List<HexLinkPeer>();
        if (link is null)
            return devices;
        foreach (HexLinkPeer peer in LinkPeers)
        {
            if (!peer.IsClosed && HexLinkAllowed.Has(peer.Granted, HexLinkRights.Steer))
                devices.Add(peer);
        }

        return devices;
    }

    private void LinkStartAgentOnDevice(string id)
    {
        if (LinkPeerOf(id) is not { } peer || !HexLinkAllowed.Has(peer.Granted, HexLinkRights.Steer))
        {
            Say(Said("link.devices_needs_steer"), error: true);
            return;
        }

        linkWindow?.Close();
        ShowAgentsPanel();
        NewAgentChat();
        LinkPickRunsOn(id);
    }

    /// <summary>The device a new chat runs on, null for this one (or when the one picked can't be steered now).</summary>
    internal HexLinkPeer? LinkRunsOnPeer()
    {
        if (link is null || linkRunsOn.Count == 0 || !linkRunsOn.TryGetValue(LinkWorkspaceKey, out string? id))
            return null;
        HexLinkPeer? peer = LinkPeerOf(id);
        return peer is not null && HexLinkAllowed.Has(peer.Granted, HexLinkRights.Steer) ? peer : null;
    }

    /// <summary>The device a new chat runs on picked, null for this one.</summary>
    internal void LinkPickRunsOn(string? deviceId)
    {
        if (deviceId is null)
            linkRunsOn.Remove(LinkWorkspaceKey);
        else
        {
            linkRunsOn[LinkWorkspaceKey] = deviceId;
            LinkAskSystems(deviceId);
        }

        agentsOptionsDrawn = string.Empty;
        agentsWait = 0.0f;
    }

    /// <summary>What the pickers are made from of Link's, for their key: the devices offered, the one picked and what it offers.</summary>
    private long LinkRunsOnKey()
    {
        if (link is null || LinkPeers.Count == 0)
            return 0;
        long key = 29 + (LinkRemoteOf(agentChat) is not null ? 1 : 0) + (LinkPickersCompact ? 3 : 0);
        foreach (HexLinkPeer peer in LinkRunsOnDevices())
            key = key * 31 + string.GetHashCode(peer.Device.Id, StringComparison.Ordinal) + string.GetHashCode(peer.Device.Name, StringComparison.Ordinal);
        if (LinkRunsOnPeer() is { } there)
        {
            key = key * 31 + string.GetHashCode(there.Device.Id, StringComparison.Ordinal) * 7L;
            key = key * 31 + (linkRemoteSystems.TryGetValue(there.Device.Id, out var systems) ? systems.Count + 1 : 0);
            var (agent, model) = linkRemotePicked.GetValueOrDefault(there.Device.Id);
            key = key * 31 + (agent is null ? 0 : string.GetHashCode(agent, StringComparison.Ordinal)) + (model is null ? 0 : string.GetHashCode(model, StringComparison.Ordinal) * 3L);
        }

        return key;
    }

    /// <summary>
    /// Link's pickers over the box, after the file's: none for a remote chat on screen (true, nothing local to pick for
    /// it), Runs on while a new chat is written and a device offers Steer, and with a device picked that device's agent
    /// system and model in place of this machine's (true). False leaves the rest of the row to the usual pickers.
    /// </summary>
    private bool LinkPickersCompact => agentsCompose.Bounds.Width < 450;

    private bool DrawLinkRunsOn()
    {
        if (LinkRemoteOf(agentChat) is not null)
            return true;
        if (agentChat is not null)
            return false;
        List<HexLinkPeer> devices = LinkRunsOnDevices();
        if (devices.Count == 0)
            return false;

        HexLinkPeer? there = LinkRunsOnPeer();
        string current = there is null ? Said("link.runs_on_here") : LinkNameOf(there.Device.Id, there.Device.Name);
        float wide = MathF.Ceiling((Skin?.Font.Measure(current, 0.135f).X ?? current.Length * 7.0f) + 44.0f);
        bool compact = LinkPickersCompact;
        Button picker = agentsOptions.Add(Picker(compact ? string.Empty : current, "vm", there is not null,
            Said("link.runs_on_tip", ("device", current)), () => { }));
        picker.Size = new System.Numerics.Vector2(compact ? 32 : MathF.Min(wide, 160), 32);
        linkRunsOnPicker = picker;
        picker.OnPressed = () => ContextMenu.Show(chrome, picker.Bounds.PointAt(Origin.TopLeft), LinkRunsOnMenu());
        if (there is null)
            return false;

        // The device's agent systems and models, as it said them, a box to type one when it said none
        string id = there.Device.Id;
        List<LinkRemoteSystem> systems = linkRemoteSystems.GetValueOrDefault(id) ?? [];
        var (agent, model) = linkRemotePicked.GetValueOrDefault(id);
        LinkRemoteSystem? system = systems.FirstOrDefault(each => each.Id == agent) ?? systems.FirstOrDefault(each => !each.Missing);
        string agentShown = system?.Name ?? agent ?? Said("link.runs_on_agent_default");
        Button agentPicker = agentsOptions.Add(Picker(compact ? string.Empty : agentShown, "robot", false, Said("link.runs_on_agent_tip", ("device", current)), () => { }));
        agentPicker.Size = new System.Numerics.Vector2(compact ? 32 : MathF.Min(MathF.Ceiling((Skin?.Font.Measure(agentShown, 0.135f).X ?? 60) + 44.0f), 150), 32);
        agentPicker.OnPressed = () =>
        {
            var menu = new Menu("remote agent");
            foreach (LinkRemoteSystem each in systems)
            {
                string picked = each.Id;
                menu.Add(each.Missing ? Said("link.runs_on_missing", ("name", each.Name)) : each.Name, () => LinkPickRemote(id, picked, null)).IsChecked = () => (linkRemotePicked.GetValueOrDefault(id).Agent ?? system?.Id) == picked;
            }

            menu.AddSeparator();
            menu.Add(Said("link.runs_on_type_agent"), () => AskText(Said("link.runs_on_type_agent"), Said("link.runs_on_type_agent_text"), agent ?? string.Empty, Said("link.rename_button"),
                typed => LinkPickRemote(id, typed.Trim().Length > 0 ? typed.Trim() : null, null)), icon: "edit");
            ContextMenu.Show(chrome, agentPicker.Bounds.PointAt(Origin.TopLeft), menu);
        };

        string modelShown = model ?? Said("agents_panel.model_default");
        Button modelPicker = agentsOptions.Add(Picker(compact ? string.Empty : modelShown, "sparkle", false, Said("agents_panel.model_tip", ("model", modelShown)), () => { }));
        modelPicker.Size = new System.Numerics.Vector2(compact ? 32 : MathF.Min(MathF.Ceiling((Skin?.Font.Measure(modelShown, 0.135f).X ?? 60) + 44.0f), 160), 32);
        modelPicker.OnPressed = () =>
        {
            var menu = new Menu("remote model");
            menu.Add(Said("agents_panel.model_default"), () => LinkPickRemote(id, linkRemotePicked.GetValueOrDefault(id).Agent, null)).IsChecked = () => linkRemotePicked.GetValueOrDefault(id).Model is null;
            foreach (string each in system?.Models ?? [])
            {
                string picked = each;
                menu.Add(each, () => LinkPickRemote(id, linkRemotePicked.GetValueOrDefault(id).Agent ?? system?.Id, picked)).IsChecked = () => linkRemotePicked.GetValueOrDefault(id).Model == picked;
            }

            menu.AddSeparator();
            menu.Add(Said("link.runs_on_type_model"), () => AskText(Said("link.runs_on_type_model"), Said("link.runs_on_type_model_text"), model ?? string.Empty, Said("link.rename_button"),
                typed => LinkPickRemote(id, linkRemotePicked.GetValueOrDefault(id).Agent ?? system?.Id, typed.Trim().Length > 0 ? typed.Trim() : null)), icon: "edit");
            ContextMenu.Show(chrome, modelPicker.Bounds.PointAt(Origin.TopLeft), menu);
        };
        return true;
    }

    // The Runs on picker as last drawn, for the self test
    private Button? linkRunsOnPicker;

    /// <summary>The Runs on menu: this device, and each linked device that lets this one steer.</summary>
    internal Menu LinkRunsOnMenu()
    {
        var menu = new Menu("runs on");
        menu.Add(Said("link.runs_on_here"), () => LinkPickRunsOn(null), icon: "vm").IsChecked = () => LinkRunsOnPeer() is null;
        foreach (HexLinkPeer peer in LinkRunsOnDevices())
        {
            string id = peer.Device.Id;
            menu.Add(LinkNameOf(id, peer.Device.Name), () => LinkPickRunsOn(id), icon: "vm").IsChecked = () => LinkRunsOnPeer()?.Device.Id == id;
        }

        return menu;
    }

    private void LinkPickRemote(string device, string? agent, string? model)
    {
        linkRemotePicked[device] = (agent, model);
        agentsOptionsDrawn = string.Empty;
        agentsWait = 0.0f;
    }

    // What a device offers asked of it once a session, its answer read when it comes
    private void LinkAskSystems(string deviceId)
    {
        if (linkRemoteSystems.ContainsKey(deviceId) || LinkPeerOf(deviceId) is not { } peer || !linkSystemsAsking.Add(deviceId))
            return;
        _ = Task.Run(async () =>
        {
            List<LinkRemoteSystem> systems = [];
            try
            {
                McpToolResult listed = await peer.Call("hex_agent_list", []);
                if (!listed.IsError)
                    systems = LinkSystemsOf(listed.AllText());
            }
            catch (Exception)
            {
                // Nothing offered then, the boxes to type one are there
            }

            PostLink(() =>
            {
                linkSystemsAsking.Remove(deviceId);
                linkRemoteSystems[deviceId] = systems;
                agentsOptionsDrawn = string.Empty;
            });
        });
    }

    /// <summary>
    /// The agent systems hex_agent_list names, read from its words, which are another machine's and may be anything:
    /// the lines under "Agent systems:" of the shape "- id (Name), model: a | b, ...", the rest passed over, at most
    /// sixteen systems of sixty-four models each.
    /// </summary>
    internal static List<LinkRemoteSystem> LinkSystemsOf(string said)
    {
        var systems = new List<LinkRemoteSystem>();
        bool under = false;
        foreach (string raw in said.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("Agent systems:", StringComparison.Ordinal))
            {
                under = true;
                continue;
            }

            if (!under)
                continue;
            if (!line.StartsWith("- ", StringComparison.Ordinal))
            {
                if (line.Trim().Length == 0 || line.StartsWith("Chats:", StringComparison.Ordinal))
                    break;
                continue;
            }

            Match match = SystemLine().Match(line);
            if (!match.Success)
                continue;
            string id = match.Groups["id"].Value;
            string name = match.Groups["name"].Value;
            bool missing = name.EndsWith(", not installed", StringComparison.Ordinal);
            if (missing)
                name = name[..^", not installed".Length];
            string[] models = [];
            foreach (string part in match.Groups["rest"].Value.Split(", ", StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.StartsWith("model: ", StringComparison.Ordinal))
                    models = [.. part["model: ".Length..].Split(" | ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(model => model.Length is > 0 and <= 120).Take(64)];
            }

            systems.Add(new LinkRemoteSystem(id, name.Length > 0 ? OneLine(name, 60) : id, missing, models));
            if (systems.Count == 16)
                break;
        }

        return systems;
    }

    [GeneratedRegex(@"^- (?<id>[A-Za-z0-9_.\-]{1,80}) \((?<name>[^)]{0,120})\)(?<rest>.*)$")]
    private static partial Regex SystemLine();

    /// <summary>A new chat started on a device with what was typed, opened on screen once the device says its number.</summary>
    private void LinkSpawnThere(HexLinkPeer peer, string prompt)
    {
        string id = peer.Device.Id, name = LinkNameOf(peer.Device.Id, peer.Device.Name);
        var (agent, model) = linkRemotePicked.GetValueOrDefault(id);
        agent ??= linkRemoteSystems.GetValueOrDefault(id)?.FirstOrDefault(each => !each.Missing)?.Id;
        var arguments = new JsonObject { ["prompt"] = prompt };
        if (agent is not null)
            arguments["agent"] = agent;
        if (model is not null)
            arguments["model"] = model;
        Say(Said("link.runs_on_starting", ("device", name)));
        _ = Task.Run(async () =>
        {
            try
            {
                McpToolResult started = await peer.Call("hex_agent_spawn", arguments);
                string said = started.AllText();
                Match number = Regex.Match(said, @"Started #(\d{1,9})");
                PostLink(() =>
                {
                    if (started.IsError || !number.Success || !int.TryParse(number.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int chat))
                    {
                        Say(Said("link.runs_on_failed", ("device", name), ("why", OneLine(said, 200))), error: true);
                        return;
                    }

                    if (agentChat is null)
                        OpenRemoteChat(new LinkRemoteCard(id, name, chat));
                });
            }
            catch (Exception e)
            {
                PostLink(() => Say(Said("link.runs_on_failed", ("device", name), ("why", e.Message)), error: true));
            }
        });
    }
}
