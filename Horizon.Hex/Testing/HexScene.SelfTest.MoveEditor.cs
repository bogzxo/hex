using System.Numerics;

using Horizon.HIDL.Runtime;
using Horizon.UI;
using Horizon.UI.Components;

namespace Horizon.Hex;

// The self test, the move editor (the extension in Assets/hex/extensions/move_editor) on a game of one fighter whose moves
// are in a shared file and one of its own. Every kind of edit made through the view and checked in the files' text, one
// step to undo, the timeline dragged, the red boxes, the problems, and the view laid out to whatever room it has.
internal sealed partial class HexScene
{
    private const string MOVE_VIEW = "move_editor.moves";

    private string ownMoves = string.Empty, sharedMoves = string.Empty, hitboxFile = string.Empty;

    private ExtensionUI? MoveView() => viewsMade.Values.FirstOrDefault(made => made.Owner.Name == "Move editor" && !made.Closed);

    private T Part<T>(string name) where T : UIComponent => MoveView()!.Made.Get<T>(name);

    private Func<Vector2> MovePart(string name) => () => chrome.ToWorld(MoveView()!.Made.Get<UIComponent>(name).Bounds.Center);

    // The state of the view in the extension, what it has on screen, for asking about it
    private ObjectValue? EditorState() =>
        MoveView()?.Owner.Runtime?.UserScope.Lookup("views") is ObjectValue views && views.Properties.Values.FirstOrDefault() is ObjectValue state ? state : null;

    private string? EditorText(string key) => EditorState()?.Properties.GetValueOrDefault(key) is StringValue text ? text.Value : null;

    // A move of the list picked by stepping up and down it the way the keys do, a few dozen steps at the most, the
    // test froze for good once when the move it went looking for wasn't there
    private void PickMove(string id)
    {
        for (int step = 0; step < 40 && EditorText("move") != id; step++)
            ExtensionKey(step < 20 ? "up" : "down");
    }

    private string FileText(string path) => OpenDocument(Path.GetFullPath(path))?.Editor.Text ?? System.IO.File.ReadAllText(path);

    private void MakeFightGame()
    {
        game = Path.Combine(testFolder, "fight_game");
        string data = Path.Combine(game, "Assets", "data");
        ownMoves = Path.Combine(data, "tester_moves.hor");
        sharedMoves = Path.Combine(data, "shared_moves.hor");
        hitboxFile = Path.Combine(data, "hitboxes", "tester.hor");
        Directory.CreateDirectory(data);

        // The art of the skin's sheet, its spinning cursor being an animation of four frames
        string skin = Path.Combine(AppContext.BaseDirectory, "Assets", "uix", "dead_revolver");
        string art = Path.Combine(game, "Assets", "art");
        Directory.CreateDirectory(art);
        foreach (string file in Directory.EnumerateFiles(skin))
            System.IO.File.Copy(file, Path.Combine(art, Path.GetFileName(file)), overwrite: true);
        if (System.IO.File.Exists(hitboxFile))
            System.IO.File.Delete(hitboxFile);

        System.IO.File.WriteAllText(Path.Combine(data, "characters.hor"),
            "let characters = { tester: { pretty_name: \"Tester\", sprites: \"Assets/art/sprites.hor\", frame_rate: 12, "
            + "moves: { shared: \"Assets/data/shared_moves.hor\", own: \"Assets/data/tester_moves.hor\" } } };\n");
        System.IO.File.WriteAllText(sharedMoves, "let moves = {\n"
            + "    idle: { phases: { stand: { animation: \"cursor_wait\", loop_while: \"always\" } } },\n"
            + "    // a kick everybody has\n"
            + "    kick: { input: \"B\", trigger: \"pressed\", priority: 30, damage: 7, phases: { swing: { animation: \"cursor_wait\", frames: 8, hit: 3 } } }\n};\n");
        System.IO.File.WriteAllText(ownMoves, "#define fight_move\n\nlet moves = {\n"
            + "    jab: {\n        input: \"Y\",\n        priority: 34,\n        damage: 5,\n        phases: {\n"
            + "            windup: { animation: \"cursor_wait\", frames: 4 },\n            swing: { frames: 6, hit: 2 }\n        }\n    },\n"
            + "    walk: { input: \"DPadRight\", priority: 50, phases: { go: { animation: \"cursor_wait\" } } }\n};\n");
    }

