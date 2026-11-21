using System.Diagnostics;
using System.Globalization;

using Hex.Shell;

using Horizon.Core.Diagnostics;
using Horizon.UI.Skinning;

namespace Hex.Dawn;

// Going between the faces timed, every way there is (Dawn and Agentic in the one scene, Dusk the other) in every look at
// 100, 150 and 200%. Every frame drawn from the press on is kept (FrameTrace): how long till the first frame of the face
// that was picked and till it stood still, the longest gap between two frames, what was made meanwhile, and whether any
// frame went out with no UI on it or with one face painted in the other's theme. Only when named, Dusk shares the window
// only then.
internal sealed partial class DawnScene
{
    // The most a switch may take to its first frame and to standing still, in a Debug build on a busy machine, ms
    private const double FACE_FIRST_BUDGET = 120.0, FACE_SETTLED_BUDGET = 450.0;

    // How long a switch is watched for
    private const float FACE_WATCH = 0.6f;

    /// <summary>One switch as it went.</summary>
    private sealed record FaceSwitchTimed(string Name, double FirstMs, double SettledMs, double WorstGapMs, int Hitches, int Blank, int Mixed, double PressMs, long PressKb, double AllocatedMb, int Collections, int Frames, int Fading);

    private readonly List<FaceSwitchTimed> faceSwitches = [];

    private void TestFaceSwitch(HexPointerScript test)
    {
        string themeBefore = settings.EditorTheme, skinBefore = settings.EditorSkin;
        float scaleBefore = 1.0f, motionBefore = settings.UiAnimation;
        HexAgentChat? chat = null;

        test.Check("face-switch: Dusk shares the self test's window", () => HexModes.Together);
        test.Run(() =>
        {
            scaleBefore = compositor.Scale;
            chat = new HexAgentChat(++agentChatsMade, "noon_agent") { Model = "claude-opus-5-5" };
            chat.Ask("time the faces");
            chat.Say("The fleet, the chat and Details, to have something in Agentic.");
            chat.Done();
            agentChats.Add(chat);
            OpenAgentChat(chat);
            ShowFace(HexFace.Dawn);
            FrameTrace.On = true;
            facesMoveInTest = true;
            settings.UiAnimation = 1.0f;
        });

        // The first Dusk of a run is made as the pointer rests on its button before the press, the way a hand gets to a
        // button. Timed on its own
        FaceSwitchStep(test, "cold", "Dawn to Dusk (first)", HexFace.Dusk, hover: 0.6f);
        FaceSwitchStep(test, "cold", "Dusk to Dawn", HexFace.Dawn);
        // And the first Agentic, its Noon theme loaded as the pointer comes to its button
        FaceSwitchStep(test, "cold", "Dawn to Agentic (first)", HexFace.Noon, hover: 0.6f);
        FaceSwitchStep(test, "cold", "Agentic to Dawn", HexFace.Dawn, hover: 0.25f);

        // HEX_FACE_LOOKS narrows it to the looks whose names have one of its words, for a quicker look at one
        string[] only = Environment.GetEnvironmentVariable("HEX_FACE_LOOKS")?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [];
        foreach (HexLook look in HexLooks.BuiltIn.Where(look => only.Length == 0 || only.Any(word => look.Name.Contains(word, StringComparison.OrdinalIgnoreCase))))
        {
            foreach (float scale in (float[])[1.0f, 1.5f, 2.0f])
            {
                string named = $"{look.Name} {scale * 100:0}%";
                test.Run(() =>
                {
                    ShowFace(HexFace.Dawn);
                    PickLook(look);
                    SetInterfaceSize(scale, remember: false);
                    HexModes.InterfaceSize = scale;
                });
                test.Settle(0.4f);
                FaceSwitchRound(test, named);
            }
        }

        // With the interface animation at 0 nothing moves, the old face's frame is let go of as the new one's there
        test.Run(() =>
        {
            ShowFace(HexFace.Dawn);
            PickLook(HexLooks.Solstice);
            SetInterfaceSize(1.0f, remember: false);
            HexModes.InterfaceSize = 1.0f;
            settings.UiAnimation = 0.0f;
        });
        test.Settle(0.4f);
        FaceSwitchRound(test, "motion 0");
        test.Check("face-switch: with the interface animation at 0 the faces don't move, no frame half faded and nothing sliding", () =>
            FaceTransition() is { InTime: 0.0f } && HexModes.ArriveTime == 0.0f && body.VisualOffset == System.Numerics.Vector2.Zero
            && faceSwitches.Where(each => each.Name.StartsWith("motion 0", StringComparison.Ordinal)).All(each => each.Fading == 0));
        test.Check("face-switch: with the interface animation on the old face fades off the new one", () =>
            faceSwitches.Where(each => !each.Name.StartsWith("motion 0", StringComparison.Ordinal) && !each.Name.StartsWith("cold", StringComparison.Ordinal)).All(each => each.Fading > 0));

        test.Run(ReportFaceSwitches);
        test.Check("face-switch: no frame went out empty, the old face's or the new one's", () => faceSwitches.All(each => each.Blank == 0));
        test.Check("face-switch: no frame of one face painted in the other's theme", () => faceSwitches.All(each => each.Mixed == 0));
        test.Check($"face-switch: every warm switch is on screen within {FACE_FIRST_BUDGET:0} ms and still within {FACE_SETTLED_BUDGET:0} ms", () =>
            faceSwitches.Where(each => !each.Name.StartsWith("cold", StringComparison.Ordinal)).All(each => each.FirstMs <= FACE_FIRST_BUDGET && each.SettledMs <= FACE_SETTLED_BUDGET));

        test.Run(() =>
        {
            FrameTrace.On = false;
            FrameTrace.Take();
            settings.UiAnimation = motionBefore;
            facesMoveInTest = false;
            SetInterfaceSize(scaleBefore, remember: false);
            HexModes.InterfaceSize = scaleBefore;
            PickLook(HexLooks.Solstice);
            UseEditorSkin(skinBefore, themeBefore, save: false);
            if (chat is not null)
                agentChats.Remove(chat);
            agentChat = null;
        });
        test.Settle(0.3f);
    }

