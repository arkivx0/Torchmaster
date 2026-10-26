package io.github.arkivx0.lanternly

import io.github.arkivx0.lanternly.core.*
import org.junit.Assert.*
import org.junit.Test
import java.time.LocalDate
import java.time.ZoneId
import java.time.ZonedDateTime
import java.time.Duration

/** Reference times from an independent implementation (astral 3), public city-centre coordinates. Tolerance 2 min. */
class SunTest {
    private fun check(lat: Double, lon: Double, zone: String, date: String, rise: String, set: String) {
        val z = ZoneId.of(zone); val d = LocalDate.parse(date); val loc = SunLocation(lat, lon)
        for ((isRise, want) in listOf(true to rise, false to set)) {
            val got = Sun.event(d, loc, isRise, z) ?: throw AssertionError("no event $zone $date $isRise")
            val exp = ZonedDateTime.of(d, java.time.LocalTime.parse(want), z)
            assertTrue("$zone $date ${if (isRise) "rise" else "set"}: got $got want $exp", Math.abs(Duration.between(exp, got).seconds) <= 120)
        }
    }
    @Test fun midLatitudes() {
        check(40.7128, -74.0060, "America/New_York", "2026-06-21", "05:25:17", "20:30:29")
        check(51.5074, -0.1278, "Europe/London", "2026-12-21", "08:04:06", "15:53:03")
        check(35.6762, 139.6503, "Asia/Tokyo", "2026-09-01", "05:13", "18:09")
    }
    @Test fun equatorSouthernAndTropics() {
        check(0.0, 0.0, "UTC", "2026-03-20", "06:04:20", "18:10:30")
        check(-33.8688, 151.2093, "Australia/Sydney", "2026-01-01", "05:47:44", "20:09:09")
        check(21.3069, -157.8583, "Pacific/Honolulu", "2026-07-04", "05:54", "19:17")
    }
    @Test fun highLatitudeAndPolar() {
        check(64.1466, -21.9426, "Atlantic/Reykjavik", "2026-06-01", "03:23", "23:29")
        val tromso = SunLocation(69.6492, 18.9553); val z = ZoneId.of("Europe/Oslo")
        assertNull(Sun.event(LocalDate.of(2026, 6, 21), tromso, false, z)) // midnight sun
        assertNull(Sun.event(LocalDate.of(2026, 12, 21), tromso, true, z)) // polar night
        // a sunset schedule there still finds the next real sunset (late July)
        val s = Schedule(1, true, 0, 0, Schedules.EVERY_DAY, "on", 1, "all", "sunset", 0)
        val t = Schedules.nextFire(s, ZonedDateTime.of(2026, 6, 21, 12, 0, 0, 0, z), z, tromso)!!
        assertTrue(t.monthValue == 7)
    }
    @Test fun dstChangesAreFollowed() {
        val c = ZoneId.of("America/New_York")
        check(40.7128, -74.0060, "America/New_York", "2026-03-07", "06:20:42", "17:53:52")
        check(40.7128, -74.0060, "America/New_York", "2026-03-08", "07:19:06", "18:54:58") // first EDT day: about +1 h on the clock
        check(40.7128, -74.0060, "America/New_York", "2026-11-01", "06:26:38", "16:51:56") // first EST day
        val loc = SunLocation(40.7128, -74.0060)
        val s = Schedule(1, true, 0, 0, Schedules.EVERY_DAY, "on", 1, "all", "sunrise", -30)
        val t = Schedules.nextFire(s, ZonedDateTime.of(2026, 3, 8, 0, 0, 0, 0, c), c, loc)!!
        assertEquals(8, t.dayOfMonth); assertEquals(6, t.hour); assertEquals(java.time.ZoneOffset.ofHours(-4), t.offset)
    }
    @Test fun offsetsDaysAndMissingLocation() {
        val c = ZoneId.of("America/New_York"); val loc = SunLocation(40.7128, -74.0060)
        val s = Schedule(1, true, 0, 0, Schedules.EVERY_DAY, "on", 1, "all", "sunset", 15)
        val now = ZonedDateTime.of(2026, 6, 21, 12, 0, 0, 0, c)
        val t = Schedules.nextFire(s, now, c, loc)!!
        assertEquals(20, t.hour); assertTrue(t.minute in 43..47)
        assertNull(Schedules.nextFire(s, now, c, null))
        val afterSunset = Schedules.nextFire(s, ZonedDateTime.of(2026, 6, 21, 22, 0, 0, 0, c), c, loc)!!
        assertEquals(22, afterSunset.dayOfMonth)
        val sundays = s.copy(days = Schedules.dayBit(java.time.DayOfWeek.SUNDAY)) // 2026-06-21 is a Sunday
        assertEquals(28, Schedules.nextFire(sundays, ZonedDateTime.of(2026, 6, 21, 22, 0, 0, 0, c), c, loc)!!.dayOfMonth)
    }
    @Test fun locationParsing() {
        assertEquals(SunLocation(39.74, -104.99), SunLocation.parse(" 39,74 ", "-104.99")) // Denver city centre
        assertNull(SunLocation.parse("91", "0")); assertNull(SunLocation.parse("0", "181")); assertNull(SunLocation.parse("x", "0"))
    }
    @Test fun scheduleJsonRoundTripAndValidation() {
        val s = Schedule(5, true, 0, 0, 0x1f, "fadein:15", 3, "room:2", "sunrise", -20)
        val back = Schedules.fromJson(Schedules.toJson(listOf(s)))
        assertEquals(listOf(s), back)
        // 0.1.5 files have no Trigger/Offset: time trigger
        val old = """{"Schedules":[{"Id":1,"Hour":7,"Minute":0,"Days":127,"Action":"on","HomeId":1,"Target":"all"}]}"""
        assertEquals("time", Schedules.fromJson(old)[0].trigger)
        for (bad in listOf(s.copy(trigger = "noon"), s.copy(offset = 500), s.copy(action = "fadein:0"), s.copy(action = "fadein:61"), s.copy(action = "effect:42"), s.copy(action = "effect:x")))
            assertNotNull(Schedules.invalidReason(bad))
        for (ok in Schedules.ALL_ACTIONS) assertNull(ok, Schedules.invalidReason(s.copy(action = ok)))
        assertEquals("Sunrise −20 min · Weekdays · Fade in over 15 min (wake-up) · ?", Schedules.describe(null, s).replace("(removed room)", "?"))
    }
}
