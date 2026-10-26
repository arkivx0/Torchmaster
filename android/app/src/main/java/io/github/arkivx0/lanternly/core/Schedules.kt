package io.github.arkivx0.lanternly.core

import org.json.JSONArray
import org.json.JSONObject
import java.time.DayOfWeek
import java.time.LocalTime
import java.time.ZoneId
import java.time.ZonedDateTime

/**
 * A local schedule: at hour:minute (local time), or at sunrise/sunset plus [offset] minutes, on the chosen days.
 * action: "on", "off", a scene name, "effect:<id>" (native light show, 0 = stop), "fadein:<min>" or "fadeout:<min>".
 * trigger: "time" (default; older files have none), "sunrise" or "sunset". hour/minute are ignored for sun triggers.
 * Runs on this phone only (no cloud). days: bit 0 = Monday … bit 6 = Sunday. target: "all", "room:<id>" or
 * "bulb:<meshId>". action: "on", "off" or a scene name (e.g. "Red"). Same JSON shape and field types as the Windows app
 * (Id and HomeId are 64-bit integers on both).
 */
data class Schedule(val id: Long, val enabled: Boolean, val hour: Int, val minute: Int, val days: Int, val action: String, val homeId: Long, val target: String,
                    val trigger: String = "time", val offset: Int = 0)

object Schedules {
    const val EVERY_DAY = 0x7f
    /** A run that would start more than this late (phone off, alarm delayed) is skipped, not run late. */
    const val GRACE_MS = 10 * 60_000L
    val DAY_NAMES = listOf("Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun")
    val ACTIONS: List<String> = listOf("on", "off") + Scenes.all.map { it.name }.filter { it != "Off" }
    val TRIGGERS = listOf("time", "sunrise", "sunset")
    const val MAX_OFFSET = 180
    val FADE_MINUTES = listOf(5, 10, 15, 20, 30, 45, 60)
    /** Every action the editor offers: on/off, scenes, native effects (and stop), fades. */
    val ALL_ACTIONS: List<String> = ACTIONS + Effects.NATIVE.map { "effect:${it.id}" } + "effect:0" +
        FADE_MINUTES.map { "fadein:$it" } + FADE_MINUTES.map { "fadeout:$it" }
    fun validAction(a: String): Boolean = a in ACTIONS ||
        Regex("effect:(\\d{1,3})").matchEntire(a)?.let { val id = it.groupValues[1].toInt(); id == 0 || Effects.native(id) != null } == true ||
        Regex("fade(in|out):(\\d{1,2})").matchEntire(a)?.let { it.groupValues[2].toInt() in 1..Fades.MAX_MINUTES } == true
    fun fadeMinutes(a: String) = Regex("fade(in|out):(\\d{1,2})").matchEntire(a)?.groupValues?.get(2)?.toInt()
    /** Extra run time a schedule needs beyond sending one command per bulb (fades). */
    fun extraRunMs(s: Schedule) = fadeMinutes(s.action)?.let { Fades.durationMs(it) } ?: 0L

    fun dayBit(d: DayOfWeek) = 1 shl (d.value - 1)

    /**
     * Next run strictly after [now]. DST: a time skipped by spring-forward runs shifted by the gap (02:30 → 03:30);
     * a time repeated by fall-back runs once, at its first occurrence (ZonedDateTime.of rules). Null if no day is set.
     */
    fun nextFire(s: Schedule, now: ZonedDateTime, zone: ZoneId = now.zone, loc: SunLocation? = null): ZonedDateTime? {
        if (s.days and EVERY_DAY == 0) return null
        val sun = s.trigger == "sunrise" || s.trigger == "sunset"
        if (sun && loc == null) return null // needs coordinates in settings
        val today = now.withZoneSameInstant(zone).toLocalDate()
        // Sun triggers look further ahead: near the poles the sun may not rise or set for months.
        for (i in -1..(if (sun) 400L else 8L)) {
            val day = today.plusDays(i)
            if (s.days and dayBit(day.dayOfWeek) == 0) continue
            val t = if (sun) Sun.event(day, loc!!, s.trigger == "sunrise", zone)?.plusMinutes(s.offset.toLong()) ?: continue
                    else ZonedDateTime.of(day, LocalTime.of(s.hour, s.minute), zone)
            if (t.isAfter(now)) return t
        }
        return null
    }

