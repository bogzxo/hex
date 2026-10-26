using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Hex.Dawn;

// Hex's MCP server, agents driving the editor the way somebody at it would. Off unless mcp_server is on (or Hex was
// started with --mcp), and then a pipe only this user on this machine can open, behind a token in the settings folder
// (Hex.Core's McpPipeHost and McpDiscovery). The calls come in on the pipe's threads and wait in a queue for the editor's
// own thread, which runs them a tick at a time (a click is a move, a press and a let go) and wakes itself for them, so an
// idle Hex answers at once. While an agent is connected the status bar says so. Off, all a tick costs is a null check.
internal sealed partial class DawnScene
{
    // How often the folders in the file the bridge reads are looked at again, in seconds
    private const float MCP_FOLDERS_EVERY = 1.0f;

    // The one Hex scene in the process with the server up, a Dawn made for the terminal's window too mustn't put up another
    private static DawnScene? mcpOwner;

    private McpPipeHost? mcpHost;
    private McpListing? mcpListing;
    private HexMcpTools? mcpTools;
    private StatusItem mcpItem = null!;
    private readonly ConcurrentQueue<HexMcpCall> mcpCalls = new();
    private HexMcpCall? mcpCurrent;
    private volatile bool mcpChanged;
    private float mcpFoldersWait;
    private int mcpClientsShown;

    // When an agent was last on, the item stays a while after so a client that connects for every call (hexmcp, a
    // script) doesn't have the status bar jumping about as the item comes and goes
    private const long MCP_LINGER_MS = 15_000;
    private long mcpLastOn;

    /// <summary>The status bar's item and the server, when the settings or the command line say so.</summary>
    private void BuildMcp()
    {
        mcpItem = statusBar.Add("$(robot)", right: true, pressed: () =>
        {
            OpenSettings(null);
            settingsView?.Reveal("mcp_server");
        }, tooltip: Said("mcp.status_tip", ("clients", string.Empty)));
        mcpItem.IsVisible = () => mcpClientsShown > 0 || System.Environment.TickCount64 - mcpLastOn < MCP_LINGER_MS;
        ApplyMcp();
    }

    /// <summary>
    /// Whether the server should be up, the setting or --mcp, never for a run of checks. A self test goes by --mcp alone
    /// (the typing suite timed with the server up and idle), the settings it reads are nobody's.
    /// </summary>
    private bool McpWanted => ((settings.McpServer && !options.SelfTest) || options.Mcp) && options.Check.Length == 0;

    /// <summary>The server put up or taken down to match the setting, as its row changes or the file is read again.</summary>
    internal void ApplyMcp()
    {
        if (McpWanted && mcpHost is null)
            StartMcp();
        else if (!McpWanted && mcpHost is not null)
        {
            StopMcp();
            Say(Said("mcp.stopped"));
        }
    }

    /// <summary>Puts the server up, its pipe listening and its file written for the bridge to find. True when it's up.</summary>
    internal bool StartMcp()
    {
        if (mcpHost is not null)
            return true;
        if (mcpOwner is not null && mcpOwner != this)
            return false;

        try
        {
            int pid = System.Environment.ProcessId;
            mcpTools ??= new HexMcpTools(this);
            string token = McpDiscovery.NewToken();
            mcpHost = new McpPipeHost(McpDiscovery.PipeOf(pid), token, MakeMcpServer)
            {
                Changed = () =>
                {
                    mcpChanged = true;
                    Engine.WindowManager.Wake();
                }
            };

            mcpListing = new McpListing(pid, mcpHost.Pipe, token, McpFolders());
            McpDiscovery.Write(mcpListing);
            mcpOwner = this;
            AppDomain.CurrentDomain.ProcessExit -= ForgetMcpFile;
            AppDomain.CurrentDomain.ProcessExit += ForgetMcpFile;
            Say(Said("mcp.started"));
            Console.WriteLine($"[Hex] the MCP server for agents is up on the pipe {mcpHost.Pipe}");
            return true;
        }
        catch (Exception e)
        {
            Say(Said("mcp.failed", ("why", e.Message)), error: true);
            StopMcp();
            return false;
        }
    }