    /// <summary>Every way between the faces once, starting and ending in Dawn.</summary>
    private void FaceSwitchRound(HexPointerScript test, string named)
    {
        FaceSwitchStep(test, named, "Dawn to Agentic", HexFace.Noon, hover: 0.25f);
        FaceSwitchStep(test, named, "Agentic to Dawn", HexFace.Dawn, hover: 0.25f);
        FaceSwitchStep(test, named, "Dawn to Dusk", HexFace.Dusk, hover: 0.25f);
        FaceSwitchStep(test, named, "Dusk to Dawn", HexFace.Dawn);
        FaceSwitchStep(test, named, "Dawn to Agentic", HexFace.Noon, hover: 0.25f);
        FaceSwitchStep(test, named, "Agentic to Dusk", HexFace.Dusk, hover: 0.25f);
        FaceSwitchStep(test, named, "Dusk to Agentic", HexFace.Noon);
        FaceSwitchStep(test, named, "Agentic to Dawn", HexFace.Dawn, hover: 0.25f);
    }

    // The button of a face on the editor's title bar
    private Button? FaceButton(HexFace face) =>
        modeToggle.Parent?.Children.OfType<Button>().FirstOrDefault(button => button.Label == face.Name());

    /// <summary>
    /// One switch to a face pressed and watched, from wherever the test is, the way its button would. In the editor the
    /// pointer rests on the button for a moment first, as a hand does on its way to a click.
    /// </summary>
    private void FaceSwitchStep(HexPointerScript test, string where, string what, HexFace to, float hover = 0.0f)
    {
        if (hover > 0.0f)
        {
            test.Hover(() => HexModes.Current == HexMode.Dawn && FaceButton(to) is { Module: { } module } button ? module.ToWorld(button.Bounds.Center) : test.Pointer.Position);
            test.Settle(hover);
        }

        long pressed = 0, allocated = 0, pressBytes = 0;
        int collections = 0;
        double pressMs = 0;
        string? noonTheme = null;
        test.Run(() =>
        {
            FrameTrace.Take();
            noonTheme = NoonThemeOf(UISkins.Find(settings.EditorSkin), settings.EditorTheme);
            allocated = GC.GetTotalAllocatedBytes(false);
            collections = GC.CollectionCount(0) + GC.CollectionCount(1) + GC.CollectionCount(2);
            long ownBytes = GC.GetAllocatedBytesForCurrentThread();
            pressed = Stopwatch.GetTimestamp();
            PressFace(to);
            pressMs = Stopwatch.GetElapsedTime(pressed).TotalMilliseconds;
            pressBytes = GC.GetAllocatedBytesForCurrentThread() - ownBytes;
            // Frames all the way through, to see each gap between them
            Engine.WindowManager.RequestFrames(FACE_WATCH);
        });
        test.Settle(FACE_WATCH);
        test.Run(() =>
        {
            FrameTrace.Frame[] frames = FrameTrace.Take();
            faceSwitches.Add(TimeFaceSwitch($"{where}, {what}", frames, pressed, to, noonTheme, pressMs, pressBytes,
                GC.GetTotalAllocatedBytes(false) - allocated, GC.CollectionCount(0) + GC.CollectionCount(1) + GC.CollectionCount(2) - collections));
        });
    }

