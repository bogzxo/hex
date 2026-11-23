using System.Text.Json.Nodes;

using Hex.Core;

using static Hex.Tests.HexLinkTestKit;

namespace Hex.Tests;

public sealed class HexLinkSpawnPolicyTests
{
    [Theory]
    [InlineData("claude_code", "permission_mode", "bypassPermissions")]
    [InlineData("codex", "sandbox", "danger-full-access")]
    [InlineData("antigravity", "mode", "anything")]
    public void Only_the_per_device_everything_setting_allows_unrestricted_modes(string provider, string key, string mode)
    {
        string home = Path.Combine(Path.GetTempPath(), "hexlink-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var trust = new HexLinkTrust(home);
            trust.Add(DeviceA, HexLinkRights.Steer, "localhost", 1000);
            trust.Add(DeviceB, HexLinkRights.Steer, "localhost", 1001);
            Assert.Equal(HexLinkPreApproval.Edits, trust.Find(DeviceA.Id)!.RemotePreApproval);
            Assert.True(trust.SetRemotePreApproval(DeviceA.Id, HexLinkPreApproval.Everything));

            var reopened = new HexLinkTrust(home);
            var args = new JsonObject { ["options"] = new JsonObject { [key] = mode } };
            Assert.Null(HexLinkSpawnPolicy.Apply(args, provider, reopened.Find(DeviceA.Id)!.RemotePreApproval));
            Assert.NotNull(HexLinkSpawnPolicy.Apply(args, provider, reopened.Find(DeviceB.Id)!.RemotePreApproval));
        }
        finally
        {
            if (Directory.Exists(home))
                Directory.Delete(home, recursive: true);
        }
    }
}
