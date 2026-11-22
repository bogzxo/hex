using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Hex.Dawn;
using Horizon.Core.Platform;
using Horizon.UI.Components;
using Horizon.UI.Scripting;

namespace Hex.Tests;

public sealed class MergeResultLanguageTests
{
    private static readonly TimeSpan ProtocolTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ResultAndOrdinaryDocumentUseTheSameUriWithIndependentTextAndLifecycle()
    {
        string root = Path.GetFullPath(Path.GetTempPath());
        string path = Path.Combine(root, "merge sample.py");
        string uri = new Uri(path).AbsoluteUri;
        using var ordinary = new ServerPeer(root, allowWorkspaceEdits: true);
        using var result = new ServerPeer(root, allowWorkspaceEdits: false);
        using var session = result.OpenSession(path);
        JsonNode initialize = await result.Take("initialize");
        Assert.Equal(new Uri(root).AbsoluteUri, (string?)initialize["rootUri"]);
        Assert.False((bool?)initialize["capabilities"]?["workspace"]?["applyEdit"]);
        await ordinary.Take("initialize");

        ordinary.Server.Open(path, "ordinary text");
        session.Send(7, "initial result");
        await AssertOpened(ordinary, uri, "ordinary text");
        await AssertOpened(result, uri, "initial result");

        Assert.False(session.Observe(8, 0.1f, 0.3f));
        Assert.True(session.Observe(8, 0.3f, 0.3f));
        session.Send(8, "edited result");
        JsonNode change = await result.Take("textDocument/didChange");
        Assert.Equal(uri, (string?)change["textDocument"]?["uri"]);
        Assert.Equal("edited result", (string?)change["contentChanges"]?[0]?["text"]);
        Assert.Equal(1, ordinary.Server.DocumentVersion(path));

        session.Dispose();
        JsonNode closed = await result.Take("textDocument/didClose");
        Assert.Equal(uri, (string?)closed["textDocument"]?["uri"]);
        Assert.Null(result.Server.DocumentVersion(path));
        Assert.Equal(1, ordinary.Server.DocumentVersion(path));
    }

    [Fact]
    public async Task UnsentAndOldVersionDiagnosticsAreDiscarded()
    {
        string root = Path.GetFullPath(Path.GetTempPath());
        string path = Path.Combine(root, "result.py");
        using var peer = new ServerPeer(root, allowWorkspaceEdits: false);
        using var session = peer.OpenSession(path);
        await peer.Take("initialize");
        session.Send(13, "initial result");
        await peer.Take("textDocument/didOpen");

        await peer.Publish(path, 1, "stale while editing");
        Assert.Null(session.TakeDiagnostics(14));

        session.Send(14, "edited result");
        await peer.Take("textDocument/didChange");
        await peer.Publish(path, 1, "late old version");
        await peer.Publish(Path.Combine(root, "ordinary.py"), 2, "another file");
        Assert.Null(session.TakeDiagnostics(14));

        await peer.Publish(path, 2, "current result");
        var diagnostics = Assert.IsAssignableFrom<IReadOnlyList<UICodeDiagnostic>>(session.TakeDiagnostics(14));
        Assert.Equal("current result", Assert.Single(diagnostics).Message);
    }

    [Fact]
    public async Task PreviewRefusesServerInitiatedWorkspaceEdits()
    {
        string root = Path.GetFullPath(Path.GetTempPath());
        string path = Path.Combine(root, "ordinary.py");
        using var peer = new ServerPeer(root, allowWorkspaceEdits: false);
        await peer.Take("initialize");

        string uri = new Uri(path).AbsoluteUri;
        var edit = new JsonObject
        {
            ["edit"] = new JsonObject
            {
                ["changes"] = new JsonObject { [uri] = new JsonArray(new JsonObject { ["range"] = Range(), ["newText"] = "server edit" }) }
            }
        };
        JsonNode? response = await peer.Rpc.Request("workspace/applyEdit", edit).WaitAsync(ProtocolTimeout);

        Assert.False((bool?)response?["applied"]);
        Assert.False(peer.Server.TryTakeEdits(out _));
    }

    [Fact]
    public void MissingServerLeavesTheResultWithoutAProtocolSession()
    {
        string missing = Path.Combine(Path.GetTempPath(), "hex-missing-" + Guid.NewGuid().ToString("N"));
        var entry = new LanguageServers.Registration("missing-merge-server", missing, [], "python", [".py"]);

        Assert.Null(LanguageServers.StartIsolated(entry, Path.GetTempPath()));
    }

