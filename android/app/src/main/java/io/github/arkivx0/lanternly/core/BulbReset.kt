package io.github.arkivx0.lanternly.core

/**
 * "Reset bulb": sends the Telink kick-out (0xE3, data 01) so a bulb whose mesh credentials we know erases its pairing
 * and returns to factory defaults (telink_mesh1 / 123), then forgets it locally so it can be added again.
 * Mirrors windows/Lanternly.Core/BulbReset.cs.
 */
object BulbReset {
    const val CONFIRM_TITLE = "Reset bulb?"
    fun confirmText(b: Bulb) = "Reset \"${b.name}\" to factory settings?\n\nThis erases the bulb's pairing; you'll need to add it again " +
        "(⚙ → Add a reset bulb → Find reset bulbs). It will stop answering to this home until then, and it loses its color and brightness.\n\n" +
        "Keep the bulb powered on and close to this device."
    const val DONE = "Reset sent. The bulb should blink and restart within a few seconds. It was removed from this home: open ⚙ → Add a reset bulb → Find reset bulbs to add it again."

    /** Blocking. Throws (and removes nothing) when the first copy could not be sent; a failed second copy is expected when the bulb was the connection. */
    fun reset(r: Router, h: Home, b: Bulb) {
        r.send(h, b, Cmd.FactoryReset)
        try { Thread.sleep(300); r.send(h, b, Cmd.FactoryReset) } catch (e: InterruptedException) { throw e } catch (e: Exception) { Diag.log("reset repeat (link already gone is expected)", e) }
        forget(h, b)
    }

    /** Removes the bulb from the home and from every room. */
    fun forget(h: Home, b: Bulb) {
        h.bulbs.remove(b)
        if (h.bulbs.any { it.meshId == b.meshId }) return
        h.rooms = h.rooms.map { Room(it.id, it.name, it.meshIds.filter { m -> m != b.meshId }) }
    }
}