    /** Retry delays after a run reached no bulb. Each retry still has to be inside [GRACE_MS] of the original due time. */
    val RETRY_DELAYS_MS = longArrayOf(2 * 60_000L, 5 * 60_000L)

    /** Time of retry number [attempt] (1-based) for a run due at [dueMs], or null when there is none left inside the grace window. */
    fun retryAt(dueMs: Long, attempt: Int, nowMs: Long, graceMs: Long = GRACE_MS): Long? {
        if (attempt < 1 || attempt > RETRY_DELAYS_MS.size) return null
        val t = maxOf(dueMs + RETRY_DELAYS_MS[attempt - 1], nowMs + 1_000)
        return if (t - dueMs <= graceMs - 30_000) t else null
    }

    /**
     * Clock set back: a due time at or before the last one that already fired is a replay and must not run again.
     * [lastFiredMs] 0 = never fired.
     */
    fun isReplay(dueMs: Long, lastFiredMs: Long) = lastFiredMs > 0 && dueMs <= lastFiredMs

    /** Next run strictly after both [now] and the last fired time, so a clock set back never re-plans a run that already happened. */
    fun nextFireAfter(s: Schedule, now: ZonedDateTime, lastFiredMs: Long, loc: SunLocation? = null): ZonedDateTime? {
        val last = java.time.Instant.ofEpochMilli(lastFiredMs).atZone(now.zone)
        return nextFire(s, if (lastFiredMs > 0 && last.isAfter(now)) last else now, now.zone, loc)
    }

    /** Lowest non-negative slot not in [used]: dense ids for alarm request codes (code = slot * 2, retry = slot * 2 + 1). */
    fun lowestFreeSlot(used: Collection<Int>): Int { var i = 0; while (i in used) i++; return i }

