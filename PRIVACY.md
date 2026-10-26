# Privacy

Lanternly has no analytics, telemetry, ads, accounts or crash upload.

- **Network: none.** Lanternly never connects to GE, Savant, Cync or any other server. The Android app does not
  request the INTERNET permission. The only radio it uses is Bluetooth, to talk to your bulbs.
- The official vendor app signs in to an account, stores the account password on the phone and keeps a cloud
  connection open. Lanternly has no account and uses no network.
- Why Bluetooth only, not local Wi-Fi: the vendor's local Wi-Fi control still needs device keys issued by its cloud.
- Stored data (homes, rooms, bulbs, mesh keys) is encrypted: Android Keystore (AES-GCM, AtomicFile) / Windows DPAPI
  (current user). The Android vault is excluded from backups. If the stored data cannot be read it is moved aside
  (`vault.bin.corrupt-<time>`) and nothing is saved over it until you import a key file or choose Forget everything.
- Logs: Windows writes `%LOCALAPPDATA%\Lanternly\crash.log` with time, place, exception type and stack trace only
  (no exception messages, no keys). Android logs exception type names to logcat.
- Key files contain your mesh password. Export them with a passphrase (PBKDF2-SHA256 + AES-256-GCM) or keep plain
  files private. Settings from 0.1.0 such as a stored cloud token are discarded on upgrade.
