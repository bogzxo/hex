using System.Text;
using System.Text.Json.Nodes;

namespace Hex.Core;

internal sealed record HexDocAgentChange(string Agent, string Name, string BaseHash, HexDocSplice Splice)
{
    public static string HashText(string text) => HexLinkFiles.Hash(Encoding.UTF8.GetBytes(text));

    public JsonObject Encode(string path, Guid epoch, long revision)
    {
        var request = HexLinkDocumentWire.Open(path, epoch, revision);
        request["agent"] = Agent;
        request["name"] = Name;
        request["base"] = BaseHash;
        request["start"] = Splice.Start;
        request["length"] = Splice.Length;
        request["text"] = Splice.Inserted;
        return request;
    }

    public static HexDocAgentChange Decode(JsonObject request) => new(
        HexLinkDocumentProtocol.Field(request, "agent", 32),
        HexLinkDocumentProtocol.Field(request, "name", 64),
        HexLinkDocumentProtocol.Field(request, "base", 64),
        new HexDocSplice(HexLinkData.Int(request, "start") ?? -1, HexLinkData.Int(request, "length") ?? -1,
            HexLinkDocumentProtocol.Field(request, "text", HexLinkDocumentWire.MaxText)));
}

internal sealed partial class HexLinkDocumentRoom
{
    private const int MaxAgents = 32;
    private readonly Dictionary<string, long> agentUntil = new(StringComparer.Ordinal);

    public HexDocExchange AgentEdit(string scope, object connection, HexLinkDevice source, Guid epoch, long revision, HexDocAgentChange change)
    {
        lock (gate)
        {
            if (online.GetValueOrDefault(scope) != connection || !members.TryGetValue(scope, out var caller))
                throw HexLinkException.NotFound("Join the shared document before editing it as an agent.");
            var basis = authority.View();
            if (authority.Checkpoint.Epoch != epoch || change.BaseHash != HexDocAgentChange.HashText(basis.Text))
                throw new HexLinkException(HexLinkErrors.CONFLICT, "The shared buffer changed. Read its latest edits and try again.");
            if (change.Splice is { Length: 0, Inserted.Length: 0 })
                return State(caller.Author, epoch, revision);

            ExpireAgents();
            string actorScope = scope + "/agent/" + change.Agent;
            if (!agentUntil.ContainsKey(actorScope) && agentUntil.Count >= MaxAgents)
                throw new InvalidOperationException("The shared document reached its active agent limit.");
            string id = HexDocAgentChange.HashText(source.Id + "/agent/" + change.Agent);
            string name = "Agent " + (HexLinkNames.Clean(change.Name) ?? source.Name);
            var agent = new HexLinkDevice(id, name);
            if (!members.TryGetValue(actorScope, out var participant))
            {
                participant = new HexDocParticipant(++lastAuthor, agent.Id, agent.Name);
                members.Add(actorScope, participant);
            }
            online[actorScope] = connection;
            agentUntil[actorScope] = Environment.TickCount64 + 2500;
            authority.Reconnect(participant.Author);
            try
            {
                HexDocSplice splice = change.Splice;
                var edit = basis.Edit(new HexDocId(authority.Clock + 1, participant.Author), splice.Start, splice.Length, splice.Inserted);
                if (edit.Removed.Length > 8192 || HexLinkDocumentAuthority.Estimate(edit) > HexLinkDocumentWire.Budget)
                    throw new ArgumentException("The agent edit is too large.");
                authority.Submit(epoch, participant.Author, edit);
                var after = authority.View();
                // Agent work highlights the edited location, then the author leaves without changing its operations.
                var caret = after.PointAt(splice.Start + splice.Inserted.Length);
                var anchor = after.PointAt(splice.Start);
                members[actorScope] = participant with { Caret = caret, Anchor = anchor };
                roster = null;
                return State(caller.Author, epoch, revision);
            }
            catch
            {
                Leave(actorScope, connection);
                throw;
            }
        }
    }

    private void ExpireAgents()
    {
        if (agentUntil.Count == 0)
            return;
        long now = Environment.TickCount64;
        foreach (var entry in agentUntil.Where(each => each.Value <= now).ToArray())
        {
            if (online.TryGetValue(entry.Key, out var connection))
                Leave(entry.Key, connection);
            else
            {
                if (members.Remove(entry.Key, out var member))
                    authority.Leave(member.Author);
                agentUntil.Remove(entry.Key);
            }
        }
    }
}
