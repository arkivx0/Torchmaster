# Factory reset

> **⚠ Caution: read before you reset**
> - A reset **loses the bulb's current color and brightness.** Set them again in Lanternly right after adding the bulb.
> - Switching a bulb off and on quickly several times can **reset it by accident.** Avoid rapid toggling at other times.
> - Use a plain on/off switch or a switched plug/power strip, not a dimmer or 3-way lamp.
> - After a reset, the bulb only answers to the key Lanternly gives it. **Export a key file backup** afterwards.

Bulbs already paired with the Cync app need a reset **once** before Lanternly can add them: their mesh key belongs
to the Cync account, and Lanternly never contacts the cloud to fetch it. Bulbs you add from a Lanternly key file
don't need a reset. A reset worked when the bulb blinks (usually 3 times).

Source: GE Cync support article "Resetting Cync Light Bulbs" (Bluetooth + Direct Connect bulbs).

## Reset from Lanternly (no power-cycling)
If Lanternly already has the bulb's mesh key (you added it in Lanternly, or imported a key file with that
home's mesh name and password), it can reset the bulb over Bluetooth: ⋯ next to the bulb → **Reset bulb…** → confirm
twice. The bulb is removed from the home. Then use **Add a reset bulb → Find reset bulbs** to add it fresh.
This sends the Telink kick-out command (see spec/PROTOCOL.md). Bulbs still on a Cync account whose key Lanternly
doesn't have can only be reset by power-cycling.

## Current firmware (try first)
1. Start with the bulb OFF.
2. Turn it ON 1 s, then OFF 1 s. Do this 5 times.
3. The bulb flashes when it has reset.

## Older bulbs, method A
1. OFF for 5 s.
2. ON 8 s, OFF 2 s. Repeat 5 times.
3. Turn ON. It flashes 3 times.

## Older bulbs, method B (firmware 2.7 or earlier, brown box / before 2019)
1. OFF for 5 s, then ON for 8 s.
2. OFF/ON at 2 s intervals, 3 times.
3. OFF 2 s, ON 8 s. Do this twice.
4. OFF 2 s, then ON. It flashes 3 times.

If one method fails, try another, and try a different fixture or a switched power strip.

## After a reset
A reset bulb advertises over Bluetooth as `telink_mesh1` (Telink factory default, password `123`).
Lanternly's **Add a reset bulb** finds it and writes a private mesh name, password and long-term key that it
generates on your device (experimental). The new key lives only in your Lanternly vault and key-file backups.
Then set the color and brightness you want.

A reset bulb is **not** on Wi-Fi and Lanternly does not put it on Wi-Fi; it is controlled over Bluetooth only.
