namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private const string HeldDraftSample = "private editor draft";
    private const string HeldPeerSample = "peer kept working";
    private HexLinkHoldService? linkDocPeerHoldService;
    private HexLinkHoldStore? linkDocPeerStore;
    private Task<HexLinkHold>? linkDocPeerHolding;
    private Task<HexLinkHoldOperation>? linkDocPeerDiscarding;

    private void TestLinkDocumentHold(HexPointerScript test, string folder, string peerWork, string peerFolder)
    {
        string file = Path.Combine(folder, "note.txt"), peerFile = Path.Combine(peerFolder, "note.txt");
        HexLinkHoldStore? store = null;
        HexLinkHoldService? service = null;
        Task<HexLinkHold>? starting = null;
        Task<HexLinkHoldOperation>? submitting = null;
        Task? completing = null;

        void Check(string message, Func<bool> ready)
        {
            test.WaitFor(ready, 20);
            test.Check("link-documents: " + message, ready);
        }

        test.Run(() =>
        {
            var shared = new HexLinkFolder("shared", folder);
            store = linkServices!.Holds(shared);
            service = new(shared, store);
            starting = LinkHoldDocument(service, LINK_DOC_WIRE);
        });
        Check("holding an open shared tab leaves its live session", () =>
            starting is { IsCompletedSuccessfully: true } && LinkLiveOf(document) is null);
        test.Run(() =>
        {
            Rewrite(document, HeldDraftSample);
            SaveTo(file);
            File.WriteAllText(Path.Combine(peerWork, "held-peer-edit"), "");
        });
        Check("the peer saw the last shared text and could edit and save while the authority held its file", () =>
            ReadLinkTestFile(file) == HeldDraftSample && document.Editor.Text == HeldDraftSample &&
            ReadLinkTestFile(peerFile) == HeldPeerSample && store!.Read(LINK_DOC_WIRE)?.IncomingBytes is { } bytes &&
            System.Text.Encoding.UTF8.GetString(bytes) == HeldPeerSample);
        test.Run(() => submitting = service!.Submit());
        Check("submitting both edits keeps the file held until resolved", () =>
            submitting is { IsCompletedSuccessfully: true } && submitting.Result.NeedsMerge.Count == 1 && store!.IsHeld(LINK_DOC_WIRE));
        test.Run(() =>
        {
            if (submitting?.IsCompletedSuccessfully == true && submitting.Result.NeedsMerge.SingleOrDefault()?.Session is { } merge)
                completing = merge.Complete(System.Text.Encoding.UTF8.GetBytes(HeldDraftSample));
        });
        Check("the resolved submit reaches the peer and both open editors can rejoin", () =>
            completing is { IsCompletedSuccessfully: true } && !store!.IsHeld(LINK_DOC_WIRE) &&
            LinkLiveOf(document) is { Draft: null } && ReadLinkTestFile(peerFile) == HeldDraftSample &&
            File.Exists(Path.Combine(peerWork, "held-peer-rejoined")));
        test.Run(() => File.WriteAllText(Path.Combine(peerWork, "hold-as-participant"), ""));
        Check("a participant can hold and save private text without editing the authority's buffer or file", () =>
            File.Exists(Path.Combine(peerWork, "participant-saved")) && document.Editor.Text == HeldDraftSample &&
            ReadLinkTestFile(file) == HeldDraftSample && ReadLinkTestFile(peerFile) == HeldPeerSample);
        test.Run(() =>
        {
            Rewrite(document, "authority edit");
            SaveTo(file);
        });
        Check("the participant can discard its held edit, receive the authority save and join again", () =>
            File.Exists(Path.Combine(peerWork, "participant-rejoined")) && ReadLinkTestFile(peerFile) == "authority edit" &&
            ReadLinkTestFile(file) == "authority edit");

        Task<string?>? receiverWrite = null;
        LinkLiveDocument? receivingRoom = null;
        byte[] submitted = "submitted by a holder"u8.ToArray();
        string expected = HexLinkFiles.Hash("authority edit"u8);
        test.Run(() =>
        {
            receivingRoom = LinkLiveOf(document);
            if (receivingRoom is null || linkServices is null)
                return;
            receivingRoom.HoldEditsForTest = true;
            Rewrite(document, "unsaved authority text");
            receiverWrite = linkServices.ApplyHeldSubmission(file, LINK_DOC_WIRE, expected, submitted, default, null, null);
        });
        Check("a checked held submission protects an unsaved authority tab", () =>
            receiverWrite?.Exception?.GetBaseException() is HexLinkException { Code: HexLinkErrors.CONFLICT } &&
            LinkLiveOf(document) == receivingRoom && document.Editor.Text == "unsaved authority text" &&
            ReadLinkTestFile(file) == "authority edit");
        test.Run(() =>
        {
            Rewrite(document, "authority edit");
            if (receivingRoom is not null)
                receivingRoom.HoldEditsForTest = false;
            receiverWrite = linkServices?.CompareAndReplace(file, LINK_DOC_WIRE, expected, submitted, default);
        });
        Check("ordinary sync still defers to the clean active room", () =>
            receiverWrite?.Exception?.GetBaseException() is HexLinkException { Code: HexLinkErrors.CONFLICT } &&
            LinkLiveOf(document) == receivingRoom && ReadLinkTestFile(file) == "authority edit");
        test.Run(() => receiverWrite = linkServices?.ApplyHeldSubmission(file, LINK_DOC_WIRE, expected, submitted, default, null, null));
        bool SharedSubmissionArrived() => receiverWrite is { IsCompletedSuccessfully: true } &&
            ReadLinkTestFile(file) == "submitted by a holder" && ReadLinkTestFile(peerFile) == "submitted by a holder" &&
            document.Editor.Text == "submitted by a holder" && LinkLiveOf(document) is { Draft: null } joined &&
            !ReferenceEquals(joined, receivingRoom);
        Check("a checked submission updates the clean authority and rejoins both editors", SharedSubmissionArrived);
        test.Run(() =>
        {
            if (!SharedSubmissionArrived())
                Console.WriteLine($"[Hex self-test] held publication: {receiverWrite?.Status}, {receiverWrite?.Exception?.GetBaseException().Message}, local={ReadLinkTestFile(file)}, peer={ReadLinkTestFile(peerFile)}, room={LinkLiveOf(document) is not null}, sync={LinkSyncs.FirstOrDefault()?.Summary}");
        });
    }

    private void LinkDocumentHoldTestPeer(string work)
    {
        if (linkDocTestStage == 7 && File.Exists(Path.Combine(work, "held-peer-edit")) &&
            LinkLiveOf(document) is null && document.Editor.Text == "named")
        {
            Rewrite(document, HeldPeerSample);
            SaveTo(document.Path!);
            linkDocTestStage++;
        }
        if (linkDocTestStage == 8 && LinkLiveOf(document) is { Draft: null } && document.Editor.Text == HeldDraftSample)
        {
            File.WriteAllText(Path.Combine(work, "held-peer-rejoined"), "");
            linkDocTestStage++;
        }
        if (linkDocTestStage == 9 && File.Exists(Path.Combine(work, "hold-as-participant")) && LiveFolder(LINK_DOC_WIRE) is { } folder)
        {
            linkDocPeerStore = linkServices!.Holds(folder);
            linkDocPeerHoldService = new(folder, linkDocPeerStore);
            linkDocPeerHolding = LinkHoldDocument(linkDocPeerHoldService, LINK_DOC_WIRE);
            linkDocTestStage++;
        }
        if (linkDocTestStage == 10 && linkDocPeerHolding is { IsCompletedSuccessfully: true } && LinkLiveOf(document) is null)
        {
            Rewrite(document, HeldPeerSample);
            SaveTo(document.Path!);
            File.WriteAllText(Path.Combine(work, "participant-saved"), "");
            linkDocTestStage++;
        }
        if (linkDocTestStage == 11 && document.Editor.Text == HeldPeerSample &&
            linkDocPeerStore!.Read(LINK_DOC_WIRE)?.IncomingBytes is { } incoming && System.Text.Encoding.UTF8.GetString(incoming) == "authority edit")
        {
            linkDocPeerDiscarding = linkDocPeerHoldService!.Discard();
            linkDocTestStage++;
        }
        if (linkDocTestStage == 12 && linkDocPeerDiscarding is { IsCompletedSuccessfully: true } &&
            LinkLiveOf(document) is { Draft: null } && document.Editor.Text == "authority edit")
        {
            File.WriteAllText(Path.Combine(work, "participant-rejoined"), "");
            linkDocTestStage++;
        }
    }
}