    /** Null when the entry is usable, else why not. */
    fun invalidReason(s: Schedule): String? = when {
        s.id == 0L -> "missing id"
        s.hour !in 0..23 -> "hour ${s.hour}"
        s.minute !in 0..59 -> "minute ${s.minute}"
        s.days < 0 || s.days > EVERY_DAY -> "days ${s.days}"
        !validAction(s.action) -> "action ${s.action}"
        s.trigger !in TRIGGERS -> "trigger ${s.trigger}"
        s.offset !in -MAX_OFFSET..MAX_OFFSET -> "offset ${s.offset}"
        !(s.target == "all" || Regex("(room|bulb):\\d{1,9}").matches(s.target)) -> "target ${s.target}"
        else -> null
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
    fun actionText(a: String) = when {
        a == "on" -> "Turn on"; a == "off" -> "Turn off"
        a.startsWith("effect:") -> a.removePrefix("effect:").toIntOrNull()?.let { if (it == 0) "Stop effect" else "Effect: ${Effects.name(it)}" } ?: a
        a.startsWith("fadein:") -> "Fade in over ${fadeMinutes(a)} min (wake-up)"
        a.startsWith("fadeout:") -> "Fade out over ${fadeMinutes(a)} min"
        else -> "Scene: $a"
    }
    fun whenText(s: Schedule) = when (s.trigger) {
        "sunrise", "sunset" -> (if (s.trigger == "sunrise") "Sunrise" else "Sunset") + when { s.offset > 0 -> " +${s.offset} min"; s.offset < 0 -> " −${-s.offset} min"; else -> "" }
        else -> "%02d:%02d".format(s.hour, s.minute)
    }
    fun describe(h: Home?, s: Schedule) = "%s · %s · %s · %s".format(whenText(s), daysText(s.days), actionText(s.action), targetName(h, s))

    /** Runs the action on the targets over [r]. Blocking. */
    fun run(r: Router, h: Home, s: Schedule, deadlineMs: Long = Long.MAX_VALUE): BatchResult {
        val bulbs = targets(h, s).filter { it.addressKnown }
        fadeMinutes(s.action)?.let { return Fades.run(r, h, bulbs, s.action.startsWith("fadein:"), it, deadlineMs) }
        if (s.action.startsWith("effect:")) {
            val id = s.action.removePrefix("effect:").toInt()
            val color = bulbs.filter { Effects.supports(it) }
            if (color.isEmpty()) return BatchResult(0, bulbs.size, "no full-color bulb for effects")
            return r.sendMany(h, color, Cmd.Effect(id))
        }
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
            .put("Action", it.action).put("HomeId", it.homeId).put("Target", it.target).put("Trigger", it.trigger).put("Offset", it.offset)
    })).toString(1)

    /**
     * Like [toJson], but entries of [existing] that were skipped as invalid at load are written back unchanged (after the
     * valid ones), so saving never silently deletes them. Unreadable [existing]: plain toJson.
     */
    fun toJsonKeepingSkipped(l: List<Schedule>, existing: String?): String {
        val keep = ArrayList<Any>()
        if (existing != null) try {
            val a = JSONObject(existing).optJSONArray("Schedules")
            val seen = HashSet<Long>()
            if (a != null) for (i in 0 until a.length()) {
                val e = a.opt(i)
                val x = try { (e as JSONObject).let { o -> Schedule(o.getLong("Id"), o.optBoolean("Enabled", true), o.getInt("Hour"), o.getInt("Minute"), o.optInt("Days", EVERY_DAY),
                    o.optString("Action", "on"), o.optLong("HomeId"), o.optString("Target", "all"), o.optString("Trigger", "time"), o.optInt("Offset", 0)) } } catch (_: Exception) { null }
                if (x != null && invalidReason(x) == null && seen.add(x.id)) continue // valid: the caller's list is authoritative
                if (e != null) keep += e
            }
        } catch (_: Exception) { }
        if (keep.isEmpty()) return toJson(l)
        val o = JSONObject(toJson(l)); val arr = o.getJSONArray("Schedules"); keep.forEach { arr.put(it) }
        return o.toString(1)
    }

    /** Parsed schedules plus the number of entries skipped as invalid. */
    data class Parsed(val schedules: List<Schedule>, val skipped: Int)

    fun fromJson(s: String): List<Schedule> = parse(s).schedules

    /** Throws only when the file itself is not JSON; bad entries (hour 24, unknown action, ...) are skipped and counted. */
    fun parse(s: String): Parsed {
        val a = JSONObject(s).optJSONArray("Schedules") ?: return Parsed(emptyList(), 0)
        val out = ArrayList<Schedule>(); var bad = 0
        for (i in 0 until a.length()) try {
            val o = a.getJSONObject(i)
            val x = Schedule(o.getLong("Id"), o.optBoolean("Enabled", true), o.getInt("Hour"), o.getInt("Minute"), o.optInt("Days", EVERY_DAY),
                o.optString("Action", "on"), o.optLong("HomeId"), o.optString("Target", "all"), o.optString("Trigger", "time"), o.optInt("Offset", 0))
            val why = invalidReason(x)
            if (why != null || out.any { it.id == x.id }) { bad++; Diag.log("schedule entry skipped: ${why ?: "duplicate id"}", null) } else out += x
        } catch (e: Exception) { bad++; Diag.log("schedule entry skipped", e) }
        return Parsed(out, bad)
    }

    /** Result of reading schedules.json: the schedules, skipped entries, and the quarantined file name if it was unreadable. */
    data class Loaded(val schedules: List<Schedule>, val skipped: Int, val quarantined: String?)

    /**
     * Loads [f]. An unreadable file (not JSON) is renamed to `schedules.json.corrupt-<stamp>` and an empty list is returned,
     * so the next save never overwrites what was there.
     */
    fun loadOrQuarantine(f: java.io.File, stamp: String, read: () -> String = { f.readText() }): Loaded {
        if (!f.exists()) return Loaded(emptyList(), 0, null)
        return try { val p = parse(read()); Loaded(p.schedules, p.skipped, null) } catch (e: Exception) {
            Diag.log("schedules unreadable", e)
            val q = java.io.File(f.path + ".corrupt-" + stamp)
            Loaded(emptyList(), 0, if (f.renameTo(q)) q.name else f.name)
        }
    }
}
