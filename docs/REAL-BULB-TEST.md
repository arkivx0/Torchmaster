# Real-bulb test checklist (0.1.3)

Use one test bulb you don't mind resetting. Report for each step: works / fails, plus the exact status-line text.
Windows crash details: `%LOCALAPPDATA%\Lanternly\crash.log` (types and stack traces only).

## A. No-risk checks (no bulb changes)
1. Windows: run `Lanternly.exe --demo`. Check the theme, room/bulb rows, scenes and the tray menu (All on/off, Scenes, Quit).
2. Android: install the APK and open it. You see "No bulbs yet". Grant Bluetooth permission when asked.
3. Android: Settings → Apps → Lanternly → Permissions shows no network permission. Optional: watch a firewall log
   on Windows; Lanternly should make no connections at all.

## B. Add a reset bulb (loses that bulb's current color)
1. Reset the test bulb (docs/RESET.md). It blinks.
2. Settings → Add a reset bulb → Find reset bulbs, within a few meters. Expect "Added 1 of 1 bulb(s)" and a backup prompt.
3. Choose to back up: export with a passphrase. The status line stops showing the backup reminder.
4. Control it: on/off, brightness, color temperature, a color swatch, a room switch and slider, each scene.
   Set the color you want to keep.
5. Red scene: first apply "Night" (dim), then "Red". Expect the bulb on, at full brightness, red. A bulb without
   color support just turns on at full brightness.
6. Find this bulb (◎), on both apps:
   1. Tap once: the bulb blinks, the button is highlighted and the status says "Blinking <name>…". After about
      5 s it stops by itself and the button returns to normal.
   2. Tap, then tap again within 5 s: it stops right away ("Stopped blinking <name>").
   3. Tap, then open and close settings (Android) or switch homes, then tap again: it stops (no second start).
   4. Android: tap, then rotate the phone; and separately tap, then press Home or close the app (✕). The bulb stops
      blinking, the app does not crash.
   5. Windows: tap, then quit from the tray (Quit). The bulb stops blinking before the app exits.
   6. With the bulb out of range (or Bluetooth off), tap: the status says "<name>: not reachable" and the next tap
      tries a new start (the button is not left highlighted).
   7. Unknown so far: whether the bulb stops on its own if a stop never arrives. Tap, then immediately turn
      Bluetooth off on the device; note whether and when the bulb stops blinking.
   8. After find, a bulb whose state was unknown is not shown as on.
7. Turn Bluetooth off in settings: the bulb shows `OUT OF RANGE`, commands report "0/1 bulbs updated", nothing crashes.

## C. Key file on the other app
1. On the other device: Import key file → choose the exported file → enter the passphrase. The bulb appears.
2. Control it from there over Bluetooth (one device at a time works best).
3. Wrong passphrase: import shows "Wrong passphrase, or the file was changed." and nothing changes.

## D. Recovery
1. Forget everything, then Import key file: the bulb is controllable again without another reset.
2. Tap ◎ (find), then Forget everything or Import right away: the bulb stops blinking and no stray
   "not reachable" message appears. Same when switching homes mid-blink.

## E. Schedules (0.1.3)
Use times 2–3 minutes ahead. Report the status line (Windows) or the "Last run" line on the Schedules page (Android).
1. Open ⏰ Schedules → Add schedule: today, 2 minutes ahead, "Turn on", target the test bulb. Turn the bulb off
   first. At that time it turns on. Repeat with "Turn off".
2. Add one with "Scene: Red" on All bulbs: at that time the bulb is on, full brightness, red.
3. Add one for a day that is not today: nothing happens today. Turn a due schedule's switch off: it does not run.
   Delete one: it disappears and does not run.
4. Android:
   1. Close the app (✕) and lock the phone; keep it near the bulb with Bluetooth on. The schedule still runs.
   2. Reboot the phone (don't open Lanternly), wait for a schedule: it still runs.
   3. Change the phone's time zone or clock, then back: schedules still run at the right local time.
   4. Settings → Apps → Special app access → Alarms & reminders: if Lanternly shows there (Android 12), turn it off.
      The Schedules page shows the exact-alarm warning and "Allow exact alarms" opens the setting. Runs may be late.
   5. Turn Bluetooth off before a run: "Last run" says it was not reachable; the next run still happens.
5. Windows:
   1. Close the window (it stays in the tray): the schedule still runs; the tray tooltip shows the result.
   2. Put the PC to sleep across a schedule and wake it more than 5 minutes later: the missed run does not happen.
   3. Quit from the tray: no schedule runs until Lanternly is started again.
