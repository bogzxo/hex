# Hex Link demo video: plan

Status: draft for proofreading. Nothing has been produced yet.

## 1. What the video is for

One motion-graphic video that shows, without a voice having to explain it, how several students or programmers work on the same code at the same time in Hex and do not get in each other's way.

The viewer should leave with three things:

1. Everyone sees everyone else's typing, live.
2. Half-finished work can be held back, so it never breaks anyone else's build.
3. When two people do change the same lines, Hex shows both versions and one click settles it.

## 2. Audience and where it runs

- Students in a group project and small teams of programmers. They know Git, they have used a shared document, and they have all been burned by a broken build the night before a deadline.
- Runs on the website (top of the Hex Link section, muted, with a play button), in the GitHub README, and as a clip for social posts.
- It has to work with the sound off. Sound is an extra.

## 3. Format

| | |
|---|---|
| Length | 75 seconds for the main cut. A 20 second cut and a 6 second loop are taken from it |
| Frame | 16:9, 1920×1080, 60 frames a second. A 1:1 crop for social is planned for in the layouts (keep the action in the centre square) |
| Look | The website's own design: Solstice night background `#18171f`, surfaces `#201f2a`, Inter for words, Hack for code, the three face colours for the three people |
| Words on screen | Short captions, sentence case, at most seven words each. No voice-over in the main cut |
| Sound | Soft key clicks tied to typing, one low tone when work is held, one bright tone when a merge completes, a quiet bed under it. All optional |
| Language | English. Captions are separate text layers, so translating is a text change |

## 4. The cast

Three people, each with a machine and a colour that never changes. The colour is how the viewer follows who did what.

| Name | Machine | Colour | Role in the story |
|---|---|---|---|
| Maya | Desktop | Dawn orange `#da854d` | Owns the shared folder. Writes steady, finished code |
| Leo | Laptop | Dusk violet `#a99bff` | In the middle of a big, risky change |
| Sam | Mac | Noon teal `#64c3c8` | Joins late, fixes a small bug |

They are shown as a name, a coloured dot and a caret. No faces, no avatars, no stock characters.

The file they work in is small and readable at video size: `Mixer.cs`, about twelve lines, large type. Every line of code that appears is real, compiles, and is short enough to read in the time it is on screen.

## 5. Storyboard

Times are for the 75 second cut.

### Scene 1: the problem (0:00 to 0:09)

- Three editor windows, side by side, each with the same file. A red build badge on two of them: "Build failed".
- Caption: "One person's half-done change. Everyone's broken build."
- Leo's window is the only green one. A thin line shows his unfinished change travelling to the others and turning them red.
- The frame holds for one beat, then the red drains out and the windows reset to clean.
- Caption: "It doesn't have to work like that."

### Scene 2: joining (0:09 to 0:18)

- Maya's window alone. The Devices window slides in. She presses "Send workspace to Laptop".
- A six-digit code appears on both machines. The two codes match and tick.
- Leo's window arrives with the same files already in it. Then Sam's.
- Caption: "Pair with a code. No account, no server."
- A small chip under each machine: "LAN, 6 ms".

### Scene 3: typing together (0:18 to 0:32)

- All three have `Mixer.cs` open. Maya types a line. Her orange caret, with her name on it, moves through Leo's and Sam's windows at the same moment.
- Sam fixes a bug three lines lower at the same time. His teal caret shows in the other two windows. The two edits never touch.
- Caption: "Type in the same file. See each other's carets."
- Maya presses save once. A single ripple crosses all three windows and each shows "Saved".
- Caption: "One save. Every machine."
- A number counts up and stops beside the ripple: "0.3 s".

### Scene 4: holding half-done work (0:32 to 0:47)

This is the centre of the video and gets the most time.

- Leo starts his big change. He renames a method and the code under it goes red: it does not compile yet.
- Before it leaves his machine, he presses "Hold changes". A violet band wraps the file on his side and a chip reads "1 held".
- The view pulls back to all three machines. A soft violet wall sits between Leo's file and the others. Maya's and Sam's builds stay green. They keep typing.
- Caption: "Not finished? Hold it. Nobody else gets it yet."
- Maya changes a line in the same file. On Leo's side a small notice appears: "Maya changed a file you hold". His code does not move.
- Caption: "Their work keeps flowing. Yours stays yours."

### Scene 5: submitting, and the one real clash (0:47 to 1:04)

