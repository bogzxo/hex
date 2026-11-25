# Hex Link demo video: plan

Status: draft for proofreading, second version. Nothing has been produced yet.

Changes from the first version: the three people are coders named Charles, Neo and John, and the video is a screen recording of the real Hex, driven by a script that moves the real mouse cursor and types real keys. It is no longer drawn animation.

## 1. What the video is for

One video that shows, without a voice having to explain it, how three coders work on the same code at the same time in Hex and do not get in each other's way.

The viewer should leave with three things:

1. Everyone sees everyone else's typing, live.
2. Half-finished work can be held back, so it never breaks anyone else's build.
3. When two people do change the same lines, Hex shows both versions and one click settles it.

Because it is the real program, recorded, the video is also proof: nothing in it is a mock-up.

## 2. Audience and where it runs

- Students in a group project and small teams of programmers. They know Git, they have used a shared document, and they have all been burned by a broken build the night before a deadline.
- Runs on the website (top of the Hex Link section, muted, with a play button), in the GitHub README, and as a clip for social posts.
- It has to work with the sound off.

## 3. Format

| | |
|---|---|
| Length | About 80 seconds for the main cut. A 20 second cut and a 6 second loop are taken from it |
| Frame | 16:9, 2560×1440 capture, delivered at 1920×1080, 60 frames a second |
| Picture | Three real Hex windows side by side on one screen, one per coder, Solstice night theme, editor font enlarged so code reads at video size |
| Words on screen | Short captions added afterwards, sentence case, at most seven words each. No voice-over |
| Sound | The real recording is silent. Optional: soft key clicks, one tone when work is held, one when a merge completes |

## 4. The cast

Three coders. Each is one Hex window with its own profile and device name, so the names appear in the real interface: on the carets, in the Devices window, in notices.

| Name | Window | Role in the story |
|---|---|---|
| Charles | Left | Owns the shared folder (its source of truth). Writes steady, finished code |
| Neo | Middle | In the middle of a big, risky change |
| John | Right | Joins, fixes a small bug |

Each coder's caret colour is whatever Hex gives that device, and stays the same for the whole video. No faces, no avatars.

The project is a small real C# project made for the video (working name `Synth`), with one file the story happens in, `Mixer.cs`, about fifteen lines. It builds, and the build really fails when Neo's half-done change is in it.

## 5. How it is recorded: the real Hex, driven by a script

### The set-up

- Three Hex instances of the release build on one Windows machine, each started with its own settings folder and device name (Charles, Neo, John), paired with each other over Hex Link on the local machine. One screen holds all three, tiled left, middle, right, each 853×1440 at capture size, or two rows if that is too narrow for the Devices window (to be tried in the rehearsal).
- A clean Windows desktop: taskbar hidden, notifications off, a plain dark wallpaper, no other windows.
- Each instance has the same `Synth` folder open from its own copy on disk (three folders), shared through Link, so sync is real.

Three windows on one machine is chosen over three real machines because one recording then holds all three coders in frame and in step. A second take on three real machines (PC, laptop, Mac) can be added later as a short "and yes, across real machines" shot.

### The script

