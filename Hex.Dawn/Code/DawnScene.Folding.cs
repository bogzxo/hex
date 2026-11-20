using Horizon.UI.Scripting;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private (HexDocument? Of, int Version, LanguageServer? Source, string? Path) foldingFor;
    private HexSettle<(HexDocument, int)> foldingSettle = new(FOLDING_SETTLE_MS);
    private const long FOLDING_SETTLE_MS = 300;
    private long foldingAskedAt, foldingRetryAt;
    private bool foldingPending;
    private int foldingRequest, foldingTries;

    private void UpdateNativeFolding()
    {
        if (!document.IsText || document.DiffOf is not null || document.Picture is not null)
        {
            ForgetNativeFolding();
            return;
        }
        int version = code.Document.Version;
        LanguageServer? source = languages.FoldingSourceOf(document);
        var key = (document, version, source, document.Path);
        long now = Environment.TickCount64;
        if (foldingFor == key)
        {
            if (!(foldingRetryAt > 0 && now >= foldingRetryAt) && !(foldingPending && now - foldingAskedAt > 10_000)) return;
        }
        else
        {
            if (foldingFor.Of != document || foldingFor.Source != source || foldingFor.Path != document.Path) foldingSettle.Reset();
            if (source is null)
            {
                foldingFor = key;
                foldingRequest++;
                foldingPending = false;
                foldingRetryAt = 0;
                if (code.NativeFoldingRanges is not null) code.SetNativeFoldingRanges(null);
                return;
            }
            if (foldingFor.Of == document && foldingFor.Source == source && foldingFor.Path == document.Path && foldingFor.Version != version)
            {
                if (!foldingSettle.Settled((document, version), now)) return;
            }
            foldingTries = 0;
        }
        if (source is null) return;
        foldingFor = key;
        foldingPending = true;
        foldingAskedAt = now;
        foldingRetryAt = 0;
        AskNativeFolding(document, version, source, document.Path, ++foldingRequest);
    }

    private void AskNativeFolding(HexDocument of, int version, LanguageServer source, string? path, int request)
    {
        var weak = new WeakReference<HexDocument>(of);
        languages.AskFolding(of, found =>
        {
            if (request != foldingRequest) return;
            foldingPending = false;
            if (!weak.TryGetTarget(out var open) || !documents.Contains(open) || open.Editor.Document.Version != version || open.Path != path
                || languages.FoldingSourceOf(open) != source) return;
            open.Editor.SetNativeFoldingRanges(found, supplement: !source.Offers("foldingRangeProvider"));
            if ((found is null || found.Count == 0) && ++foldingTries <= 6) foldingRetryAt = Environment.TickCount64 + 1500;
        });
    }

    private void ForgetNativeFolding()
    {
        foldingSettle.Reset();
        if (foldingFor.Of is null) return;
        foldingFor = default;
        foldingRequest++;
        foldingPending = false;
        foldingRetryAt = 0;
    }
}
