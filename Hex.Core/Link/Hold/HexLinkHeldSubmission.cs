namespace Hex.Core;

/// <summary>Applies an explicitly submitted hold to a checked working file with an editor open.</summary>
internal interface IHexLinkHeldSubmission
{
    Task<string?> ApplyHeldSubmission(string full, string wire, string? basis, byte[]? bytes, CancellationToken cancel,
        bool? executable, HexLinkFiles.PackDirectories? directories);
}
