# Real-world stability test

Goal: find out whether XStream is actually steadier than the official Xbox app on *your* network, in normal play, not just in the simulator.

## What you can test today

XStream plays with video, audio and controller: use the launcher, or `xstream play <serverId>` from the command line, on the Mac or the ROG Ally.

## The comparison

Each "pair" is one session with the official app and one with XStream, in the same place, on the same day.

| Rule | Why |
|---|---|
| Same spot (room, sofa, desk) and same network | Location matters more than anything else |
| Same game, similar play (e.g. both online matches) | Fast scenes need more bitrate |
| 15–20 minutes each | Short sessions are mostly luck |
| Alternate who goes first (official first today, XStream first tomorrow) | Evenings get busier as time goes on |
| Same quality setting: official app on its default; XStream `balanced 15` | Keeps bitrate comparable |
| At least 3 pairs per spot before drawing conclusions | One bad evening isn't a trend |

## Spots worth testing (pick 2–3)

- Your usual spot (where you play most)
- The furthest room you'd play from
- Moving between rooms mid-session (if you have mesh Wi-Fi)
- Away from home (friend's house, hotel), if you ever do that
- Peak time (around 8–10 pm, when everyone at home is online)

## Recording a session

**Official app:** open the test bench page on your phone, choose "Official app", press **Start**, and tap **+ Freeze** each time the picture stops for about a quarter of a second or more. Tap **+ Drop** for a "reconnecting" message or a disconnect. Press **Stop**, rate how it felt (1–5), and save.

**XStream:** after the session, run `xstream report` for a summary. Upload the `.csv` from `~/.xstream/logs/` on the test bench page, which fills in the numbers for you, then add how it felt and save.

## Measuring input lag (the slow-motion test)

Counting freezes doesn't capture input lag, so test it separately:

1. Put the TV/Ally screen and the controller in the same camera shot. Use your phone's slow-motion video (240 fps if it has it).
2. In a game menu, press a button that changes the screen instantly (e.g. move the cursor).
3. Do it 10 times. In the video, count frames from the thumb hitting the button to the screen changing. At 240 fps each frame is about 4.2 ms.
4. Repeat with the official app, in the same spot and on the same game screen.

Also compare XStream with AMD Fluid Motion Frames on and off (AMD Software / Armoury Crate). Expect it to look smoother and feel slightly slower.

Before that, run `xstream video-test` on the Ally: it should say the decoder is `d3d11va (hardware)` and show network->screen of a few milliseconds. Then run `xstream pad`: it should show about 1000 Hz and change->send under 1 ms. If the read rate is far lower, check Armoury Crate is in Gamepad mode.

## Reading the results

- **Freezes per hour** is the main number. A difference under about 30% after only 3 pairs is likely noise.
- **Drops per hour**: any reduction here is a big deal in practice.
- **Feel**: your 1–5 rating catches things the counters miss.
- Check two lines in the XStream summary: **"Console follows bitrate caps"** and **"NACK"** (packet resend). If either says False/Ignored, XStream is running with one hand tied, and the result tells us what to fix next rather than whether the idea works.

Caveat: your freeze counts for the official app come from watching; XStream's come from the log. The page shows both side by side, but treat small differences with care. XStream's log may count very short stalls you wouldn't notice.