    private void TestMoveEditor(HexPointerScript test)
    {
        test.Run(() =>
        {
            MakeFightGame();
            Open(ownMoves);
        });
        test.WaitFor(() => document.View == HexView.Extension && MoveView() is { } && Part<ListBox>("moves").Items.Length > 0, 3.0f);

        test.Check("moves: a file that says #define fight_move opens on the move editor's view of it, Hex's right side away", () =>
            document.ExtensionView == MOVE_VIEW && MoveView() is { Root.Visible: true } && !right.Visible
            && viewButtons.Any(view => view.Id == MOVE_VIEW && view.Button.Selected));
        test.Check("moves: the fighter's moves out of both its files, the shared ones saying where they're written", () =>
            Part<Dropdown>("fighter").Value == "Tester" && Part<ListBox>("moves").Items is var rows
            && rows.Length == 4 && rows[0].StartsWith("idle", StringComparison.Ordinal) && rows[0].Contains("shared_moves", StringComparison.Ordinal)
            && rows.Any(row => row == "jab"));

        // Down the list onto the jab, idle and kick being first out of the shared file
        test.Run(() =>
        {
            ExtensionKey("down");
            ExtensionKey("down");
        });
        test.Wait();
        test.Check("moves: picked, on its hit frame, the art and its boxes, the phases along the timeline, the art of the phase, the frame data", () =>
            EditorText("move") == "jab" && Part<DrawingPanel>("canvas").SpriteCount > 0 && Part<DrawingPanel>("timeline").ShapeCount > 10
            && Part<DrawingPanel>("strip").SpriteCount == 4 && Part<Label>("frame_info").Text.StartsWith("7 of 10", StringComparison.Ordinal)
            && Part<Label>("data_text").Text.Contains("**startup**", StringComparison.Ordinal));

        TestPlaying(test);
        TestMoveForms(test);
        TestPhaseEditing(test);
        TestMovesOfTheFighter(test);
        TestRedBoxesAndProblems(test);
    }

    // Space plays the move and pauses it. The view is only ticked while it plays (or fits its frame), sat still it costs nothing
    private void TestPlaying(HexPointerScript test)
    {
        test.Settle(0.3f);
        test.Check("moves: sat still, the view isn't ticked", () => MoveView() is { Tick: null });

        test.Run(() => ExtensionKey("space"));
        test.Settle(0.4f);
        test.Check("moves: space plays it, the frames go by", () =>
            MoveView() is { Tick: not null } && !Part<Label>("frame_info").Text.StartsWith("7 of 10", StringComparison.Ordinal));

        test.Run(() => ExtensionKey("space"));
        test.Wait();
        test.Check("moves: and again pauses it, and it lets go of the ticks", () => MoveView() is { Tick: null });

        // Back onto the jab's hit frame for what comes after, picking it again does that
        test.Run(() =>
        {
            ExtensionKey("up");
            ExtensionKey("down");
        });
        test.Wait();
    }

    // The Move tab's form, a key changed, put in, taken out and undone, and a reroute
    private void TestMoveForms(HexPointerScript test)
    {
        test.Run(() => Part<FormPanel>("move_form").Enter("damage", new NumberValue(9)));
        test.Wait();
        test.Check("moves: a key changed in the form is changed where it's written in the file", () =>
        {
            return code.Text.Contains("        damage: 9,\n", StringComparison.Ordinal);
        });

        bool undid = false;
        test.Run(() => undid = ExtensionKey("undo"));
        test.Wait();
        test.Check("moves: one step to undo", () => undid && code.Text.Contains("damage: 5,", StringComparison.Ordinal));

        test.Run(() => Part<FormPanel>("move_form").Enter("level", new StringValue("low")));
        test.Wait();
        test.Check("moves: a key it hadn't got goes in after the keys that come before it", () =>
            code.Text.Contains("        damage: 5,\n        level: \"low\",\n        phases", StringComparison.Ordinal));

        test.Run(() => Part<FormPanel>("move_form").Unset("level"));
        test.Wait();
        test.Check("moves: and its x takes it out again", () => !code.Text.Contains("level", StringComparison.Ordinal));

        test.Run(() => Part<FormPanel>("reroute_form").Enter("stance_reroutes.crouching", new StringValue("walk")));
        test.Wait();
        test.Check("moves: a reroute, the object of them made for the first", () =>
            code.Text.Contains("stance_reroutes: { crouching: \"walk\" }", StringComparison.Ordinal));
    }

