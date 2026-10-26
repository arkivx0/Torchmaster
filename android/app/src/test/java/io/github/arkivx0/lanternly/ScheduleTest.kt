package io.github.arkivx0.lanternly

import io.github.arkivx0.lanternly.core.*
import org.junit.Assert.*
import org.junit.Test
import java.time.DayOfWeek
import java.time.ZoneId
import java.time.ZoneOffset
import java.time.ZonedDateTime

class ScheduleTest {
    private val ny = ZoneId.of("America/New_York")
    private fun at(y: Int, mo: Int, d: Int, h: Int, mi: Int) = ZonedDateTime.of(y, mo, d, h, mi, 0, 0, ny)
    private fun s(h: Int, m: Int, days: Int = Schedules.EVERY_DAY, action: String = "on", target: String = "all") = Schedule(1, true, h, m, days, action, 7, target)

    @Test fun nextFireLaterTodayOrTomorrow() {
        val now = at(2026, 10, 14, 6, 0) // Wednesday
        assertEquals(at(2026, 10, 14, 7, 0), Schedules.nextFire(s(7, 0), now))
        assertEquals(at(2026, 10, 15, 5, 0), Schedules.nextFire(s(5, 0), now))
        assertEquals(at(2026, 10, 15, 6, 0), Schedules.nextFire(s(6, 0), now)) // exactly now: next one
    }

    @Test fun dayOfWeekMask() {
        val wed = at(2026, 10, 14, 8, 0)
        assertEquals(at(2026, 10, 15, 7, 0), Schedules.nextFire(s(7, 0, 0x1f), wed))
        assertEquals(at(2026, 10, 19, 7, 0), Schedules.nextFire(s(7, 0, Schedules.dayBit(DayOfWeek.MONDAY)), wed))
        assertEquals(at(2026, 10, 17, 9, 0), Schedules.nextFire(s(9, 0, 0x60), wed))
        assertEquals(at(2026, 10, 21, 7, 0), Schedules.nextFire(s(7, 0, Schedules.dayBit(DayOfWeek.WEDNESDAY)), wed))
        assertNull(Schedules.nextFire(s(7, 0, 0), wed))
        assertEquals(1, Schedules.dayBit(DayOfWeek.MONDAY)); assertEquals(64, Schedules.dayBit(DayOfWeek.SUNDAY))
    }

    @Test fun dstSpringForwardGapShiftsByTheGap() {
        val now = ZonedDateTime.of(2026, 3, 8, 1, 0, 0, 0, ny)
        val t = Schedules.nextFire(s(2, 30), now)!!
        assertEquals(ZoneOffset.ofHours(-4), t.offset); assertEquals(3, t.hour); assertEquals(30, t.minute); assertEquals(8, t.dayOfMonth)
        assertEquals(at(2026, 3, 8, 7, 0), Schedules.nextFire(s(7, 0), now))
    }

    @Test fun dstFallBackRunsOnce() {
        val now = ZonedDateTime.of(2026, 11, 1, 0, 0, 0, 0, ny)
        val first = Schedules.nextFire(s(1, 30), now)!!
        assertEquals(ZoneOffset.ofHours(-4), first.offset)
        val second = Schedules.nextFire(s(1, 30), first)!!
        assertEquals(2, second.dayOfMonth); assertEquals(ZoneOffset.ofHours(-5), second.offset)
        val sat = at(2026, 10, 31, 7, 0)
        assertEquals(25 * 3600L, java.time.Duration.between(sat, Schedules.nextFire(s(7, 0), sat)).seconds)
    }

    @Test fun missedRunsAreSkipped() {
        assertTrue(Schedules.onTime(1_000_000, 1_000_000 + 4 * 60_000))
        assertFalse(Schedules.onTime(1_000_000, 1_000_000 + 30 * 60_000))
        assertFalse(Schedules.onTime(1_000_000, 999_000))
    }

    @Test fun targetsJsonAndRedAction() {
        val h = Home(7, "H", "n", "p", listOf(Room(4, "R", listOf(3))), mutableListOf(Bulb("A", 2, Caps.ALL_CONTROLS, ""), Bulb("B", 3, Caps.ALL_CONTROLS, "")))
        assertEquals(2, Schedules.targets(h, s(1, 0)).size)
        assertEquals("B", Schedules.targets(h, s(1, 0, target = "room:4")).single().name)
        assertEquals("A", Schedules.targets(h, s(1, 0, target = "bulb:2")).single().name)
        val l = listOf(Schedule(9, false, 21, 5, 0x60, "Red", 7, "bulb:2"))
        assertEquals(l, Schedules.fromJson(Schedules.toJson(l)))
        assertEquals("21:05 · Weekends · Scene: Red · A", Schedules.describe(h, l[0]))
        val sent = ArrayList<Cmd>()
        val link = object : Link { override val kind = Via.BLE; override var onState: ((Home?, BulbState) -> Unit)? = null
            override fun canReach(h: Home, b: Bulb) = true; override fun send(h: Home, b: Bulb, c: Cmd) { sent += c } }
        assertTrue(Schedules.run(Router(listOf(link)), h, l[0]).allOk)
        assertEquals(listOf(Cmd.Power(true), Cmd.Brightness(100), Cmd.Rgb(255, 0, 0)), sent)
    }
}
