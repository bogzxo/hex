using Hex.Core.Projects;

namespace Hex.Dawn;

// A folder with a Visual Studio solution at the top of it offers Visual Studio's keys with a card, as long as nobody's
// ever chosen either way. Somebody from Visual Studio found out about Hex's keys by pressing Ctrl+- to go back and
// having the code zoom out, the setting was there all along and nothing said so.
internal sealed partial class DawnScene
{
    // How long after a folder opens the card goes up, in seconds. Put up as Hex was still starting it never got seen
    private const float VISUAL_STUDIO_OFFER_AFTER = 1.5f;

    // Whether the card went up this run, it's never up twice, and the seconds left till it does, 0 for not waiting
    private bool visualStudioKeysOffered;
    private float visualStudioOfferIn;

    // The card while it's up, taken down once the setting's changed some other way. It stayed up asking after Visual
    // Studio's keys had been turned on in the settings
    private UINotification? visualStudioOffer;

    // The folders changed, a folder with a solution has the card waiting to go up
    private void OfferVisualStudioKeys()
    {
        if (!visualStudioKeysOffered && WantsVisualStudioOffer() && DotnetSolutionTree.SolutionsIn(explorer.workspace.Folders).Count > 0)
            visualStudioOfferIn = VISUAL_STUDIO_OFFER_AFTER;
    }

    // Whether the keys are anybody's to offer, nobody having chosen and nothing checking itself
    private bool WantsVisualStudioOffer() => !settings.VisualStudioKeys && !settings.VisualStudioKeysChosen && !options.SelfTest && options.Check.Length == 0;

    /// <summary>Every tick, the card up once its moment comes, if it's still wanted then.</summary>
    private void UpdateVisualStudioOffer(float dt)
    {
        if (visualStudioOffer is { } card && (card.Closed || !WantsVisualStudioOffer()))
        {
            card.Close();
            visualStudioOffer = null;
        }

        if (visualStudioOfferIn <= 0.0f || (visualStudioOfferIn -= dt) > 0.0f)
            return;

        visualStudioOfferIn = 0.0f;
        if (visualStudioKeysOffered || !WantsVisualStudioOffer() || explorer.workspace.IsEmpty)
            return;

        visualStudioKeysOffered = true;
        visualStudioOffer = toasts.Show(Said("keys.visual_studio_offer"), "keyboard", null, 0.0f,
            (Said("keys.use_visual_studio"), () => ChooseVisualStudioKeys(true)),
            (Said("keys.keep_these"), () => ChooseVisualStudioKeys(false)));
    }

    // An answer to the card, kept in the settings so it isn't asked again
    private void ChooseVisualStudioKeys(bool use)
    {
        settings.VisualStudioKeys = use;
        settings.VisualStudioKeysChosen = true;
        ApplyKeyLayer();
        SaveSettings();
        if (use)
            Say(Said("keys.visual_studio_on"));
    }
}
