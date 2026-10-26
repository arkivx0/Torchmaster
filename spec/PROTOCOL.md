# Lanternly protocol notes

Shared by `windows/Lanternly.Core` (C#) and `android/.../core` (Kotlin). Lanternly is offline-only, so the only
protocol is Bluetooth LE (Telink mesh). Both implementations are tested against `test_vectors.json`, produced by
running acync/cync2mqtt `mesh.py` (Telink crypto). The factory pairing write is also checked against the
constant published by the cync-lan project.

## BLE (Telink mesh)
Service `00010203-0405-0607-0809-0a0b0c0d1910`; notify `...1911`, command `...1912`, pair `...1914`.
AES-128-ECB with key and data byte-reversed ("Telink AES").
- Login: write `0C r8 AES(pad16(r8), name^password)[0:8]` to pair; read `0D d8 ...`;
  session key = `AES(name^password, r8 | d8)`.
- Command: 20 bytes `seq(3 LE) mic(2) target(u16 LE) cmd vendor(11 02) data[10]`; MIC/keystream with nonces
  built from the MAC (LE) and seq (see `Ble.EncryptPacket`). Same command codes: `d0`, `d2`, `e2 05 ct`, `e2 04 r g b`.
- Identify (blink to find a bulb): cmd `f7`, data `03 01` = start, `03 02` = stop. Plaintext bytes 7..11 on the
  wire: `f7 11 02 03 01` / `f7 11 02 03 02` (same as the official app). No state change.
- Factory reset ("Reset bulb", Telink kick-out): cmd `e3`, data `01` (= return to factory defaults). Plaintext bytes
  7..10 on the wire: `e3 11 02 01`, addressed to the bulb's mesh id. Same bytes as the official app
  (`KickOutDeviceCommand`, body `{-29, 17, 2, 1}`; Telink opcode table: `0xE3` "kick out / reset factory"). Not acknowledged:
  the app sends it, sends a second copy 300 ms later (a failed second copy is expected when the reset bulb was the
  connected node), then forgets the bulb. Afterwards the bulb advertises as `telink_mesh1` / `123` and can be provisioned
  again. Validated against a simulated node; confirm on a real bulb (docs/REAL-BULB-TEST.md §F).
- Status notifications: cmd `dc`, two 4-byte records `[mesh, online, bri(+128 = rgb mode), ct or rgb332]`.
- Provision a factory-reset node (`telink_mesh1` / `123`): log in, then write to pair
  `04 AES(sk, pad16(new_name))`, `05 AES(sk, pad16(new_password))`, `06 AES(sk, ltk)`; read back `07` = confirmed.
  Experimental; validated only against a simulated node.
- Packet sequence: 24-bit, starts at a random value, never 0 (skipped on wrap).
- Color temperature `1..100` (0 is invalid and is clamped to 1).

## Key file
Plain: the vault JSON (`{"Format":1,"Homes":[...]}`; homes carry `MeshName`, `MeshPassword`, rooms and bulbs).
Protected: `{"format":"lanternly-keyfile","version":1,"kdf":"pbkdf2-sha256","iterations":600000,"salt":b64(16),
"nonce":b64(12),"ciphertext":b64(ct || tag16)}`; key = PBKDF2-HMAC-SHA256(passphrase, salt, iterations, 32 bytes);
AES-256-GCM with AAD `lanternly-keyfile`. Readers accept 100k..10M iterations. Vector: `keyfile_vector.json`.



## Light shows (light run mode), 0.1.6

Same vendor command envelope as color (opcode `0xE2`, vendor `0x0211`), data `07 <mode> <show> <rnd>`:

| mode | meaning |
|---|---|
| 0 | static (stops a show; show byte 0) |
| 1 | light show |
| 2 | music show (needs the phone microphone in the Cync app; not used) |
| 3 | reveal |
| 4 | multi-color |

On the wire a show is `E2 11 02 07 01 <id> <rnd>`; `<rnd>` is one random byte, which the official app sends with
every run-mode command. Light show ids (official app `LightRunMode.LightShow`): 1 Candlelight, 2 Rainbow,
3 Fireworks, 4 Volcanic, 5 Aurora, 6 Happy Holidays, 7 Red White & Blue, 8 Vegas, 9 Party Time, 65 Power Up,
66 Splash, 67 Cyber; 10–32 are user-made custom shows. The status byte reports 224/225/227 for Power Up/Splash/Cyber.
cync-lan independently sends `E2 … 11 02 07 01 <id> <byte>` with the same ids, except Fireworks (it uses 0x3A;
Lanternly follows the app's 3). The app offers shows only on full-color bulbs (Capability LightShow).

Not native (done by Lanternly with timed commands): color cycle, candle flicker, breathe, and fades over minutes.

## Sun times

Sunrise/sunset use the NOAA solar calculator equations (Meeus), zenith 90.833°, computed for the local date and
converted with the device time zone (so DST is handled by the zone rules). Tested against astral 3 for several
latitudes (within 2 min), polar day/night and both 2026 US DST changes.
