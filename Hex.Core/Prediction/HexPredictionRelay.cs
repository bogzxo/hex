using System.Runtime.CompilerServices;

namespace Hex.Core;

/// <summary>
/// The seam Hex Link serves predictions through and reaches them by. On the device without a model, Hex Link sets
/// <see cref="Remote"/> while attached to one that offers predictions and the link provider sends its prompts there. On
/// the device with the model, Hex Link calls <see cref="Serve"/> with what it was sent, which is refused unless the user
/// chose to share and this Hex's own provider is a server, never another device.
/// </summary>
public static class HexPredictionRelay
{
    // What the settings say and whether the user shares predictions, set by the glue that reads the settings
    private static Func<PredictionOptions?>? options;
    private static Func<bool>? shared;

    // The most tokens a paired device may ask for in one answer, the settings' own most
    private const int MOST_TOKENS = 2048;

    /// <summary>Set by Hex Link while attached to a device that offers predictions, null otherwise.</summary>
    public static Func<PredictionWireRequest, CancellationToken, IAsyncEnumerable<string>>? Remote { get; set; }

    /// <summary>The name of the device <see cref="Remote"/> reaches, for the status bar's "from ...".</summary>
    public static string? RemoteName { get; set; }

    /// <summary>
    /// Why no linked device serves this one while <see cref="Remote"/> is null, in words the user can act on (nothing
    /// connected, a device that hasn't given this one Autocomplete, one that doesn't share). Null when it isn't known.
    /// </summary>
    public static string? WhyNoRemote { get; set; }

    /// <summary>The provider served with in place of the one the settings make, for the tests.</summary>
    internal static IPredictionProvider? ProviderOverride { get; set; }

    /// <summary>Whether this Hex offers its predictions to paired devices: the setting, and a provider that is set up.</summary>
    public static bool Serving => shared?.Invoke() == true && Provider() is not null;

    /// <summary>Where the settings are read from, called once by the glue.</summary>
    internal static void Configure(Func<PredictionOptions?>? options, Func<bool>? shared)
    {
        HexPredictionRelay.options = options;
        HexPredictionRelay.shared = shared;
    }

    /// <summary>
    /// What Hex Link calls on the device that has the model: the request passed to this Hex's own provider and its
    /// text streamed back. Throws <see cref="PredictionException"/>, Refused when predictions aren't shared.
    /// </summary>
    public static IAsyncEnumerable<string> Serve(PredictionWireRequest request, CancellationToken cancel) =>
        Served(request, cancel);

    private static async IAsyncEnumerable<string> Served(PredictionWireRequest request, [EnumeratorCancellation] CancellationToken cancel)
    {
        IPredictionProvider? provider = shared?.Invoke() == true ? Provider() : null;
        if (provider is null)
            throw new PredictionException(PredictionStatus.Refused, "This Hex doesn't share its predictions.");

        if (request.MaxTokens > MOST_TOKENS)
            request = request with { MaxTokens = MOST_TOKENS };

        await foreach (string piece in provider.Complete(request, cancel).WithCancellation(cancel).ConfigureAwait(false))
            yield return piece;
    }

    // This Hex's own provider, when it is a server: a device asked through Hex Link is never passed on to another
    private static IPredictionProvider? Provider()
    {
        if (ProviderOverride is { } given)
            return given;
        if (options?.Invoke() is not { } settings || settings.Provider == PredictionProviders.LINK)
            return null;
        return PredictionProviders.Make(settings);
    }
}
