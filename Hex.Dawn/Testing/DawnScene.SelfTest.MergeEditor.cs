using System.Numerics;
using System.Text;
using Hex.Core.Merge;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestMergeEditor(HexPointerScript test)
    {
        const string draft = "start\n<<<<<<< Mine\nfirst mine\n||||||| Base\nfirst base\n=======\nfirst theirs\n>>>>>>> Theirs\n"
            + "between\n<<<<<<< Mine\nsecond mine\n||||||| Base\nsecond base\n=======\nsecond theirs\n>>>>>>> Theirs\nend\n";
        const string expected = "start\nfirst mine\nbetween\nhand resolved\nend\n";
        string theme = Environment.GetEnvironmentVariable("HEX_SELFTEST_MERGE_THEME") ?? "dark";
        var finished = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        MergeEditor? merge = null;
        bool narrow = false, cancelled = false;
        int binaryWrites = 0;
        test.Run(() =>
        {
            ShowFace(HexFace.Dawn);
            UseEditorTheme(theme, save: false);
            right.Show(false, instantly: true);
            merge = OpenMergeEditor(Session(draft, bytes => { finished.TrySetResult(bytes); return Task.CompletedTask; }));
        });
        test.Wait();
        test.Check("merge editor: two conflicts and Complete merge disabled", () =>
            merge is { UnresolvedCount: 2, ConflictCount: 2, CanComplete: false } && document.Name == "Merge: Fixture.cs");

        test.Check("merge editor: theme text has at least 4.5:1 contrast on the editor and its conflict tint", () =>
        {
            if (compositor.Skin is not { } skin)
                return false;
            Vector4 text = skin.TextColor, panel = code.Color ?? skin.PanelColor;
            Vector4 highlighted = Vector4.Lerp(panel, WarningColor, 0.15f);
            double baseRatio = Contrast(text, panel), conflictRatio = Contrast(text, highlighted);
            Console.WriteLine($"[Hex self-test] merge editor {settings.EditorTheme}: text {baseRatio:F2}:1, conflict {conflictRatio:F2}:1");
            return baseRatio >= 4.5 && conflictRatio >= 4.5;
        });
        test.Run(() =>
        {
            merge!.Fit(new Vector2(720, 480));
            narrow = merge.IsNarrow && code.LensesAbove.Any(lens => lens.Text.StartsWith("Mine  ", StringComparison.Ordinal))
                && code.LensesAbove.Any(lens => lens.Text.StartsWith("Theirs  ", StringComparison.Ordinal));
        });
        test.Check("merge editor: narrow view shows both inline versions", () => narrow);
        if (Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS") is { Length: > 0 } shots)
        {
            test.Run(() => Engine.CaptureScreenshot(Path.Combine(shots, $"merge-editor-{theme}.png")));
            test.WaitFor(() => new FileInfo(Path.Combine(shots, $"merge-editor-{theme}.png")) is { Exists: true, Length: > 1024 }, 10);
        }

        test.Run(() => { code.GoTo(1); merge!.AcceptCurrent(MergeChoice.Mine); });
        test.Check("merge editor: Accept mine resolves only the first block", () =>
            merge is { UnresolvedCount: 1, CanComplete: false } && code.Text.Contains("first mine\nbetween", StringComparison.Ordinal));

        test.Run(() => { code.GoTo(1); merge!.AcceptCurrent(MergeChoice.Unresolved); });
        test.Check("merge editor: Reset restores the first conflict", () => merge is { UnresolvedCount: 2, CanComplete: false });

        test.Run(() => { code.GoTo(1); merge!.AcceptCurrent(MergeChoice.BothTheirsFirst); });
        test.Check("merge editor: Accept both respects the order", () => code.Text.Contains("first theirs\nfirst mine\n", StringComparison.Ordinal));
        test.Run(() => code.Document.Undo());
        test.Check("merge editor: undo restores the conflict resolution state", () => merge is { UnresolvedCount: 2, CanComplete: false });

        test.Run(() =>
        {
            code.GoTo(1);
            merge!.AcceptCurrent(MergeChoice.Mine);
            int row = Enumerable.Range(0, code.Document.LineCount).First(line => code.Document.LineAt(line) == "second mine");
            code.Document.Select(new(row, 0), new(row, "second mine".Length));
            code.Document.Insert("hand resolved");
        });
        test.Check("merge editor: typing resolves the last conflict and enables Complete merge", () =>
            merge is { UnresolvedCount: 0, CanComplete: true } && code.Text == expected);
        test.Run(() => CompleteMergeEditor(document));
        test.WaitFor(() => finished.Task.IsCompletedSuccessfully && document.Merge is null, 12);
        test.Check("merge editor: Complete returns the exact resolved bytes", () =>
            finished.Task.IsCompletedSuccessfully && Encoding.UTF8.GetString(finished.Task.Result) == expected);

        test.Run(() =>
        {
            merge = OpenMergeEditor(Session(draft, _ => { binaryWrites++; return Task.CompletedTask; },
                () => cancelled = true));
            CancelMergeEditor(document);
        });
        test.Check("merge editor: cancelling an untouched session writes nothing", () =>
            cancelled && binaryWrites == 0 && document.Merge is null);

        test.Run(() =>
        {
            merge = OpenMergeEditor(new("Fixture.bin", "Mine", "Theirs", "Base", MergeModel.Binary([0], [1, 0], [2, 0]),
                bytes => { if (bytes.AsSpan().SequenceEqual(new byte[] { 2, 0 })) Interlocked.Increment(ref binaryWrites); return Task.CompletedTask; },
                static () => { }, Path.Combine(testFolder, "Fixture.bin")));
        });
        test.Check("merge editor: binary sessions show both side choices with tooltips", () =>
            merge is { Session.IsBinary: true, CanComplete: false } &&
            Descendants(merge.Root).OfType<Button>().Count(button => button.Visible && button.Label is "Keep mine" or "Take theirs" && button.Tooltip.Length > 0) == 2);
        test.Run(() => Descendants(merge!.Root).OfType<Button>().Single(button => button.Label == "Take theirs").OnPressed!());
        test.WaitFor(() => Volatile.Read(ref binaryWrites) == 1 && document.Merge is null, 12);
        test.Check("merge editor: the binary choice returns the selected file bytes", () => binaryWrites == 1 && document.Merge is null);

        TestManualMergeDelimiters(test);

        static double Contrast(Vector4 first, Vector4 second)
        {
            static double Level(float value) => value <= 0.04045f ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
            static double Luma(Vector4 color) => 0.2126 * Level(color.X) + 0.7152 * Level(color.Y) + 0.0722 * Level(color.Z);
            double a = Luma(first), b = Luma(second);
            return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
        }

        MergeSession Session(string text, Func<byte[], Task> complete, Action? cancel = null) =>
            new("Fixture.cs", "Mine", "Theirs", "Base", MergeModel.FromMarkers(text), complete,
                cancel ?? (static () => { }), Path.Combine(testFolder, "Fixture.cs"));
    }
}