    /// <summary>A face asked for the way the face switch on the title bar of whichever mode is on screen asks for it.</summary>
    private void PressFace(HexFace to)
    {
        if (HexModes.Current == HexMode.Dawn)
        {
            PickFace(to);
            return;
        }

        // Dusk's switch: the face the editor's to show, and the swap
        if (to != HexFace.Dusk)
        {
            HexModes.FaceWanted = to;
            HexModes.Swap();
        }
    }

    /// <summary>What the frames of one switch say.</summary>
    private static FaceSwitchTimed TimeFaceSwitch(string name, FrameTrace.Frame[] frames, long pressed, HexFace to, string? noonTheme, double pressMs, long pressBytes, long allocated, int collections)
    {
        static bool Faced(FrameTrace.Frame frame, HexFace face) => frame.Tag is HexFace painted && painted == face;
        // The new face drawn whole, with the old one's frame maybe still over it as it fades
        bool Arrived(FrameTrace.Frame frame) => frame.Scene is not null && !frame.Empty && Faced(frame, to);
        // And seen, the old frame not all over it any more
        bool Shown(FrameTrace.Frame frame) => Arrived(frame) && frame.Cover < 1.0f;
        double Ms(long at) => Stopwatch.GetElapsedTime(pressed, at).TotalMilliseconds;

        var after = frames.Where(frame => frame.At >= pressed).ToArray();
        if (Environment.GetEnvironmentVariable("HEX_FACE_FRAMES") is { Length: > 0 })
            foreach (FrameTrace.Frame frame in after.Take(40))
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[face-frames] {name}	{Ms(frame.At):0.0}	{frame.Scene ?? "-"}	{frame.Tag ?? "-"}	{frame.Skin ?? "-"}	{(frame.Empty ? "empty" : "")}	{frame.Painted}	{frame.Cover:0.00}"));
        int first = Array.FindIndex(after, Shown);
        // Standing still from the first frame on which every frame after it is the face arrived, uncovered, in the theme
        // it ends in
        int settled = -1;
        bool Still(FrameTrace.Frame frame) => Arrived(frame) && frame.Cover <= 0.0f && frame.Skin == after[^1].Skin;
        if (after.Length > 0 && Still(after[^1]))
        {
            settled = after.Length - 1;
            while (settled > 0 && Still(after[settled - 1]))
                settled--;
        }

        // Nothing of the UI on a frame, a scene not drawn or a traced UI that drew nothing, with no old frame over it
        int blank = after.Count(frame => (frame.Scene is null || frame.Tag is null || frame.Empty) && frame.Cover < 1.0f);
        int fading = after.Count(frame => frame.Cover is > 0.0f and < 1.0f);
        // A face painted in the other's theme, Dawn in Noon's or Noon in Dawn's, where the look has a Noon theme
        int mixed = noonTheme is null ? 0 : after.Count(frame =>
            Faced(frame, HexFace.Noon) && frame.Skin != noonTheme || Faced(frame, HexFace.Dawn) && frame.Skin == noonTheme);

        double worst = 0;
        int hitches = 0;
        long last = pressed;
        foreach (FrameTrace.Frame frame in after)
        {
            double gap = Stopwatch.GetElapsedTime(last, frame.At).TotalMilliseconds;
            worst = Math.Max(worst, gap);
            if (gap > 34.0)
                hitches++;
            last = frame.At;
        }

        return new FaceSwitchTimed(name, first >= 0 ? Ms(after[first].At) : double.PositiveInfinity, settled >= 0 ? Ms(after[settled].At) : double.PositiveInfinity,
            worst, hitches, blank, mixed, pressMs, pressBytes / 1024, allocated / (1024.0 * 1024.0), collections, after.Length, fading);
    }