    // The Phase tab and the timeline
    private void TestPhaseEditing(HexPointerScript test)
    {
        test.Check("moves: the phase of the hit frame is the one picked", () => EditorText("phase") == "swing");

        test.Run(() => Part<FormPanel>("phase_form").Enter("frames", new NumberValue(8)));
        test.Wait();
        test.Check("moves: a key of a phase changed in its form", () => code.Text.Contains("swing: { frames: 8, hit: 2 }", StringComparison.Ordinal));

        test.Run(() => Part<TabPanel>("tabs").Select(1));
        test.Wait();
        test.Click(MovePart("phase_after"));
        test.Wait();
        test.Check("moves: a phase put in after the one picked, and picked", () =>
            code.Text.Contains("swing: { frames: 8, hit: 2 },\n            follow: { frames: 4 }", StringComparison.Ordinal) && EditorText("phase") == "follow");

        test.Click(MovePart("phase_earlier"));
        test.Wait();
        test.Check("moves: and played sooner", () =>
            code.Text.IndexOf("follow:", StringComparison.Ordinal) < code.Text.IndexOf("swing:", StringComparison.Ordinal));

        // The end of the swing dragged two cells on, its frames written when it's let go of
        Vector2 from = Vector2.Zero, to = Vector2.Zero;
        test.Run(() =>
        {
            from = TimelineEnd("swing", 0);
            to = TimelineEnd("swing", 2);
        });
        test.Drag(() => from, () => to);
        test.Wait();
        test.Check("moves: the end of a phase dragged along the timeline, how long it is written once it's let go of", () =>
            code.Text.Contains("swing: { frames: 10, hit: 2 }", StringComparison.Ordinal));
    }

    // Where the end of a phase's block is on the timeline, in the world, and how wide a cell is in the drawing
    private float TimelineCell()
    {
        var timeline = Part<DrawingPanel>("timeline");
        int count = EditorState()?.Properties["laid"] is ObjectValue laid && laid.Properties["frames"] is ListValue frames ? frames.Count : 1;
        return MathF.Min(22, MathF.Max(4, (timeline.Bounds.Width / timeline.Zoom - 20) / count));
    }

    private Vector2 TimelineEnd(string phase, int cellsOn)
    {
        var timeline = Part<DrawingPanel>("timeline");
        if (EditorState()?.Properties["laid"] is not ObjectValue laid || laid.Properties["phases"] is not ListValue phases)
            return Vector2.Zero;

        int count = laid.Properties["frames"] is ListValue frames ? frames.Count : 1;
        float cell = TimelineCell();
        foreach (IRuntimeValue item in phases.Items)
        {
            if (item is ObjectValue block && block.Properties["name"] is StringValue { Value: var name } && name == phase
                && block.Properties["first"] is NumberValue first && block.Properties["length"] is NumberValue length)
            {
                float x = -cell * count / 2.0f + cell * (first.Value + length.Value + cellsOn);
                return timeline.Module!.ToWorld(timeline.ToScreen(new Vector2(x, 22.0f)));
            }
        }

        return Vector2.Zero;
    }

    // Moves written in the shared file, new ones, renamed and taken out
    private void TestMovesOfTheFighter(HexPointerScript test)
    {
        test.Run(() =>
        {
            ExtensionKey("up");
            Part<FormPanel>("move_form").Enter("damage", new NumberValue(11));
        });
        test.Wait();
        test.Check("moves: a move of the shared file changed in the shared file, opened for it behind the one on screen", () =>
            EditorText("move") == "kick" && FileText(sharedMoves).Contains("damage: 11", StringComparison.Ordinal)
            && document.Path is { } here && Path.GetFullPath(here) == Path.GetFullPath(ownMoves)
            && OpenDocument(Path.GetFullPath(sharedMoves)) is { Modified: true });

        test.Run(() =>
        {
            ExtensionKey("down");
            TypeInto(Part<TextBox>("move_name"), "uppercut");
            Part<Dropdown>("template").Value = "launcher";
        });
        test.Click(MovePart("new_move"));
        test.Wait();
        test.Check("moves: a new move out of a template, after the one picked, and picked", () =>
            code.Text.Contains("uppercut: {", StringComparison.Ordinal) && code.Text.Contains("knockback: vec(300, 2000)", StringComparison.Ordinal)
            && EditorText("move") == "uppercut");

        test.Run(() =>
        {
            PickMove("walk");
            TypeInto(Part<TextBox>("move_name"), "stroll");
        });
        test.Click(MovePart("rename_move"));
        test.Wait();
        test.Check("moves: renamed, and the reroutes to it with it", () =>
            code.Text.Contains("stroll: {", StringComparison.Ordinal) && code.Text.Contains("crouching: \"stroll\"", StringComparison.Ordinal)
            && !code.Text.Contains("walk", StringComparison.Ordinal));

        test.Run(() =>
        {
            PickMove("uppercut");
        });
        test.Click(MovePart("delete_move"));
        test.Wait();
        test.Check("moves: taking one out asks for a second press", () => code.Text.Contains("uppercut", StringComparison.Ordinal));
        test.Click(MovePart("delete_move"));
        test.Wait();
        test.Check("moves: and the second takes it out", () => !code.Text.Contains("uppercut", StringComparison.Ordinal));
    }

