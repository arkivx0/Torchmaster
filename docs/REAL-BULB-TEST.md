# Real-bulb test checklist (0.1.4, Android versionCode 6)

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
   5. Turn Bluetooth off before a run: "Last run" says "not reachable … retrying at <time>". Turn Bluetooth back on
      within 2 minutes: the retry runs and "Last run" shows the result. Leave it off: after the
      second retry it says "not reachable" with no further retry, and the next regular run still happens.
   6. During a run a small "Running a schedule" notification appears briefly, then goes away. "Last run" never stays
      on "running…" (if it does, note the Android version).
   7. With Lanternly open and connected (status "BLE ✓"), let a schedule run: "Last run" ends with "(app connection)"
      and the app stays connected.
   8. Walk out of Bluetooth range for a run (or switch the bulb off at the wall): the app does not freeze, no
      "isn't responding" dialog appears, and "Last run" reports a result within about a minute.
   9. Set the phone clock back across a run that already happened (turn off automatic time first): it does not run
      a second time. Turn automatic time back on afterwards.
5. Windows:
   1. Close the window (it stays in the tray): the schedule still runs; the tray tooltip shows the result.
   2. Put the PC to sleep across a schedule and wake it more than 10 minutes later: the missed run does not happen and
      "Last run" says it was skipped. Sleep the PC for a few minutes, wake it, then let a schedule run: it works
      (Lanternly reconnects after waking).
   3. With two homes, add a schedule while home A is selected, then select home B: the row says "not selected: runs
      over a temporary connection" and at that time home A's bulb still changes. The Schedules page shows "Last run".
   4. Set the PC clock back across a run that already happened: it does not run again.
   5. Quit from the tray: no schedule runs until Lanternly is started again.

## F. Reset bulb over Bluetooth (0.1.4)
Use a bulb whose mesh key Lanternly already has (added in Lanternly, or imported from a key file).
1. Make sure the bulb can be controlled first: on/off works and the status shows "BLE ✓".
2. ⋯ next to the bulb → **Reset bulb…** → read the warning → Continue → **Erase pairing** (Windows: OK, then Yes).
   Cancelling at either step must change nothing.
3. Expect "Reset sent. Checking…", then (0.1.5) a "Reset confirmed" dialog that points to Add a reset bulb. The bulb
   disappears from the list and from its room. Note what the bulb does (blinks, how many times, turns off or on).
4. Within about 10 s: ⚙ → Add a reset bulb → **Find reset bulbs**. Expect the bulb to be found and "Added 1 of 1 bulb(s)".
   If nothing is found, wait 30 s and try once more, then note it. Turning the bulb off and on once at the switch is
   fine at this point.
5. Control it again. Export a new key-file backup.
6. Negative check: with Bluetooth off (or the bulb out of range), Reset bulb… reports "reset not sent … Nothing was
   removed" and the bulb stays in the list.
7. Android 13+: on a fresh install, the app asks for notifications once. Deny it: schedules still run.

## H. Reset bulb fixes (0.1.5)
1. Give the bulb its own schedule (target: that bulb) plus one room schedule. Reset it as in F. Expect "Checking that
   … restarted (10 s)…", then the "Reset confirmed" dialog. The bulb's own schedule is gone from the list; the room
   schedule stays. Android: no stray alarm fires at the deleted schedule's time.
2. Re-add the bulb (it may get the same mesh address). Check that no old schedule controls it.
3. Unconfirmed path: start a reset with the bulb just at the edge of range (or switch it off at the wall right after
   tapping Erase pairing). Expect "Couldn't confirm the reset" with **Keep** / **Remove anyway** (Windows: Yes = Keep,
   No = Remove anyway). Keep must leave the bulb and its schedules; Remove anyway removes both.
4. Edit a key-file copy so one bulb has `"MeshId": 65535`, import it: expect "Could not import: … invalid mesh
   address". Restore the good key file afterwards.
5. Note how long the bulb took to show up as `telink_mesh1` after the reset (if it took more than 10 s, report it).

## G. 0.1.4 schedule fixes
1. Android 12+: revoke Nearby devices (Bluetooth) for Lanternly, then let a schedule fire: "Last run" says
   "Bluetooth permission missing…" and the app does not crash. Grant it again.
2. Windows, two homes: start a schedule run on home A, switch to home B straight away. The run finishes ("Last run"
   shows a result) and then home B connects.

