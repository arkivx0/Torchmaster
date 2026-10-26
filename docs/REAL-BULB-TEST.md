# Real-bulb test checklist (0.1.2)

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
