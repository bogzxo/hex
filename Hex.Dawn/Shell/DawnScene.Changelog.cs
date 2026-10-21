using Hex.Core;

using Horizon.Core.Threading;
using Horizon.UI.Components;
using Horizon.UI.Drawing;

namespace Hex.Dawn;

// Help, Release Notes. What changed comes from Hex's releases on GitHub, the notes each one was released with, and
// the history from before there were releases is out of Hex's texts under them. The window comes up straight away
// saying it's asking and the page goes in when GitHub answers. Without GitHub (no network, the hour's questions used
// up) it's the history out of the texts on its own, and a line saying why. The self test never asks, it's offline.
internal sealed partial class DawnScene
{
    // Where Hex is released
    private const string HEX_REPO = "bogzxo/hex", CHANGELOG_WORK = "hex-changelog";

    /// <summary>Help, Release Notes.</summary>
    private void ShowChangelog()
    {
        ReadStrings();
        string header = Said("changelog.header", ("version", HexAbout.Version));
        string history = Said("changelog.page");

        if (options.SelfTest)
        {
            ShowText(Said("changelog.title"), header + "\n\n" + history);
            return;
        }

        WindowPanel window = ShowText(Said("changelog.title"), header + "\n\n" + Said("changelog.asking", ("repo", HEX_REPO)), out Label page);
        List<HexRelease> releases = [];
        StartWork(CHANGELOG_WORK, Said("changelog.title"), async context =>
        {
            releases = await HexReleases.Recent(HEX_REPO, 30, context.Token).ConfigureAwait(false);
        }, item =>
        {
            // Closed before GitHub answered, there's nowhere to put it
            if (window.Parent is null)
                return;

            if (item.State == WorkState.Succeeded && releases.Count > 0)
            {
                // The notes are markdown as they were written for the release, so they go in as they are
                string released = HexReleases.Changelog(releases, null, text => text);
                page.Text = header + "\n\n" + Said("changelog.from_github", ("repo", HEX_REPO)) + "\n\n" + released + "\n\n" + Said("changelog.older") + "\n\n" + history;
            }
            else
            {
                string why = item.State == WorkState.Succeeded ? Said("changelog.no_releases") : item.Error ?? string.Empty;
                page.Text = header + "\n\n" + Said("changelog.offline", ("why", UIRichText.Literal(why))) + "\n\n" + history;
            }
        }, seen: false, cancellable: true);
    }
}
