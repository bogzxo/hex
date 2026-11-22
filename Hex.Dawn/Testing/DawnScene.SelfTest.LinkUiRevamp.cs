using System.Globalization;
using System.Numerics;

using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Skinning;

namespace Hex.Dawn;

// What the Devices window revamp promises, checked on the window with a device connected: its chips read at 4.5:1 in
// the look that's on, no button is only an ellipsis, the status item says who is linked, and the window doesn't change
// size when it's made again from the same state.
internal sealed partial class DawnScene
{
    private void LinkUiRevampChecks(HexPointerScript test)
    {
        test.Check("link-ui: every tone of chip in the window reads at 4.5:1 on the grounds it sits on", () =>
        {
            UIPalette palette = compositor.Skin!.Palette;
            Vector4 panel = Over(palette.Panel, new Vector4(0, 0, 0, 1));
            Vector4[] grounds = [panel, Over(palette.Field, panel), palette.Raised];
            var failed = new List<string>();
            foreach (UIBadgeTone tone in (UIBadgeTone[])[UIBadgeTone.Success, UIBadgeTone.Warning, UIBadgeTone.Danger, UIBadgeTone.Info])
            {
                var chip = new Badge { Tone = tone };
                chip.Colour = LinkBadgeColour(tone);
                Vector4 colour = chip.ColourIn(compositor.Skin);
                foreach (Vector4 ground in grounds)
                {
                    // The chip's own tint (16%) is what its words are on
                    float ratio = UIPalette.Contrast(colour with { W = 1.0f }, Over(colour with { W = 0.16f }, ground));
                    if (ratio < 4.5f)
                        failed.Add($"{tone} {ratio.ToString("0.00", CultureInfo.InvariantCulture)}");
                }
            }

            if (failed.Count > 0)
                Console.WriteLine($"[Hex self-test] link-ui: chips too low: {string.Join(", ", failed)}");
            return failed.Count == 0;
        });
        test.Check("link-ui: no button of the window is only an ellipsis, More says what it is", () =>
            linkWindow is not null && Descendants(linkWindow).OfType<Button>().All(button => button.Label.Trim() is not ("…" or "...") && button.Label.Length > 0)
            && Descendants(linkWindow).OfType<Button>().Any(button => button.Label == "More"));
        test.Check("link-ui: the status item says who is linked", () =>
            LinkPeers.Count == 1 && linkItem.Text.Contains(OneLine(LinkNameOf(LinkPeers[0].Device.Id, LinkPeers[0].Device.Name), 18), StringComparison.Ordinal));

        // An initial sync adds progress rows. Compare two rebuilds only once those transfers finish.
        float before = 0.0f;
        bool measuredIdle = false;
        test.WaitFor(SyncIdle, 15.0f);
        test.Run(() => linkWindowStale = true);
        test.Settle(0.4f);
        test.Run(() =>
        {
            measuredIdle = SyncIdle();
            before = linkWindowDevices?.Bounds.Height ?? 0.0f;
            linkWindowStale = true;
        });
        test.Settle(0.4f);
        test.Check("link-ui: the window made again from the same state keeps its size", () =>
        {
            float after = linkWindowDevices?.Bounds.Height ?? -1.0f;
            Console.WriteLine($"[Hex self-test] link-ui: devices part {before.ToString("0.0", CultureInfo.InvariantCulture)} then {after.ToString("0.0", CultureInfo.InvariantCulture)}");
            return measuredIdle && SyncIdle() && before > 0.0f && MathF.Abs(after - before) < 1.0f;
        });

        bool SyncIdle()
        {
            LinkSyncShown[] states = [.. LinkSyncs, .. LinkPeers.SelectMany(LinkRemoteSyncs)];
            return states.Any(sync => sync.State == HexLinkSyncState.Idle) &&
                states.All(sync => sync.State is not (HexLinkSyncState.Comparing or HexLinkSyncState.Syncing));
        }
    }
}
