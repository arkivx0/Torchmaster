# Changelog

## 0.1.6 (unreleased, Android versionCode 8)
- **Light shows.** An Effects menu for all bulbs, each room and each bulb. Twelve built-in shows run inside the
  bulb (Candlelight, Rainbow, Fireworks, Volcanic, Aurora, Happy Holidays, Red White & Blue, Vegas, Party Time,
  Power Up, Splash, Cyber), on full-color bulbs. Three **app-side** effects (Color cycle, Candle flicker, Breathe)
  are sent step by step by Lanternly while it stays open, at most 4 commands a second. A **Now playing / Stop** bar
  shows whatever is running. Windows also has Effects in the tray menu.
- **Sunrise and sunset schedules** with an offset of up to ±180 minutes. Enter latitude/longitude in settings
  (optional); sun times are computed on the device, with no location permission. New schedule actions: start or
  stop a built-in effect, and **fade in** (wake-up) or **fade out** over 5–60 minutes. Time/day schedules are
  unchanged, and older schedule files still load.
- **Windows now matches Android** for all of the above.
- Key files where two bulbs share an address now import, with a warning (Reset still refuses those bulbs).
  Stored keys are checked the same way on load. Bulbs with no known address show "address unknown", and their own
  controls are turned off.
- Reset bulb: the confirmation scan no longer blocks other controls on Android. Keep reconnects. A bulb with no
  Bluetooth address on file is explained instead of scanned for. You choose which of its schedules to delete.
- Design review fixes: named 48dp color swatches with a ✓ on the current one, 48dp touch targets, a high-contrast
  "Backup needed" banner with Export now, screen-reader labels for switches, sliders and swatches, announced status
  changes, keyboard focus rings on Windows, higher-contrast muted text, and theme colors in one shared file
  (`spec/theme_tokens.json`).

## 0.1.5 (unreleased, Android versionCode 7)
Reset bulb fixes from the 0.1.4 review:
- **A reset bulb's own schedules are deleted** (schedules whose target is that bulb alone, in that home), with their
  alarms (Android) and last-run records (Windows). Before, they stayed behind as "(removed bulb)" and would have
  controlled a later bulb that got the same mesh address. Room and "All bulbs" schedules are kept.
- **Reset is refused** when the bulb's mesh address isn't a single bulb's address (it must be 1 to 0x7FFF; 0 means
  "unknown", 0x8000 and up are groups, and 0xFFFF would reach every bulb), or when another bulb in the home has the
  same address (both would be erased). Nothing is sent and a clear message explains why.
- **Key-file import checks addresses:** a file with a bulb or room address above 0x7FFF, or two bulbs of one home
  on the same address, is rejected ("Could not import: …"). 0 (address not learned yet) is still accepted.
- **The reset is confirmed before the bulb is removed.** The bulb doesn't acknowledge the command, so after sending it
  Lanternly listens for about 10 s for that bulb's factory-reset advertisement (`telink_mesh1`). If it's seen, the bulb
  and its own schedules are removed. If not, it asks: **Keep** (nothing removed; try again closer) or **Remove anyway**.
- Android: the bulb list is changed only on the UI thread after a reset (before, it was changed on the Bluetooth
  thread while the screen could be drawing it).

## 0.1.4 (unreleased, Android versionCode 6)
- **Reset bulb…** (per bulb, under ⋯): factory resets a bulb whose mesh key Lanternly has, over Bluetooth, for bulbs
  that won't reset by power-cycling. Sends the Telink kick-out command (`E3 11 02 01`, the same bytes as the official
  app's kick-out). Asks twice before it does anything ("This erases the bulb's pairing; you'll need to add it again").
  When the command goes out, the bulb is removed from the home and its rooms, and Lanternly points to
  Add a reset bulb → Find reset bulbs. If it can't be sent, nothing is removed. Tested against the simulated bulb only.
- Import a key file that has the bulb's existing mesh name and password (e.g. one you exported yourself) to control or
  reset that bulb without power-cycling it.
- Fixes left over from the 0.1.3 review:
  - Android: Bluetooth permission is checked before the schedule foreground service starts. If it's missing, the run
    reports "Bluetooth permission missing" and sets a retry instead of failing to start.
  - Android: the run deadline now covers a run that borrows the app's open connection, too.
  - Android: an alarm whose foreground start fails no longer stops another schedule run that's in progress. The
    service stops only after the last run ends.
  - Android 13+: asks once for the notification permission, which the "Running a schedule" notification needs.
  - Both: saving schedules keeps entries that were skipped as invalid at load. They're written back unchanged and
    no longer deleted.
  - Windows: switching homes waits for a schedule run in progress and doesn't swap the connection in the middle of it.
    Reset bulb waits the same way.

## 0.1.3 (unreleased)
- Schedules: turn bulbs, a room or all bulbs on or off, or apply a scene (e.g. Red at full brightness), at a set
  time on chosen days. Runs on the device only, over Bluetooth, with no network.
  - Android: exact alarms when allowed (falls back to inexact), re-planned after reboot and clock or time zone
    changes. The phone must be near the bulbs with Bluetooth on. A run more than 10 minutes late is skipped.
  - Windows: runs while Lanternly is open or in the tray and the PC is awake. A run more than 5 minutes late is skipped.
