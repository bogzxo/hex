using System.Numerics;

using Horizon.HIDL.Runtime;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The self test, the move editor (the extension in Assets/hex/extensions/move_editor) on a game of one fighter whose moves
// are in a shared file and one of its own, in Fighter2D's second format (game frames and world units). Every kind of edit
// made through the view and checked in the files' text, one step to undo, the timeline dragged, the frame meter, the combo
// window, an old key said the new way, the red boxes, the problems, and the view laid out to whatever room it has.
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

    // A number of what the extension keeps, down a path of its objects ("meter_was.numbers.advantage"), NaN for none
    private float EditorNumber(string path)
    {
        IRuntimeValue? at = EditorState();
        foreach (string key in path.Split('.'))
            at = at is ObjectValue inside ? inside.Properties.GetValueOrDefault(key) : null;
        return at is NumberValue number ? number.Value : float.NaN;
    }

    // A move of the list picked by stepping up and down it the way the keys do, a few dozen steps at the most, the
    // test froze for good once when the move it went looking for wasn't there
    private void PickMove(string id)
    {
        for (int step = 0; step < 40 && EditorText("move") != id; step++)
            ExtensionKey(step < 20 ? "up" : "down");
    }

    private string FileText(string path) => OpenDocument(Path.GetFullPath(path))?.Editor.Text ?? System.IO.File.ReadAllText(path);

    // A combat_rules.hor for the pretend game, every key the move editor reads
    private const string CombatRulesOfTheGame = """
        let rules = {
            base_disadvantage: 4, startup_reward: 0.75, max_advantage: 24, hitstun_per_damage: 0.6, guessed_min_hitstun: 12,
            blockstun_scale: 0.7, guessed_min_blockstun: 7, chip_damage_scale: 0.12, block_push: 23,
            push: 16, attacker_recoil: 0.5, air_hit_pop: 14,
            combo_damage_scale: 0.88, min_combo_damage_scale: 0.35, counter_hitstun_bonus: 4, counter_damage_scale: 1.25,
            hit_movement_cancel: 4, juggle_gravity_step: 0.1, knockdown_ticks: 55,
            hitlag_per_damage: 0.65, hitlag_base: 6, max_hitlag: 30, block_hitlag_scale: 0.67, counter_hitlag_bonus: 4, ko_hitstop: 36,
            di_max_angle: 0.17, sdi_distance: 4, sdi_interval: 4, tech_window: 11, tech_lockout: 40,
            stale_steps: [0.09, 0.08, 0.07, 0.06, 0.05, 0.04, 0.03, 0.02, 0.01],
            parry_ticks: 5, parry_stun: 20,
            meter_per_damage_dealt: 1.0, meter_per_damage_taken: 0.6, meter_on_block: 3, meter_on_blocked: 2, meter_on_whiff: 1,
            movement: {
                walk_speed: 420, ground_acceleration: 4000, traction: 2800, air_speed: 330, air_acceleration: 1600, air_friction: 500,
                gravity: 3000, fall_speed: 620, fast_fall_speed: 950, jump_height: 134, short_hop_height: 60, double_jumps: 1,
                double_jump_height: 100, hitstun_gravity: 0.5, weight: 100
            }
        };
        """;

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

        // The game's combat rules, which the move editor reads out of the game it is editing the moves of (Fighter2D keeps
        // its in Assets/data/combat_rules.hor). These are the numbers the game shipped with when the editor learnt to read them
        System.IO.File.WriteAllText(Path.Combine(data, "combat_rules.hor"), CombatRulesOfTheGame);

        System.IO.File.WriteAllText(Path.Combine(data, "characters.hor"),
            "let characters = { tester: { pretty_name: \"Tester\", sprites: \"Assets/art/sprites.hor\", frame_rate: 12, "
            + "movement: { gravity: 4000, hitstun_gravity: 0.5 }, "
            + "moves: { shared: \"Assets/data/shared_moves.hor\", own: \"Assets/data/tester_moves.hor\" } } };\n");
        System.IO.File.WriteAllText(sharedMoves, "let moves = {\n"
            + "    idle: { phases: { stand: { animation: \"cursor_wait\", loop_while: \"always\" } } },\n"
            + "    // a kick everybody has, still saying its knockback the old way\n"
            + "    kick: { input: \"B\", trigger: \"pressed\", priority: 30, damage: 7, knockback: vec(300, 0), phases: { swing: { animation: \"cursor_wait\", frames: 8, hit: 3 } } }\n};\n");
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

        TestTheLayout(test);
        TestFrameMeter(test);
        TestPlaying(test);
        TestMoveForms(test);
        TestComboWindow(test);
        TestPhaseEditing(test);
        TestOldFormat(test);
        TestMovesOfTheFighter(test);
        TestRedBoxesAndProblems(test);
    }

    // The list as tall as the column, the toolbar of overlays over the frame, the frame fitted to the fighter with a floor
    // under him, the header saying what's on screen, and what went wrong on a card in the corner
    private void TestTheLayout(HexPointerScript test)
    {
        test.Settle(0.3f);
        test.Check("moves: the list takes the height of its column, far more than the four rows it had", () =>
            Part<ListBox>("moves") is var list && list.Rows >= 14 && list.Bounds.Min.Y >= extensionViews.Bounds.Min.Y - 1
            && list.Bounds.Min.Y - extensionViews.Bounds.Min.Y < 60);
        test.Check("moves: the fighter, the move, the phase and the file in a header over the frame", () =>
            Part<Dropdown>("fighter").Value == "Tester" && Part<Label>("written_in").Text is var said
            && said.Contains("**jab**", StringComparison.Ordinal) && said.Contains("swing", StringComparison.Ordinal)
            && said.Contains("tester_moves.hor", StringComparison.Ordinal));

        // Fitted to what's drawn rather than to the frame, so never smaller than the frame fitted whole, and the floor found
        test.Check("moves: the frame fitted to the fighter, with a floor where his idle's feet are", () =>
        {
            var canvas = Part<DrawingPanel>("canvas");
            float whole = MathF.Min((canvas.Bounds.Width - 56) / FrameSide(), (canvas.Bounds.Height - 56) / FrameSide());
            return !float.IsNaN(EditorNumber("floor")) && canvas.Zoom >= whole * 0.99f;
        });

        test.Check("moves: the overlays are a toolbar over the frame, lit while they're on", () =>
            Part<Button>("show_hurtbox").Selected && Part<Button>("show_traced").Selected && !Part<Button>("show_onion").Selected
            && Part<Button>("show_path").Selected && Part<UIComponent>("toolbar").Bounds.Min.Y >= Part<DrawingPanel>("canvas").Bounds.Max.Y);
        int shapes = 0;
        test.Run(() => shapes = Part<DrawingPanel>("canvas").ShapeCount);
        test.Click(MovePart("show_hurtbox"));
        test.Wait();
        test.Check("moves: and a press turns one off, the frame drawn without it", () =>
            !Part<Button>("show_hurtbox").Selected && Part<DrawingPanel>("canvas").ShapeCount < shapes);
        test.Click(MovePart("show_hurtbox"));
        test.Wait();

        test.Click(MovePart("new_move"));
        test.Wait();
        test.Check("moves: New with no name typed says so on a card in the corner and under the frame, not in a grey line nobody reads", () =>
            toasts.Cards.Any(card => card.Text.Contains("type a name for it first", StringComparison.Ordinal))
            && Part<Label>("status").Text == "type a name for it first");
        test.Run(() =>
        {
            foreach (UINotification card in toasts.Cards)
                card.Close();
        });
        test.Wait();
    }

    // How big a frame of the test's art is, the skin's sheet
    private float FrameSide() => EditorState()?.Properties.GetValueOrDefault("art") is Horizon.UI.Drawing.UISprite art
        ? MathF.Max(art.FrameSize.X, art.FrameSize.Y) : 128.0f;

    // The frame meter over the timeline, the jab's on hit and on block where the Data tab says they are, the row of them
    // swapped by a click on its name
    private void TestFrameMeter(HexPointerScript test)
    {
        // 6 frames of startup, 1 active and 3 after it (the game's recovery of 4 counts the active one), hitstun guessed at
        // the least of 12 and blockstun at 8 out of it
        test.Check("moves: the meter counts you and them, the advantage where you're both free the same as the frame data's on hit", () =>
            EditorNumber("meter_was.numbers.startup") == 6 && EditorNumber("meter_was.numbers.active") == 1
            && EditorNumber("meter_was.numbers.recovery") == 3 && EditorNumber("meter_was.numbers.stun") == 12
            && EditorNumber("meter_was.numbers.advantage") == 8 && Part<Label>("data_text").Text.Contains("**on hit** +8", StringComparison.Ordinal)
            && Part<DrawingPanel>("timeline").ShapeCount > 30);

        test.Click(() => TimelineGutter(31.5f));
        test.Wait();
        test.Check("moves: a click on their name and it's on block", () =>
            EditorText("meter_on") == "block" && EditorNumber("meter_was.numbers.stun") == 8
            && EditorNumber("meter_was.numbers.advantage") == 4 && Part<Label>("data_text").Text.Contains("**on block** +4", StringComparison.Ordinal));
        test.Click(() => TimelineGutter(31.5f));
        test.Wait();
        test.Check("moves: and another back on hit, picking nothing", () =>
            EditorText("meter_on") == "hit" && EditorText("move") == "jab" && EditorText("phase") == "swing");
    }

    // A point of the timeline left of its cells, where the meter's rows are named, so high in the drawing, in the world
    private Vector2 TimelineGutter(float y)
    {
        var timeline = Part<DrawingPanel>("timeline");
        return timeline.Module!.ToWorld(timeline.ToScreen(new Vector2(EditorNumber("timeline_at.left") - 20.0f, y)));
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

    // The Data tab's combo window, a launch put on the jab moving the arc and the numbers under it, hitstun, the launch's
    // growth and a hit cancel moving them again, and all of it taken back
    private void TestComboWindow(HexPointerScript test)
    {
        test.Run(() =>
        {
            Part<TabPanel>("tabs").Select(3);
            Part<FormPanel>("move_form").Enter("launch", Values.Vector([20.0f, 90.0f]));
        });
        test.Wait();
        test.Check("moves: a launch written in world units, the combo window working out the arc and when the attacker's free", () =>
            code.Text.Contains("launch: vec(20, 90)", StringComparison.Ordinal)
            && Part<Label>("combo_text").Text is var said && said.Contains("**Combo window**", StringComparison.Ordinal)
            && said.Contains("they're 35.6 up", StringComparison.Ordinal)
            && said.Contains("Free 4 frames after the hit at the end of the move, **8 frames before they recover**", StringComparison.Ordinal)
            && Part<DrawingPanel>("combo_arc").ShapeCount > 10);
        test.Check("moves: the jab's 5 damage freezes them Smash's 9 frames, 13 on a counter and 6 blocked, and DI's cone round the launch", () =>
            Part<Label>("data_text").Text.Contains("Both freeze **9** on the hit (5 damage * 0.65 + 6, 30 at most), 13 on a counter and 6 on a block", StringComparison.Ordinal)
            && Part<Label>("combo_text").Text.Contains("DI turns it up to 9.74 degrees either way", StringComparison.Ordinal));

        test.Run(() => Part<FormPanel>("move_form").Enter("hitstun", new NumberValue(40)));
        test.Wait();
        test.Check("moves: an exact hitstun moves when they recover, long enough they get to the top of the launch, and the counter hit is 4 on top", () =>
            Part<Label>("combo_text").Text.Contains("Their hitstun runs out on frame 40", StringComparison.Ordinal)
            && Part<Label>("combo_text").Text.Contains("The top is 90 up", StringComparison.Ordinal)
            && Part<Label>("data_text").Text.Contains("**counter hit** 44", StringComparison.Ordinal));
        test.Check("moves: and the meter says what its numbers were, lit, ticking until they go out", () =>
            EditorNumber("meter_was.numbers.stun") == 40 && EditorNumber("lit.stun.from") == 12
            && EditorNumber("lit.advantage.from") == 8 && EditorNumber("meter_was.numbers.advantage") == 36 && MoveView() is { Tick: not null });
        test.WaitFor(() => float.IsNaN(EditorNumber("lit.stun.from")) && MoveView() is { Tick: null }, 4.0f);
        test.Check("moves: a couple of seconds on they've gone out and the ticks are let go of", () =>
            float.IsNaN(EditorNumber("lit.stun.from")) && MoveView() is { Tick: null });

        test.Run(() => Part<FormPanel>("move_form").Enter("launch_growth", Values.Vector([2.0f, 5.0f])));
        test.Wait();
        test.Check("moves: launch_growth says where the launch goes as they lose health, 6 and 15 further with 30 gone", () =>
            code.Text.Contains("launch_growth: vec(2, 5)", StringComparison.Ordinal)
            && Part<Label>("data_text").Text.Contains("then 26 along and 105 up with 30 lost", StringComparison.Ordinal));

        test.Run(() => Part<FormPanel>("move_form").Enter("hit_cancel", new StringValue("jump | special")));
        test.WaitFor(() => problemList.Any(problem => problem.Text.Contains("has no jump to cancel into", StringComparison.Ordinal)), 3.0f);
        test.Check("moves: a hit cancel written as a list, free the frame after the hit, a move it names that isn't there and a kind nobody is in the problems", () =>
            code.Text.Contains("hit_cancel: [\"jump\", \"special\"]", StringComparison.Ordinal)
            && Part<Label>("combo_text").Text.Contains("Free 1 frame after the hit by hit_cancel", StringComparison.Ordinal)
            && problemList.Any(problem => problem.Text.Contains("has no jump to cancel into, and it isn't a kind of move", StringComparison.Ordinal))
            && problemList.Any(problem => problem.Text.Contains("has no special moves", StringComparison.Ordinal)));

        test.Run(() =>
        {
            ExtensionKey("undo");
            ExtensionKey("undo");
            ExtensionKey("undo");
            ExtensionKey("undo");
        });
        test.Wait();
        test.Check("moves: and four undos take the four of them back", () =>
            !code.Text.Contains("launch", StringComparison.Ordinal) && !code.Text.Contains("hit_cancel", StringComparison.Ordinal)
            && !code.Text.Contains("hitstun", StringComparison.Ordinal));
    }

    // The kick in the shared file says knockback, which the game won't load any more, the problems say so and the Move tab
    // says it the new way
    private void TestOldFormat(HexPointerScript test)
    {
        test.Run(() =>
        {
            Part<TabPanel>("tabs").Select(0);
            PickMove("kick");
        });
        test.WaitFor(() => problemList.Any(problem => problem.Text.Contains("knockback isn't a thing any more", StringComparison.Ordinal)), 3.0f);
        test.Check("moves: a key of the old format is an error, and the Move tab offers to say it the new way", () =>
            problemList.Any(problem => problem.Text.Contains("knockback isn't a thing any more", StringComparison.Ordinal) && !problem.Warning)
            && Part<UIComponent>("upgrade_row").Visible);

        test.Click(MovePart("upgrade"));
        test.Wait();
        test.Check("moves: a knockback that stays on the ground turned into a push through the old curve, where it was written", () =>
            FileText(sharedMoves) is var shared && shared.Contains("damage: 7, push: 37, phases", StringComparison.Ordinal)
            && !shared.Contains("knockback:", StringComparison.Ordinal) && !Part<UIComponent>("upgrade_row").Visible);

        test.Run(() => PickMove("jab"));
        test.Wait();
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

        // The end of the active bar dragged from the hit two cells on, the hit out for three frames
        test.Run(() =>
        {
            from = TimelineActiveEnd("swing", 0);
            to = TimelineActiveEnd("swing", 2);
        });
        test.Drag(() => from, () => to);
        test.Wait();
        test.Check("moves: the active bar's handle dragged, how long the hit's out for written on the phase with it", () =>
            code.Text.Contains("swing: { frames: 10, hit: 2, active: 3 }", StringComparison.Ordinal));

        // The swing carries on a frame of art, 5 game frames at 12 a second and 10 at half of that, which is what it says.
        // A click on its block picks it
        test.Run(() => from = TimelineEnd("swing", -3));
        test.Click(() => from);
        test.Wait();
        test.Check("moves: the length a phase's art gives it, at the fighter's frame rate", () =>
            EditorText("phase") == "swing" && Part<Label>("phase_head").Text.Contains("its art would make it 5", StringComparison.Ordinal));
        test.Run(() => Part<FormPanel>("phase_form").Enter("art_speed", new NumberValue(0.5f)));
        test.Wait();
        test.Check("moves: and at half the art speed, twice that", () =>
            code.Text.Contains("art_speed: 0.5", StringComparison.Ordinal) && !Part<Label>("phase_head").Text.Contains("its art would make it", StringComparison.Ordinal));
        test.Run(() => ExtensionKey("undo"));
        test.Wait();
    }

    // Where the end of a phase's block is on the timeline, in the world. Where the cells start and how wide they are is the
    // extension's to say, the meter decides how many frames they span
    private Vector2 TimelineEnd(string phase, int cellsOn)
    {
        var timeline = Part<DrawingPanel>("timeline");
        if (EditorState()?.Properties["laid"] is not ObjectValue laid || laid.Properties["phases"] is not ListValue phases)
            return Vector2.Zero;

        float left = EditorNumber("timeline_at.left"), cell = EditorNumber("timeline_at.cell");
        foreach (IRuntimeValue item in phases.Items)
        {
            if (item is ObjectValue block && block.Properties["name"] is StringValue { Value: var name } && name == phase
                && block.Properties["first"] is NumberValue first && block.Properties["length"] is NumberValue length)
            {
                float x = left + cell * (first.Value + length.Value + cellsOn);
                return timeline.Module!.ToWorld(timeline.ToScreen(new Vector2(x, 0.0f)));
            }
        }

        return Vector2.Zero;
    }

    // Where the handle at the end of a phase's active bar is on the timeline, so many cells on, in the world
    private Vector2 TimelineActiveEnd(string phase, int cellsOn)
    {
        var timeline = Part<DrawingPanel>("timeline");
        if (EditorState()?.Properties["laid"] is not ObjectValue laid || laid.Properties["hit"] is not NumberValue hit
            || laid.Properties["active"] is not NumberValue active)
            return Vector2.Zero;

        float left = EditorNumber("timeline_at.left"), cell = EditorNumber("timeline_at.cell");
        float x = left + cell * (hit.Value + active.Value - 1 + cellsOn) + cell * 0.5f;
        return timeline.Module!.ToWorld(timeline.ToScreen(new Vector2(x, 19.5f)));
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
            code.Text.Contains("uppercut: {", StringComparison.Ordinal) && code.Text.Contains("launch: vec(20, 90)", StringComparison.Ordinal)
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
            // the cards the editor put up (press delete again) go with it, the garbage sweep after doesn't want them fading
            foreach (UINotification card in toasts.Cards)
                card.Close();
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
