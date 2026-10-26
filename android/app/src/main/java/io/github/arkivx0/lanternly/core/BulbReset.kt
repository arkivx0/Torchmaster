package io.github.arkivx0.lanternly.core

/**
 * "Reset bulb": sends the Telink kick-out (0xE3, data 01) so a bulb whose mesh credentials we know erases its pairing
 * and returns to factory defaults (telink_mesh1 / 123). The bulb does not acknowledge, so the app then looks for its
 * factory-reset advertisement and only forgets it (with its single-bulb schedules) when seen or when the user says so.
 * Mirrors windows/Lanternly.Core/BulbReset.cs.
 */
object BulbReset {
    const val CONFIRM_TITLE = "Reset bulb?"
    /** Unicast node addresses. 0 is "the directly connected node"; 0x8000 and up are group / broadcast (0xFFFF = every bulb). */
    const val MAX_ADDRESS = 0x7FFF
    const val CONFIRM_SCAN_MS = 10_000L
    fun validAddress(a: Int) = a in 1..MAX_ADDRESS

    fun confirmText(b: Bulb) = "Reset \"${b.name}\" to factory settings?\n\nThis erases the bulb's pairing; you'll need to add it again " +
        "(⚙ → Add a reset bulb → Find reset bulbs). It will stop answering to this home until then, and it loses its color and brightness. " +
        "Schedules for this bulb alone are deleted.\n\n" +
        "Keep the bulb powered on and close to this device."
    const val DONE = "Reset confirmed: the bulb is back to factory settings and was removed from this home, with its own schedules. Open ⚙ → Add a reset bulb → Find reset bulbs to add it again."
    const val UNCONFIRMED_TITLE = "Couldn't confirm the reset"
    fun unconfirmedText(b: Bulb) = "The reset was sent, but \"${b.name}\" was not seen restarting as a reset bulb. It may be out of range and still paired.\n\n" +
        (if (!canConfirm(b)) noMacText(b) + "\n\n" else "") +
        "Keep: leave it in this home (you can try again closer; Lanternly reconnects).\nRemove anyway: forget it and its own schedules (if it is still paired, only a power-switch reset brings it back)."

    /** Why this bulb must not be reset from the app, or null when it can. */
    fun refuseReason(h: Home, b: Bulb): String? = when {
        !validAddress(b.meshId) -> "\"${b.name}\" has mesh address ${b.meshId} (0x%X), which is not a single bulb's address (1 to 0x7FFF). Not reset; use the power-switch reset instead.".format(b.meshId)
        h.bulbs.count { it.meshId == b.meshId } > 1 -> "\"${b.name}\" shares mesh address ${b.meshId} with another bulb in this home, so the reset would erase both. Not reset; use the power-switch reset instead."
        else -> null
    }

    /** Blocking. Sends the reset (two copies). Throws, removing nothing, when refused or when the first copy could not be sent. Does not forget the bulb. */
    fun send(r: Router, h: Home, b: Bulb) {
        refuseReason(h, b)?.let { throw IllegalArgumentException(it) }
        r.send(h, b, Cmd.FactoryReset)
        try { Thread.sleep(300); r.send(h, b, Cmd.FactoryReset) } catch (e: InterruptedException) { throw e } catch (e: Exception) { Diag.log("reset repeat (link already gone is expected)", e) }
    }

    /** True when [b] shows up among the factory-reset (telink_mesh1) advertisers [factoryMacs]. */
    fun confirmed(b: Bulb, factoryMacs: List<String>) = b.mac.isNotBlank() && factoryMacs.any { it.equals(b.mac, ignoreCase = true) }
    /** A bulb with no Bluetooth address on file (e.g. from some key files) can't be recognised in the scan. */
    fun canConfirm(b: Bulb) = b.mac.replace(":", "").replace("-", "").length == 12
    fun noMacText(b: Bulb) = "\"${b.name}\" has no Bluetooth address on file, so Lanternly can't recognise it when it restarts as a reset bulb; the reset can't be confirmed. " +
        "If it blinks or turns on at full white after the reset, choose Remove anyway."

    /** Schedules that control only this bulb: they must go with it, or a later bulb given the same address would inherit them. */
    fun schedulesFor(h: Home, b: Bulb, l: List<Schedule>) = l.filter { it.homeId == h.id && it.target == "bulb:${b.meshId}" }

    /** Removes the bulb from the home and from every room. Call on the UI thread. */
    fun forget(h: Home, b: Bulb) {
        h.bulbs.remove(b)
        if (h.bulbs.any { it.meshId == b.meshId }) return
        h.rooms = h.rooms.map { Room(it.id, it.name, it.meshIds.filter { m -> m != b.meshId }) }
    }

    /**
     * Key-file import check: every bulb/room address must be 0 (unknown) or 1..0x7FFF, else the file is rejected.
     * Bulbs sharing an address are imported, with a warning from [addressWarnings] (Reset refuses those bulbs).
     */
    fun checkImport(v: Vault) {
        for (h in v.homes) {
            h.bulbs.firstOrNull { it.meshId !in 0..MAX_ADDRESS }?.let { throw IllegalArgumentException("bulb \"${it.name}\" has invalid mesh address ${it.meshId}") }
            h.rooms.firstOrNull { r -> r.meshIds.any { it !in 0..MAX_ADDRESS } }?.let { throw IllegalArgumentException("room \"${it.name}\" has an invalid mesh address") }
        }
    }

    /** Problems worth showing after an import or when the stored vault is loaded. Nothing is changed. */
    fun addressWarnings(v: Vault): List<String> {
        val w = ArrayList<String>()
        for (h in v.homes) {
            h.bulbs.filter { it.meshId != 0 }.groupBy { it.meshId }.values.filter { it.size > 1 }.forEach {
                w += "${it.joinToString(" and ") { b -> "\"${b.name}\"" }} share mesh address ${it[0].meshId}: a command to one reaches both, and Reset is refused for them."
            }
            h.bulbs.filter { it.meshId !in 0..MAX_ADDRESS }.forEach { w += "\"${it.name}\" has invalid mesh address ${it.meshId}; its own controls are disabled." }
            h.rooms.filter { r -> r.meshIds.any { it !in 0..MAX_ADDRESS } }.forEach { w += "Room \"${it.name}\" lists an invalid mesh address." }
            val unknown = h.bulbs.count { it.meshId == 0 }
            if (unknown > 0) w += "$unknown bulb(s) have no known mesh address (shown as \"address unknown\"); their own controls are disabled until they are added again."
        }
        return w
    }
}