- Fixes from review: leaving the app no longer drops other pending UI updates; closing no longer blocks the UI
  while stops are sent; the automatic blink stop reports a failed stop; Forget, Import and switching homes stop
  any blink first; Windows Bluetooth writes time out instead of blocking later commands.
- New version number (0.1.3, Android versionCode 4) so this build can be told apart from 0.1.2.
- Tests for schedule times, days of the week and daylight-saving changes.
- Schedule fixes from the second 0.1.3 review (still 0.1.3; Android versionCode 5 so it installs over the reviewed build):
  - Android: the Bluetooth work of a run moved out of the alarm receiver into a short foreground service
    (type connectedDevice, small "Running a schedule" notification) with an overall 40 s deadline that is passed
    into connect, service discovery and every GATT wait; a hard 45 s timeout backs it up. "Last run" says
    "running…" first, so a run that gets killed is visible.
  - Android: a run that reaches no bulb sets up to two retries (about +2 and +5 minutes), always inside the
    10-minute window; retries never replace the next regular alarm.
  - Android: when the app is open and connected, a run uses that connection instead of opening a second one.
  - Android 12 with exact alarms denied: the warning now says plainly that idle-time delays can make runs skip.
  - Windows: after the PC wakes from sleep Lanternly reconnects and checks schedules; a run that reaches no bulb
    reconnects and retries once.
  - Windows: schedules for a home that is not selected now run, over a temporary connection to that home, and
    the row says so. The Schedules page keeps a "Last run" line.
  - Both: setting the clock back no longer repeats a run that already happened. Saved schedules are checked on
    load (hour 0–23, minute 0–59, days, action, target); bad entries are skipped and each one is planned on its
    own, so one bad entry can't stop the others or crash the app at launch. An unreadable schedules.json is
    moved aside to schedules.json.corrupt-<time> instead of being overwritten.
  - Both: the missed-run window is 10 minutes on both platforms (Windows was 5) and the Schedules page shows it.
    HomeId is a 64-bit integer in schedules.json on both platforms. Android alarm request codes are small dense
    numbers per schedule. "Last run" is saved synchronously.
  - Tests for entry validation, retry timing, clock-set-back dedupe and quarantine of an unreadable file.

## 0.1.2 (unreleased)
- "Find this bulb": a per-bulb button makes the bulb blink (BLE identify command); it stops after a few seconds
  or on a second tap.
- New "Red" quick scene: power on, full brightness, red (bulbs without color just turn on at full brightness).
- Find button fixes (review): blink state is kept per window/activity, so rebuilding the list no longer turns the
  next tap into a second start; rotating, leaving or quitting the app mid-blink sends the stop first (Android no
  longer crashes); a failed start resets the button; the button shows when a bulb is blinking, with status text and
  an accessible name; find no longer marks a bulb with unknown state as on.
- Windows: BLE command writes are serialized, so a stop can't overtake its start.
- Android: bulb commands run on their own queue, so scanning or adding bulbs no longer delays them; buttons are 48 dp.
- Privacy notes: how Lanternly differs from the vendor app, and why it is Bluetooth-only.
- More offline regression tests (both platforms): vendor cloud names, vendor LAN markers, networking APIs and
  the INTERNET permission fail the build. New test pins the identify command bytes.

## 0.1.1 (unreleased)
**Offline only.** All cloud code is removed: no Cync sign-in, no one-time cloud export, no cloud fallback, and no
code that contacts any GE host. The Windows LAN bridge is removed too: it needed GE host names redirected to the
PC plus per-device IDs and codes that only the cloud provides, so it cannot work offline. Mesh keys now come only
from "Add a reset bulb" (a key Lanternly generates) or from a key file.
- New offline tests (both platforms) fail if a GE cloud host name, a networking API or the Android INTERNET
  permission appears in the source. The Windows core assembly is checked to reference no `System.Net.*` assembly.
- Android: the INTERNET permission is gone; R8 minification is on (APK 2.2 MB to 0.1 MB); lint now fails the build on errors.
- Onboarding and docs: bulbs already paired with the Cync app need a factory reset once. The reset loses the current
  color; set it again right after adding. Reset guide has a caution box about accidental resets.
- Key files: optional passphrase protection (PBKDF2-SHA256 600k + AES-256-GCM, same format on both apps,
  cross-checked against an independent Python vector). After adding bulbs, the app prompts for a backup and shows a
  reminder until one is exported.
- Vault safety: an unreadable vault is quarantined as `vault.bin.corrupt-<time>`, the user is told, and saving is
  blocked until Import or Forget. Android writes the vault with AtomicFile.
- Errors are no longer swallowed: rooms, scenes and All on/off report "N/M bulbs updated" with the first error.
  Windows `crash.log` records exception type and stack only (no messages).
- Android: the BLE state callback is set before login. BLE packet sequence skips 0 on wrap (both apps).
- Color temperature 0 is treated as invalid and clamped to 1 (as in pycync); the Night scene uses 1.
- `docs/REAL-BULB-TEST.md` is now generic. The source zip excludes logs, `.kotlin/`, keys and local files.

## 0.1.0 (unreleased)
- First version: Android and Windows. BLE mesh control, Windows LAN bridge (experimental), optional cloud fallback (off by default).
- Rooms and bulbs with on/off, brightness, color temperature and RGB; quick scenes; connection badge per bulb.
- One-time cloud export, key file import/export, "Add a reset bulb" (experimental), reset guide.
- Windows tray icon with All on / All off / scenes.
