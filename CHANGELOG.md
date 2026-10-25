# Changelog

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