    [RealMergeLanguageServerFact]
    public async Task PyrightUsesUnsavedResultAndTheOriginalProjectContext()
    {
        var directory = Directory.CreateTempSubdirectory("hex-merge-pyright-");
        string root = directory.FullName;
        try
        {
            string path = Path.Combine(root, "merge.py");
            string helper = Path.Combine(root, "helper.py");
            const string saved = "ordinary_only = 1\nordinary_undefined\n";
            const string edited = "from helper import project_value\nresult_only = project_value\nresult_undefined\nresult_only\n";
            File.WriteAllText(path, saved);
            File.WriteAllText(helper, "project_value = 41\n");

            var entry = new LanguageServers.Registration("pyright-merge-probe", "pyright-langserver", ["--stdio"], "python", [".py"],
                JsonNode.Parse("""{"python":{"analysis":{"typeCheckingMode":"basic","diagnosticMode":"openFilesOnly"}}}""")!.AsObject());
            using var ordinary = Assert.IsType<LanguageServer>(LanguageServers.StartIsolated(entry, root));
            using var session = new MergeResultLanguageSession(path, Assert.IsType<LanguageServer>(LanguageServers.StartIsolated(entry, root)));
            Assert.DoesNotContain(session.Server, LanguageServers.Running);
            await Until(() => ordinary.IsReady && session.Server.IsReady, () => ordinary.Failure ?? session.Server.Failure);
            ordinary.Open(path, saved);
            session.Send(1, "initial_result = 0\n");
            session.Send(2, edited);

            await Until(() => session.TakeDiagnostics(2)?.Any(item => item.Message.Contains("result_undefined", StringComparison.Ordinal)) == true,
                () => session.Server.Failure);
            await Until(() => ordinary.TryTake(out _, out var items)
                && items.Any(item => item.Message.Contains("ordinary_undefined", StringComparison.Ordinal)), () => ordinary.Failure);

            string? hover = await session.Server.Hover(path, 3, 4).WaitAsync(ProtocolTimeout);
            Assert.Contains("result_only", hover ?? string.Empty, StringComparison.Ordinal);
            var completions = await session.Server.Complete(path, 3, 8, 0).WaitAsync(ProtocolTimeout);
            Assert.Contains(completions?.Items ?? [], item => item.Text == "result_only");
            Assert.DoesNotContain(completions?.Items ?? [], item => item.Text == "ordinary_only");

            var local = Assert.Single(await session.Server.Definition(path, 3, 4).WaitAsync(ProtocolTimeout));
            Assert.True(PlatformPaths.Same(path, local.Path));
            Assert.Equal(1, local.Line);
            var imported = Assert.Single(await session.Server.Definition(path, 1, 18).WaitAsync(ProtocolTimeout));
            Assert.True(PlatformPaths.Same(helper, imported.Path));

            session.Dispose();
            Assert.Equal(saved, File.ReadAllText(path));
            Assert.Equal(1, ordinary.DocumentVersion(path));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static async Task AssertOpened(ServerPeer peer, string uri, string text)
    {
        JsonNode message = await peer.Take("textDocument/didOpen");
        Assert.Equal(uri, (string?)message["textDocument"]?["uri"]);
        Assert.Equal(text, (string?)message["textDocument"]?["text"]);
    }

    private static async Task Until(Func<bool> condition, Func<string?> serverFailure)
    {
        using var timeout = new CancellationTokenSource(ProtocolTimeout);
        while (!condition())
        {
            string? failure = serverFailure();
            Assert.Null(failure);
            await Task.Delay(25, timeout.Token);
        }
    }

    private static JsonObject Range() => new()
    {
        ["start"] = new JsonObject { ["line"] = 0, ["character"] = 0 },
        ["end"] = new JsonObject { ["line"] = 0, ["character"] = 1 }
    };

    private sealed class RealMergeLanguageServerFactAttribute : FactAttribute
    {
        public RealMergeLanguageServerFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("HEX_TEST_REAL_MERGE_LSP") != "1")
                Skip = "Set HEX_TEST_REAL_MERGE_LSP=1 with Pyright installed to run the real server test.";
        }
    }

    private sealed class ServerPeer : IDisposable
    {
        private readonly TcpClient client = new();
        private readonly TcpClient remote;
        private bool ownsServer = true;
        private readonly Channel<(string Method, JsonNode? Parameters)> messages = Channel.CreateUnbounded<(string, JsonNode?)>();

        public ServerPeer(string root, bool allowWorkspaceEdits)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            client.Connect((IPEndPoint)listener.LocalEndpoint);
            remote = listener.AcceptTcpClient();
            NetworkStream wire = remote.GetStream();
            Rpc = new JsonRpcConnection(wire, wire)
            {
                OnRequest = (method, parameters) =>
                {
                    if (method != "initialize")
                        return null;
                    messages.Writer.TryWrite((method, parameters));
                    return new JsonObject { ["capabilities"] = new JsonObject { ["textDocumentSync"] = 1 } };
                }
            };
            foreach (string method in new[] { "textDocument/didOpen", "textDocument/didChange", "textDocument/didClose" })
                Rpc.OnNotification(method, parameters => messages.Writer.TryWrite((method, parameters)));
            NetworkStream editor = client.GetStream();
            Server = LanguageServer.Over("merge-test", "python", root, editor, editor, allowWorkspaceEdits: allowWorkspaceEdits);
        }

        public JsonRpcConnection Rpc { get; }
        public LanguageServer Server { get; }

        public MergeResultLanguageSession OpenSession(string path)
        {
            ownsServer = false;
            return new MergeResultLanguageSession(path, Server);
        }

        public async Task<JsonNode> Take(string expected)
        {
            using var timeout = new CancellationTokenSource(ProtocolTimeout);
            var (method, parameters) = await messages.Reader.ReadAsync(timeout.Token);
            Assert.Equal(expected, method);
            return Assert.IsAssignableFrom<JsonNode>(parameters);
        }

        public async Task Publish(string path, int version, string message)
        {
            Rpc.Notify("textDocument/publishDiagnostics", new JsonObject
            {
                ["uri"] = new Uri(path).AbsoluteUri,
                ["version"] = version,
                ["diagnostics"] = new JsonArray(new JsonObject { ["message"] = message, ["range"] = Range() })
            });
            // A reply on the same connection establishes that preceding notifications were read.
            await Rpc.Request("workspace/workspaceFolders", null).WaitAsync(ProtocolTimeout);
        }

        public void Dispose()
        {
            if (ownsServer)
                Server.Dispose();
            Rpc.Dispose();
            remote.Dispose();
            client.Dispose();
        }
    }
}
