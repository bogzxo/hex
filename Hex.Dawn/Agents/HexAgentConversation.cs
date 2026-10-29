namespace Hex.Dawn;

/// <summary>A user request, with the exact editor snapshot the agent was asked about. Positions are 1 based.</summary>
internal sealed record HexAgentRequest(int Id, string Kind, string Prompt, string Path, int Line, int Column,
    int EndLine, string Source, int Version)
{
    internal string? Reply { get; set; }
    internal bool Cancelled { get; set; }
    internal bool Pending => Reply is null && !Cancelled;
}

/// <summary>The in-memory conversation shared by the editor UI and MCP tools; reading never consumes a request.</summary>
internal sealed class HexAgentConversation
{
    internal const int Limit = 200;
    private int nextId;
    internal List<HexAgentRequest> Requests { get; } = [];

    internal HexAgentRequest Add(string kind, string prompt, string path, int line, int column, int endLine, string source, int version)
    {
        if (Requests.Count >= Limit)
            throw new InvalidOperationException("The conversation is full. Clear it before sending another request.");
        var request = new HexAgentRequest(++nextId, kind, prompt, path, line, column, endLine, source, version);
        Requests.Add(request);
        return request;
    }

    internal HexAgentRequest? Find(int id) => Requests.Find(request => request.Id == id);
    internal void Clear() => Requests.Clear();
}
