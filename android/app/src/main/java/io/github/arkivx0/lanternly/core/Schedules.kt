package io.github.arkivx0.lanternly.core

import org.json.JSONArray
import org.json.JSONObject
import java.time.DayOfWeek
import java.time.LocalTime
import java.time.ZoneId
import java.time.ZonedDateTime

/**
 * A local schedule: at hour:minute (local time) on the chosen days, turn the target on or off or apply a scene.
 * Runs on this phone only (no cloud). days: bit 0 = Monday … bit 6 = Sunday. target: "all", "room:<id>" or
 * "bulb:<meshId>". action: "on", "off" or a scene name (e.g. "Red"). Same JSON shape as the Windows app.
 */
data class Schedule(val id: Long, val enabled: Boolean, val hour: Int, val minute: Int, val days: Int, val action: String, val homeId: Long, val target: String)

object Schedules {
    const val EVERY_DAY = 0x7f
    /** A run that would start more than this late (phone off, alarm delayed) is skipped, not run late. */
    const val GRACE_MS = 10 * 60_000L
    val DAY_NAMES = listOf("Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun")
    val ACTIONS: List<String> = listOf("on", "off") + Scenes.all.map { it.name }.filter { it != "Off" }

    fun dayBit(d: DayOfWeek) = 1 shl (d.value - 1)

    /**
     * Next run strictly after [now]. DST: a time skipped by spring-forward runs shifted by the gap (02:30 → 03:30);
     * a time repeated by fall-back runs once, at its first occurrence (ZonedDateTime.of rules). Null if no day is set.
     */
    fun nextFire(s: Schedule, now: ZonedDateTime, zone: ZoneId = now.zone): ZonedDateTime? {
        if (s.days and EVERY_DAY == 0) return null
        val today = now.withZoneSameInstant(zone).toLocalDate()
        for (i in 0..8L) {
            val day = today.plusDays(i)
            if (s.days and dayBit(day.dayOfWeek) == 0) continue
            val t = ZonedDateTime.of(day, LocalTime.of(s.hour, s.minute), zone)
            if (t.isAfter(now)) return t
        }
        return null
    }

    fun onTime(dueMs: Long, nowMs: Long, graceMs: Long = GRACE_MS) = nowMs >= dueMs && nowMs - dueMs <= graceMs

    fun targets(h: Home, s: Schedule): List<Bulb> = when {
        s.target == "all" -> h.bulbs.toList()
        s.target.startsWith("room:") -> h.rooms.firstOrNull { "room:${it.id}" == s.target }?.let { r -> h.bulbs.filter { it.meshId in r.meshIds } } ?: emptyList()
        s.target.startsWith("bulb:") -> h.bulbs.filter { "bulb:${it.meshId}" == s.target }
        else -> emptyList()
    }

    fun targetName(h: Home?, s: Schedule) = when {
        s.target == "all" -> "All bulbs"
        s.target.startsWith("room:") -> h?.rooms?.firstOrNull { "room:${it.id}" == s.target }?.name ?: "(removed room)"
        s.target.startsWith("bulb:") -> h?.bulbs?.firstOrNull { "bulb:${it.meshId}" == s.target }?.name ?: "(removed bulb)"
        else -> "?"
    }

    fun daysText(d: Int) = when (d and EVERY_DAY) {
        EVERY_DAY -> "Every day"; 0x1f -> "Weekdays"; 0x60 -> "Weekends"; 0 -> "No days"
        else -> DAY_NAMES.filterIndexed { i, _ -> d and (1 shl i) != 0 }.joinToString(" ")
    }
    fun actionText(a: String) = when (a) { "on" -> "Turn on"; "off" -> "Turn off"; else -> "Scene: $a" }
    fun describe(h: Home?, s: Schedule) = "%02d:%02d · %s · %s · %s".format(s.hour, s.minute, daysText(s.days), actionText(s.action), targetName(h, s))

    /** Runs the action on the targets over [r]. Blocking. */
    fun run(r: Router, h: Home, s: Schedule): BatchResult {
        val bulbs = targets(h, s)
        if (s.action == "on" || s.action == "off") {
            var ok = 0; var first: String? = null
            for (b in bulbs) try { r.send(h, b, Cmd.Power(s.action == "on")); ok++ } catch (e: Exception) { if (first == null) first = "${b.name}: ${e.message}"; Diag.log("schedule", e) }
            return BatchResult(ok, bulbs.size, first)
        }
        val scene = Scenes.all.firstOrNull { it.name == s.action } ?: throw IllegalArgumentException("unknown scene ${s.action}")
        return Scenes.apply(r, h, scene, bulbs)
    }

    fun toJson(l: List<Schedule>): String = JSONObject().put("Format", 1).put("Schedules", JSONArray(l.map {
        JSONObject().put("Id", it.id).put("Enabled", it.enabled).put("Hour", it.hour).put("Minute", it.minute).put("Days", it.days)
            .put("Action", it.action).put("HomeId", it.homeId).put("Target", it.target)
    })).toString(1)

    fun fromJson(s: String): List<Schedule> {
        val a = JSONObject(s).optJSONArray("Schedules") ?: return emptyList()
        return (0 until a.length()).map { i -> val o = a.getJSONObject(i)
            Schedule(o.optLong("Id"), o.optBoolean("Enabled", true), o.optInt("Hour"), o.optInt("Minute"), o.optInt("Days", EVERY_DAY),
                o.optString("Action", "on"), o.optLong("HomeId"), o.optString("Target", "all"))
        }
    }
}