    private void TestRedBoxesAndProblems(HexPointerScript test)
    {
        test.Run(() =>
        {
            PickMove("jab");
            Part<TabPanel>("tabs").Select(2);
        });
        test.Click(MovePart("from_trace"));
        test.Wait();
        test.Check("moves: a red box from the trace, the move saying it's drawn", () =>
            Part<ListBox>("moves").Items.Any(row => row.StartsWith("jab  (drawn)", StringComparison.Ordinal))
            && Part<Label>("frame_info").Text.Contains("unsaved", StringComparison.Ordinal));

        test.Click(MovePart("save"));
        test.WaitFor(() => System.IO.File.Exists(hitboxFile), 2.0f);
        test.Check("moves: the red boxes saved the way the game reads them", () =>
            System.IO.File.Exists(hitboxFile) && System.IO.File.ReadAllText(hitboxFile) is var saved
            && saved.Contains("jab: { min: vec(", StringComparison.Ordinal) && new Horizon.HIDL.HIDLRuntime { Output = null }.Evaluate(saved).success);

        test.Run(() => code.Document.Replace(code.Text.Replace("priority: 34,", "priority: 34,\n        bogus: 1,")));
        test.WaitFor(() => problemList.Any(problem => problem.Text.Contains("hasn't got a bogus", StringComparison.Ordinal)), 3.0f);
        test.Check("moves: what the game wouldn't load is in Hex's problems, where it's written", () =>
            problemList.Any(problem => problem.Text.Contains("hasn't got a bogus", StringComparison.Ordinal) && problem.Line == code.Text.Split('\n').ToList().FindIndex(line => line.Contains("bogus"))));
        test.Run(() => code.Document.Undo());
        test.Wait();
    }

    // The view laid out to the room it has, the sides of Hex away and back, the tabs dragged wide and narrow
    private void TestMoveEditorLayout(HexPointerScript test)
    {
        bool Inside()
        {
            if (MoveView() is not { } view)
                return false;

            UIRect room = extensionViews.Bounds;
            bool fits = true;
            foreach (string part in (string[])["left", "canvas", "timeline", "right"])
            {
                UIRect at = view.Made.Get<UIComponent>(part).Bounds;
                if (!view.Made.Get<UIComponent>(part).Visible) continue;
                Console.WriteLine($"[Hex self-test]   {part} {at.Min.X:0}..{at.Max.X:0} in {room.Min.X:0}..{room.Max.X:0}");
                fits &= at.Min.X >= room.Min.X - 1 && at.Max.X <= room.Max.X + 1;
            }

            return fits;
        }

        test.Settle(0.3f);
        test.Check("moves: the view's columns are inside it", Inside);
        test.Run(ToggleProperties);
        test.Settle(0.3f);
        test.Check("moves: and still are with the right side brought back by hand", () => right.Visible && Inside());
        test.Run(ToggleSideBar);
        test.Settle(0.3f);
        test.Check("moves: and with the left one away", () => !left.Visible && Inside());
        test.Run(ToggleSideBar);
        test.Settle(0.3f);
        test.Check("moves: and with it back", () => left.Visible && Inside());

        Vector2 edge = Vector2.Zero;
        void TakeEdge() => edge = chrome.ToWorld(new Vector2(Part<UIComponent>("right").Bounds.Min.X + 1.0f, Part<UIComponent>("right").Bounds.Center.Y));

        test.Run(TakeEdge);
        test.Drag(() => edge, () => edge - new Vector2(900, 0) * compositor.UIScale);
        test.Settle(0.3f);
        test.Check("moves: the tabs dragged as wide as they go, nothing leaves the view", () => Inside() && Part<UIComponent>("canvas").Bounds.Width >= 159);
        test.Run(TakeEdge);
        test.Drag(() => edge, () => edge + new Vector2(900, 0) * compositor.UIScale);
        test.Settle(0.3f);
        test.Check("moves: and back as narrow as they go, the moves beside them again", () => Inside() && Part<UIComponent>("left").Visible);

        test.Run(() => ToggleView());
        test.Wait();
        test.Check("moves: F7 goes on round the views of the file, back to its code, its values beside it again", () =>
            document.View == HexView.Code && MoveView() is { Root.Visible: false } && code.Visible && right.Visible);

        test.Run(() =>
        {
            CloseNow();
            if (OpenDocument(Path.GetFullPath(sharedMoves)) is { } shared)
            {
                Show(shared);
                CloseNow();
            }
        });
        test.Wait();
        test.Check("moves: and the view goes with the file", () => MoveView() is null);
    }
}