- Leo finishes. His code goes from red to clean. He presses "Submit held changes".
- Most of the file merges by itself: lines from Maya and lines from Leo slot together like a zip. Caption: "What doesn't collide merges by itself."
- One block does collide. The merge editor opens: Mine on the left, Theirs on the right, Result in the middle, the clashing lines outlined.
- Leo presses "Accept both". The two versions settle into the Result column in order. The counter goes from "1 conflict left" to "0".
- He presses "Complete merge". The violet wall dissolves, the finished change flows to Maya and Sam, and all three builds are green.
- Caption: "When it does collide, you choose. One click."

### Scene 6: close (1:04 to 1:15)

- The three windows shrink into a row. Under them, three short lines appear one at a time:
  - "Live editing across your machines"
  - "Held changes, so builds stay green"
  - "A merge editor for the rest"
- The Hex mark, then: "Hex Link. In Hex 3.4.0." and the address.
- No slogan beyond that.

## 6. Shorter cuts

- 20 seconds: scene 3 (typing together, 6 s), scene 4 (hold, 8 s), scene 5's last beat (complete merge, 4 s), end card (2 s).
- 6 second loop for the website and README: three carets typing in one file, one save ripple, loops cleanly. No captions.

## 7. Motion rules

- One thing moves at a time. The eye is led, never split, except in scene 3 where two people typing at once is the point.
- Colour means a person. Orange is always Maya, violet Leo, teal Sam. Red and green mean only "build fails" and "build passes".
- Text is typed at a readable speed (about 14 characters a second) and never faster than it could be read.
- Movement eases out and settles. Nothing bounces, nothing spins, no particle effects.
- Every caption stays up for at least two seconds.
- Nothing flashes more than three times a second.
- The interface shown is Hex's real interface, redrawn cleanly, with the real names of the buttons and the real wording of the notices. Nothing is shown that the release does not do.

## 8. How it gets made

Two ways to produce it. The first is recommended.

**A. Built in the browser, recorded by script.**
Each scene is an HTML and CSS page using the website's own stylesheet and colours, animated on a timeline. Playwright opens each scene at 1920×1080, steps through it frame by frame, and saves the frames. ffmpeg joins them into the video and adds the sound.

- For: it matches the website exactly, every frame is repeatable, a wording change is a text edit and a re-run, and the 6 second loop can also ship as a live element on the page.
- Against: fine easing and camera moves take more care than in a motion tool.

**B. Built in a motion graphics tool** (After Effects or similar) from exported interface drawings.

- For: the richest motion.
- Against: someone has to own the project file, every wording change is manual work, and it drifts from the product as the product changes.

Steps for route A:

1. Lock this plan and the captions.
2. Still frames: one finished frame for each scene, reviewed before anything moves.
3. Animatic: the stills cut to time with captions, no motion yet. Check the pacing at 75 seconds.
4. Animate scene 4 first, since it carries the video. Review.
5. Animate the rest. Add sound.
6. Export the 75 second cut, the 20 second cut, the loop, and the 1:1 crops.
7. Check on a phone with the sound off.

## 9. What gets delivered

- `hex-link-75s.mp4` (H.264, 1080p) and `.webm`, with a poster frame
- `hex-link-20s.mp4`
- `hex-link-loop.webm` and an animated fallback image, under 2 MB
- 1:1 crops of the 75 and 20 second cuts
- A captions file (`.vtt`) and the caption text as a plain list
- The scene sources, so the video can be regenerated when the interface changes

## 10. Things that must be true before it is published

- Held changes and the merge editor are in the released build. They are being built now. If they slip, scenes 4 and 5 are cut and the video becomes a 40 second piece about live editing only.
- Every button name and notice in the video matches the released interface word for word.
- The numbers shown ("0.3 s", "LAN, 6 ms") are the measured ones in `hex-link-test-evidence.md`.

## 11. Questions for you

1. Are Maya, Leo and Sam the right framing, or should it be two students and a teaching assistant, or a three-person team at work?
2. Captions only, or do you want a voice-over version too?
3. Does the site's blunt tone carry into the captions (the site swears), or does the video stay clean so it can be shown in a classroom?
4. Is 75 seconds right, or should the main cut be 45?
5. Route A (built in the browser) or route B (motion tool)?
6. Music: none, a quiet bed, or something with more energy?
