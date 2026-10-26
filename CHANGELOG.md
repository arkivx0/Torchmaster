# Changelog

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