    /// <summary>Every switch timed, a line each and the worst of each kind, on the run's output and in the file HEX_FACE_TIMINGS names.</summary>
    private void ReportFaceSwitches()
    {
        var lines = new List<string> { "switch\tfirst ms\tsettled ms\tworst gap ms\thitches\tblank\tmixed\tpress ms\tpress KB\tallocated MB\tGCs\tframes" };
        foreach (FaceSwitchTimed each in faceSwitches)
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"{each.Name}\t{each.FirstMs:0.0}\t{each.SettledMs:0.0}\t{each.WorstGapMs:0.0}\t{each.Hitches}\t{each.Blank}\t{each.Mixed}\t{each.PressMs:0.00}\t{each.PressKb}\t{each.AllocatedMb:0.00}\t{each.Collections}\t{each.Frames}"));

        lines.Add(string.Empty);
        lines.Add("kind\tswitches\tmedian first\tworst first\tmedian settled\tworst settled\tworst gap\tblank\tmixed\tmedian press ms\tmedian allocated MB");
        foreach (var kind in faceSwitches.GroupBy(each => each.Name[(each.Name.LastIndexOf(", ", StringComparison.Ordinal) + 2)..] + (each.Name.StartsWith("cold", StringComparison.Ordinal) ? " (cold)" : string.Empty)))
        {
            double Median(Func<FaceSwitchTimed, double> of)
            {
                double[] sorted = [.. kind.Select(of).Order()];
                return sorted[sorted.Length / 2];
            }

            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"{kind.Key}\t{kind.Count()}\t{Median(each => each.FirstMs):0.0}\t{kind.Max(each => each.FirstMs):0.0}\t{Median(each => each.SettledMs):0.0}\t{kind.Max(each => each.SettledMs):0.0}\t{kind.Max(each => each.WorstGapMs):0.0}\t{kind.Sum(each => each.Blank)}\t{kind.Sum(each => each.Mixed)}\t{Median(each => each.PressMs):0.00}\t{Median(each => each.AllocatedMb):0.00}"));
        }

        foreach (string line in lines)
            Console.WriteLine($"[face-switch] {line}");
        if (Environment.GetEnvironmentVariable("HEX_FACE_TIMINGS") is { Length: > 0 } file)
            File.WriteAllLines(file, lines);
    }
}