One driver script, `scripts/demo/link-demo.ps1` (PowerShell with a small C# helper for input), plays the whole story from a scene file.

- **Real input only.** The cursor is moved with the operating system's input call (`SendInput`), along an eased curved path at a human speed (about 600 to 900 pixels a second, slowing into the target), and clicks are real clicks. Text is typed as real key presses at about 12 to 14 characters a second with small random gaps, so it reads as a person typing. Nothing is injected into Hex's buffers.
- **Finding things on screen.** Before each click the script asks the target Hex instance for its interface tree over Hex's MCP server (`hex_ui_tree`: every control with its text and bounds), turns the control's bounds into screen coordinates for that window, and moves the cursor there. No fixed pixel positions, so a layout change does not break the script.
- **Waiting for the product, never for the clock.** After an action the script waits for the real result (a caret appearing in the other window, "Saved", the "1 held" chip, the build finishing) by polling that instance's interface tree or sync status, with a timeout that fails the take loudly. Fixed pauses are only used for pacing the viewer's reading time.
- **Scene file.** `scripts/demo/link-demo.scenes.json` lists the beats: which window, which control or text to type, what to wait for, how long to hold for the viewer. Changing the story is editing this file.
- **Reset.** `link-demo.ps1 -Reset` puts the three folders back to the starting commit, clears holds, closes extra tabs and restores window positions, so every take starts identical.
- **Dry run.** `-DryRun` plays the take with the cursor path drawn but nothing clicked, to check positions.
- **One cursor, three coders.** There is only one real mouse. The script moves it from window to window, so two people never click at the same instant. Simultaneous typing (scene 3) is shown by typing a few characters in one window, then a few in the other, alternating quickly, which reads as both typing at once; the carets in the third window show both moving. This limit is stated here so nobody expects true parallel input.

### The recording

- Captured with ffmpeg's desktop duplication capture (`ddagrab`) at 60 frames a second, lossless or near-lossless, started and stopped by the driver script so the take and the capture line up. OBS driven over its websocket is the fallback if ffmpeg capture drops frames.
- The cursor is recorded. It is enlarged slightly (Windows pointer size) so it is visible at 1080p.
- The script writes a timing log (`take-07.json`: when each beat started and ended). The edit uses it to place captions and zooms exactly.

### The edit

- Cuts between beats, speed-ups of waiting (a build, a first sync) marked with a small "×4" tag so it stays honest.
- Zooms into one window when the detail matters (the merge editor), back out to all three for the pay-off.
- Captions and the end card are added in the edit. They are the only things in the video that are not the real screen.
- Nothing is retouched: no painted-over text, no fake cursors.

## 6. Storyboard

Times are for the 80 second cut, after editing.

### Scene 1: the problem (0:00 to 0:09)

- All three windows, `Mixer.cs` open in each. Neo's half-done change is in the shared folder. Charles presses build: the Problems panel fills with errors. John's build fails too.
- Caption: "One half-done change. Everyone's broken build."
- Cut to black for half a second. Caption: "It doesn't have to work like that."

This scene is recorded with holding switched off, which is how it really behaves without the feature.

### Scene 2: joining (0:09 to 0:19)

- Charles's window. The cursor opens the Devices window and presses the invite button. The six-digit code shows.
- The cursor crosses to John's window, opens Join, types the code. Both windows show the matching code and the permission choice. John is accepted.
- Charles presses "Send workspace to John". John's explorer fills with the project (sped up, tagged).
- Caption: "Pair with a code. No account, no server."

### Scene 3: typing together (0:19 to 0:34)

- All three in `Mixer.cs`. Charles types a line near the top. In Neo's and John's windows a caret labelled "Charles" writes the same line as it is typed.
- John fixes a bug four lines lower, alternating with Charles. Two named carets move in Neo's window. The edits never touch.
- Caption: "Type in the same file. See each other's carets."
- Charles presses Ctrl+S once. All three tabs lose their unsaved dot.
- Caption: "One save. Every machine."

### Scene 4: holding half-done work (0:34 to 0:50)

The centre of the video, and the longest scene.

- Neo starts the big change: renames a method, leaves the body half written. Red squiggles appear in his window.
- The cursor right-clicks the tab and picks "Hold changes". The held marker appears on the tab and in the explorer, and the Devices row shows "1 held".
- Wide shot: Charles and John still have the old, working file. Charles presses build: it passes.
- Caption: "Not finished? Hold it. Nobody else gets it yet."
- Charles edits another line of the same file. In Neo's window the notice appears: Charles changed a file you hold. Neo's code does not move.
- Caption: "Their work keeps flowing. Yours stays yours."

### Scene 5: submitting, and the one real clash (0:50 to 1:08)

- Neo finishes the method. The squiggles clear. He runs "Submit held changes".
- The submit dialog lists the file: part merges cleanly, one block needs the merge editor.
- Zoom into the merge editor in Neo's window: Mine, Result, Theirs, the clashing lines outlined, "1 of 1 conflicts left".
- The cursor presses "Use both". The counter reaches 0. The cursor presses "Save merged file".
- Wide shot: the merged file arrives in Charles's and John's windows. Charles presses build: it passes.
- Caption: "When it does collide, you choose. One click."

### Scene 6: close (1:08 to 1:20)

- The three windows stay on screen, dimmed. Three lines appear one at a time:
  - "Live editing across your machines"
  - "Held changes, so builds stay green"
  - "A merge editor for the rest"
- The Hex mark, then "Hex Link. In Hex 3.4.0." and the address.

## 7. Shorter cuts

- 20 seconds: scene 3's typing (6 s), scene 4's hold (8 s), scene 5's complete merge (4 s), end card (2 s).
- 6 second loop for the website and README: two named carets typing in one file, one save. No captions.

## 8. Rules for the take

- Everything on screen is the release build doing the real thing. If a beat cannot be done for real, it is cut, not faked.
- Cursor and typing move at human speed. Waiting is shortened in the edit and tagged, never hidden.
- One thing happens at a time, except the alternating typing in scene 3.
- The code on screen is real, short, and compiles when the story says it does.
- No personal names, paths, account names or other projects in frame: the three profiles and the `Synth` folders live under a neutral path such as `C:\demo\`.
- Every caption stays up for at least two seconds.

## 9. Steps

1. Lock this plan and the captions.
2. Make the `Synth` project and the exact edits each coder types. Check by hand, once, that the story works in the real program from start to finish.
3. Write the driver script, the reset, and the scene file. Dry runs until every beat lands.
4. Rehearsal takes at full size. Fix pacing in the scene file.
5. Record final takes (several, pick the cleanest). Keep the timing logs.
6. Edit: cuts, speed-ups, zooms, captions, end card. Optional sound.
7. Export the 80 second cut, the 20 second cut and the loop. Check on a phone with the sound off.

## 10. What gets delivered

- `hex-link-80s.mp4` (H.264, 1080p) and `.webm`, with a poster frame
- `hex-link-20s.mp4`
- `hex-link-loop.webm` and an animated fallback image, under 2 MB
- A captions file (`.vtt`)
- `scripts/demo/link-demo.ps1`, the input helper, the scene file and the reset, so the video can be recorded again when the interface changes
- The `Synth` demo project

## 11. Risks and what is done about them

| Risk | Answer |
|---|---|
| The intermittent case where a third device keeps its old file and reports a conflict shows up in a take | The script checks all three files match after each sync beat and fails the take. It is also the best reproduction rig for that bug |
| Three windows are too narrow for the Devices window or the merge editor | Rehearsal decides: tile in two rows, or record those beats with one window enlarged and cut to it |
| Capture drops frames while a build runs | Record at a lower load (build tagged and sped up), or capture with OBS |
| A notice or dialog covers a click target | The script reads the interface tree before every click and waits for the target to be clear |
| The interface wording changes after recording | The script finds controls by their text from one table at the top of the scene file; re-record with one command |

## 12. Things that must be true before it is published

- Held changes and the merge editor are in the released build (they are in the 3.4.0 candidate).
- Every button name and notice shown is the released interface, which it will be, since it is recorded from it.
- The take passes the script's own checks from start to finish, with no manual help.

## 13. Questions for you

1. Three windows on one machine for the main take, as planned, or do you want the main take on the three real machines?
2. Captions only, as planned, or a voice-over version too?
3. Does the site's blunt tone carry into the captions, or does the video stay clean?
4. Is 80 seconds right, or should the main cut be 45?
5. Sound: none, key clicks and two tones, or music?
6. Device names in frame are "Charles", "Neo" and "John". Should the project keep the working name `Synth`?