    /// <summary>Takes the server down, its clients cut off, its file gone and the calls still waiting told so.</summary>
    internal void StopMcp()
    {
        mcpHost?.Dispose();
        mcpHost = null;
        if (mcpListing is not null)
            McpDiscovery.Remove(mcpListing.Pid);
        mcpListing = null;
        if (mcpOwner == this)
            mcpOwner = null;

        while (mcpCalls.TryDequeue(out HexMcpCall? waiting))
            waiting.Finish(McpToolResult.Error("Hex's MCP server was switched off."));
        mcpCurrent?.Finish(McpToolResult.Error("Hex's MCP server was switched off."));
        mcpCurrent = null;
        agentPointer.Release();
        mcpChanged = true;
    }

    private static void ForgetMcpFile(object? sender, EventArgs e) => McpDiscovery.Remove(System.Environment.ProcessId);

    /// <summary>A server for one client, its tools the editor's.</summary>
    private McpServer MakeMcpServer() => new(mcpTools!, "hex", HexAbout.Version, Said("mcp.instructions"));

    /// <summary>The folders of the workspace, what the bridge picks a Hex by.</summary>
    private string[] McpFolders() => [.. explorer.workspace.Folders];

    /// <summary>A call from a client, queued for the next tick, the editor woken for it. From the pipe's threads.</summary>
    internal void QueueMcp(HexMcpCall call)
    {
        mcpCalls.Enqueue(call);
        Engine.WindowManager.Wake();
    }

