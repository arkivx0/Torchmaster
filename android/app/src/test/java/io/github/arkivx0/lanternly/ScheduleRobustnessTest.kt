package io.github.arkivx0.lanternly

import io.github.arkivx0.lanternly.core.*
import org.junit.Assert.*
import org.junit.Test
import java.io.File
import java.nio.file.Files
import java.time.ZoneId
import java.time.ZonedDateTime

/** 0.1.3 review fixes: validation on load, retry timing, clock-set-back dedupe, quarantine, dense alarm slots. */
class ScheduleRobustnessTest {
    private val ny = ZoneId.of("America/New_York")
    private fun entry(id: Any?, hour: Any?, minute: Any?, days: Any? = 127, action: String = "on", target: String = "all") =
        """{"Id":$id,"Enabled":true,"Hour":$hour,"Minute":$minute,"Days":$days,"Action":"$action","HomeId":7,"Target":"$target"}"""
    private fun file(vararg e: String) = """{"Format":1,"Schedules":[${e.joinToString(",")}]}"""

    @Test fun invalidEntriesAreSkippedNotFatal() {
        val p = Schedules.parse(file(
            entry(1, 7, 0), entry(2, 24, 0), entry(3, 7, 60), entry(4, -1, 0), entry(5, 7, 0, days = 128), entry(6, 7, 0, days = -1),
            entry(7, 7, 0, action = "Disco"), entry(8, 7, 0, target = "room:x"), entry(9, "\"seven\"", 0), "42", entry(1, 8, 0), entry(10, 23, 59, days = 0, target = "bulb:3")))
        assertEquals(listOf(1L, 10L), p.schedules.map { it.id })
        assertEquals(10, p.skipped)
        // a valid list round-trips untouched
        val l = listOf(Schedule(11, true, 0, 0, 1, "Red", 7, "room:4"))
        assertEquals(Schedules.Parsed(l, 0), Schedules.parse(Schedules.toJson(l)))
    }

    @Test fun boundaryValuesAccepted() {
        assertNull(Schedules.invalidReason(Schedule(1, true, 0, 0, 0, "off", 1, "all")))
        assertNull(Schedules.invalidReason(Schedule(1, true, 23, 59, 0x7f, "on", 1, "bulb:255")))
        assertNotNull(Schedules.invalidReason(Schedule(0, true, 1, 1, 1, "on", 1, "all")))
    }

    @Test fun retriesStayInsideGraceWindow() {
        val due = 1_000_000_000L
        assertEquals(due + 2 * 60_000, Schedules.retryAt(due, 1, due + 30_000))
        assertEquals(due + 5 * 60_000, Schedules.retryAt(due, 2, due + 3 * 60_000))
        assertNull(Schedules.retryAt(due, 3, due + 6 * 60_000)) // only two retries
        assertNull(Schedules.retryAt(due, 0, due))
        // a slow first attempt pushes the retry later; past the grace window there is none
        assertEquals(due + 8 * 60_000 + 1_000, Schedules.retryAt(due, 2, due + 8 * 60_000))
        assertNull(Schedules.retryAt(due, 2, due + 9 * 60_000 + 40_000))
        // each retry time is still on time for the original due time
        for (a in 1..2) Schedules.retryAt(due, a, due)?.let { assertTrue(Schedules.onTime(due, it)) }
    }

    @Test fun clockSetBackDoesNotReplay() {
        val s = Schedule(1, true, 7, 0, 0x7f, "on", 7, "all")
        val fired = ZonedDateTime.of(2026, 10, 14, 7, 0, 0, 0, ny)
        val firedMs = fired.toInstant().toEpochMilli()
        assertTrue(Schedules.isReplay(firedMs, firedMs))
        assertTrue(Schedules.isReplay(firedMs - 1, firedMs))
        assertFalse(Schedules.isReplay(firedMs + 86_400_000, firedMs))
        assertFalse(Schedules.isReplay(firedMs, 0))
        // clock moved back to 06:50 after the 07:00 run: next run is tomorrow, not 07:00 again
        assertEquals(fired.plusDays(1), Schedules.nextFireAfter(s, fired.minusMinutes(10), firedMs))
        // normal case: unaffected by an older last-fired time
        assertEquals(fired, Schedules.nextFireAfter(s, fired.minusMinutes(10), firedMs - 86_400_000))
        assertEquals(fired, Schedules.nextFireAfter(s, fired.minusMinutes(10), 0))
    }

    @Test fun unreadableFileIsQuarantinedNotOverwritten() {
        val dir = Files.createTempDirectory("lt").toFile()
        try {
            val f = File(dir, "schedules.json")
            assertEquals(Schedules.Loaded(emptyList(), 0, null), Schedules.loadOrQuarantine(f, "x"))
            f.writeText("{not json")
            val l = Schedules.loadOrQuarantine(f, "20261025-200000")
            assertEquals(emptyList<Schedule>(), l.schedules)
            assertEquals("schedules.json.corrupt-20261025-200000", l.quarantined)
            assertFalse(f.exists())
            assertEquals("{not json", File(dir, l.quarantined!!).readText())
            // a readable file with one bad entry is kept in place
            f.writeText(file(entry(1, 7, 0), entry(2, 99, 0)))
            val ok = Schedules.loadOrQuarantine(f, "y")
            assertEquals(1, ok.schedules.size); assertEquals(1, ok.skipped); assertNull(ok.quarantined); assertTrue(f.exists())
        } finally { dir.deleteRecursively() }
    }

    @Test fun denseAlarmSlots() {
        assertEquals(0, Schedules.lowestFreeSlot(emptyList()))
        assertEquals(2, Schedules.lowestFreeSlot(listOf(0, 1, 3)))
        assertEquals(1, Schedules.lowestFreeSlot(listOf(0, 2)))
    }

    @Test fun graceIsTenMinutes() = assertEquals(10 * 60_000L, Schedules.GRACE_MS)
}
