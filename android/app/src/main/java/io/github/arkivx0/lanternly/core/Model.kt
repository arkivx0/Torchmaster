package io.github.arkivx0.lanternly.core

import org.json.JSONArray
import org.json.JSONObject

enum class Via { NONE, BLE }

object Caps { const val ON_OFF = 1; const val DIM = 2; const val TEMP = 4; const val COLOR = 8; const val HUB = 16
    private val map: Map<Int, Int> by lazy {
        val s = Caps::class.java.classLoader?.getResourceAsStream("device_caps.tsv") ?: return@lazy emptyMap()
        s.bufferedReader().readLines().filter { it.isNotBlank() }.associate { l -> val p = l.split('\t'); p[0].toInt() to parse(p.getOrElse(1) { "" }) }
    }
    fun parse(f: String) = ON_OFF or (if ('D' in f) DIM else 0) or (if ('T' in f) TEMP else 0) or (if ('C' in f) COLOR else 0) or (if ('H' in f) HUB else 0)
    /** Unknown types: on/off + dimming only. */
    const val ALL_CONTROLS = -1
    fun forType(t: Int) = if (t == ALL_CONTROLS) (ON_OFF or DIM or TEMP or COLOR) else map[t] ?: (ON_OFF or DIM)
    val count get() = map.size
}

class Bulb(var name: String, var meshId: Int, var type: Int, var mac: String) {
    val caps get() = Caps.forType(type)
    fun has(c: Int) = caps and c != 0
    /** False for 0 (not learned yet: commands would reach whichever bulb is connected) and group/broadcast addresses. */
    val addressKnown get() = meshId in 1..0x7FFF
    @Volatile var state: BulbState? = null
    @Volatile var via: Via = Via.NONE
}
class Room(val id: Int, val name: String, val meshIds: List<Int>)
class Home(val id: Long, val name: String, val meshName: String, val meshPassword: String, var rooms: List<Room>, val bulbs: MutableList<Bulb>)

/** Last reported state. mode: color temperature 1..100, or 0xFE when showing RGB. */
data class BulbState(val meshId: Int, val online: Boolean, val on: Boolean, val brightness: Int, val mode: Int, val rgb: Triple<Int, Int, Int>)

/** Same JSON shape as the Windows app (key files are interchangeable). */
/** Unknown fields from older files (e.g. a 0.1.0 cloud token) are ignored and dropped on the next save. */
class Vault(var homes: List<Home> = emptyList()) {
    fun toJson(): String {
        val o = JSONObject().put("Format", 1)
        o.put("Homes", JSONArray(homes.map { h ->
            JSONObject().put("Id", h.id).put("Name", h.name).put("MeshName", h.meshName).put("MeshPassword", h.meshPassword)
                .put("Rooms", JSONArray(h.rooms.map { r -> JSONObject().put("Id", r.id).put("Name", r.name).put("MeshIds", JSONArray(r.meshIds)) }))
                .put("Bulbs", JSONArray(h.bulbs.map { b -> JSONObject().put("Name", b.name).put("MeshId", b.meshId).put("Type", b.type).put("Mac", b.mac) }))
        }))
        return o.toString(1)
    }
    companion object {
        fun fromJson(s: String): Vault {
            val o = JSONObject(s)
            val homes = o.optJSONArray("Homes")?.let { a -> (0 until a.length()).map { i -> val h = a.getJSONObject(i)
                Home(h.optLong("Id"), h.optString("Name", "Home"), h.optString("MeshName"), h.optString("MeshPassword"),
                    h.optJSONArray("Rooms")?.let { r -> (0 until r.length()).map { j -> val x = r.getJSONObject(j)
                        Room(x.optInt("Id"), x.optString("Name", "Room"), x.optJSONArray("MeshIds")?.let { m -> (0 until m.length()).map { m.getInt(it) } } ?: emptyList()) } } ?: emptyList(),
                    h.optJSONArray("Bulbs")?.let { r -> (0 until r.length()).map { j -> val x = r.getJSONObject(j)
                        Bulb(x.optString("Name", "Bulb"), x.optInt("MeshId"), x.optInt("Type"), x.optString("Mac")) }.toMutableList() } ?: mutableListOf())
            } } ?: emptyList()
            return Vault(homes)
        }
    }
}

/** Factory reset steps (GE support article). Shown in the app; needed once for bulbs paired with another app. */
object ResetText {
    const val INTRO = "Bulbs already paired with the Cync app must be factory reset once before Torchmaster can add them. A reset removes the bulb from its old account and mesh, and it also loses its current color and brightness; set them again in Torchmaster right after adding the bulb. " +
        "Careful: switching a bulb off and on quickly several times can reset it by accident. " +
        "Use a plain on/off switch or a switched plug/power strip (not a dimmer or 3-way lamp). A reset worked when the bulb blinks (usually 3 times)."
    val METHODS = listOf(
        "Current firmware (try first)" to "Start with the bulb OFF. Turn ON 1 s, OFF 1 s. Do this 5 times. The bulb flashes when reset.",
        "Older bulbs, method A" to "OFF 5 s. Then ON 8 s, OFF 2 s; repeat 5 times. Turn ON: it flashes 3 times.",
        "Older bulbs, method B (firmware 2.7 or earlier)" to "OFF 5 s. ON 8 s. Then OFF/ON at 2 s intervals 3 times. Then OFF 2 s, ON 8 s; twice. Then OFF 2 s and ON: it flashes 3 times.",
    )
}