    /// <summary>
    /// Every tick, the calls run a step at a time, the pointer handed back once the real mouse moves, the status bar and
    /// the bridge's file brought up to date. Nothing at all while the server is off and nothing is waiting.
    /// </summary>
    private void UpdateMcp(float dt)
    {
        if (mcpHost is null && mcpCurrent is null && mcpCalls.IsEmpty && !agentPointer.Taken)
            return;

        agentPointer.Update(Engine.Input.Mouse.Position);
        RunMcpCalls();

        if (mcpChanged)
        {
            mcpChanged = false;
            IReadOnlyList<string> clients = mcpHost?.Clients ?? [];
            if (clients.Count > mcpClientsShown && clients.LastOrDefault() is { } newest && newest != "connecting")
                Say(Said("mcp.connected", ("client", newest)));
            mcpClientsShown = clients.Count;
            mcpLastOn = System.Environment.TickCount64;
            mcpItem.Text = clients.Count > 1 ? $"$(robot) {Said("mcp.status_many", ("count", clients.Count.ToString()))}" : $"$(robot) {Said("mcp.status")}";
            mcpItem.Tooltip = Said("mcp.status_tip", ("clients", string.Join(", ", clients)));
        }

        // The folders open now, for a client started in one of them to find this Hex
        if ((mcpFoldersWait -= dt) <= 0.0f && mcpListing is not null)
        {
            mcpFoldersWait = MCP_FOLDERS_EVERY;
            string[] folders = McpFolders();
            if (!folders.SequenceEqual(mcpListing.Folders, PlatformPaths.Comparer))
            {
                mcpListing = mcpListing with { Folders = folders };
                try
                {
                    McpDiscovery.Write(mcpListing);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    /// <summary>
    /// The call going on taken a step further, and the next ones started when it's done. A call that's waiting on
    /// something (a tick, a dialog, a file) says so by yielding nothing, and the editor's woken for its next step.
    /// </summary>
    private void RunMcpCalls()
    {
        for (int started = 0; started < 8; started++)
        {
            if (mcpCurrent is null)
            {
                if (!mcpCalls.TryDequeue(out mcpCurrent))
                    return;
            }

            if (!mcpCurrent.Step())
            {
                // Waiting on the next tick, which comes a tick from now rather than an idle tick's while later. Woken for it
                // instead, an idle loop made the next one at once and the one after that, two thousand ticks a second of a
                // core spinning for as long as a call waited on something (hex_wait_for while Hex was busy)
                Engine.WindowManager.NoteChange();
                return;
            }

            mcpCurrent = null;
        }
    }

    /// <summary>Whether an agent is connected right now, for the self test.</summary>
    internal int McpClients => mcpHost?.Clients.Count ?? 0;

    /// <summary>The tools, for the self test to call the way a client would.</summary>
    internal HexMcpTools McpTools => mcpTools ??= new HexMcpTools(this);
}

/// <summary>
/// One call of a tool waiting for, or running on, the editor's thread. The tool is a run of steps (an iterator), each step
/// on its own tick, null for "not yet" and a result for done. It's given up on past its deadline or when cancelled.
/// </summary>
internal sealed class HexMcpCall(string name, IEnumerator<McpToolResult?> run, float timeout, CancellationToken cancel)
{
    private readonly TaskCompletionSource<McpToolResult> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly long deadline = System.Environment.TickCount64 + (long)(timeout * 1000.0f);

    public Task<McpToolResult> Done => done.Task;

    /// <summary>The next step, true once the call is over (done, failed, timed out or cancelled).</summary>
    public bool Step()
    {
        if (done.Task.IsCompleted)
            return true;

        if (cancel.IsCancellationRequested)
            return Finish(McpToolResult.Error($"{name} was cancelled."));

        if (System.Environment.TickCount64 > deadline)
            return Finish(McpToolResult.Error($"{name} timed out after {timeout:0.#} seconds."));

        try
        {
            if (!run.MoveNext())
                return Finish(McpToolResult.Error($"{name} said nothing."));
            if (run.Current is { } result)
                return Finish(result);
            return false;
        }
        catch (Exception e)
        {
            return Finish(McpToolResult.Error($"{name} went wrong, {e.GetType().Name}: {e.Message}"));
        }
    }

    /// <summary>Ends the call with a result, true so a step can return it.</summary>
    public bool Finish(McpToolResult result)
    {
        if (done.TrySetResult(result))
            run.Dispose();
        return true;
    }
}

/// <summary>
/// A tool of Hex's, what tools/list says of it and how it runs. Most run on the editor's thread a step a tick (Run),
/// one that has nothing of the editor's to touch and takes its time runs off it (RunAway, the self test in a Hex of its own).
/// </summary>
internal sealed record HexMcpTool(McpTool Meta, Func<JsonObject, IEnumerable<McpToolResult?>>? Run, float Timeout = 30.0f,
    Func<JsonObject, CancellationToken, Task<McpToolResult>>? RunAway = null);

/// <summary>The editor's tools as a client sees them, every call queued for the editor's thread (DawnScene.Mcp).</summary>
internal sealed class HexMcpTools : IMcpTools
{
    private readonly DawnScene hex;
    private readonly Dictionary<string, HexMcpTool> byName;

    public IReadOnlyList<McpTool> Tools { get; }

    public HexMcpTools(DawnScene hex)
    {
        this.hex = hex;
        List<HexMcpTool> all = hex.McpToolList();
        byName = all.ToDictionary(tool => tool.Meta.Name, StringComparer.Ordinal);
        Tools = [.. all.Select(tool => tool.Meta)];
    }

    public Task<McpToolResult> Call(string name, JsonObject arguments, CancellationToken cancel)
    {
        if (!byName.TryGetValue(name, out HexMcpTool? tool))
            return Task.FromResult(McpToolResult.Error($"There's no tool called {name}."));

        if (tool.RunAway is { } away)
            return away(arguments, cancel);

        // A call that says how long it may wait (wait_for's timeout) gets that and a little more
        float timeout = arguments.Number("timeout") is { } asked ? (float)Math.Clamp(asked, 0.1, 600.0) + 5.0f : tool.Timeout;
        var call = new HexMcpCall(name, tool.Run!(arguments).GetEnumerator(), timeout, cancel);
        hex.QueueMcp(call);
        return call.Done;
    }
}
