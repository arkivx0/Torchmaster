package io.github.arkivx0.lanternly.core

import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.time.ZonedDateTime
import kotlin.math.*

/** Coordinates typed by the user (decimal degrees, north/east positive). Never from a location permission. */
data class SunLocation(val lat: Double, val lon: Double) {
    /** 1 decimal (about 10 km): what Torchmaster stores. */
    fun rounded() = SunLocation(Math.round(lat * 10) / 10.0, Math.round(lon * 10) / 10.0)
    companion object {
        fun valid(lat: Double, lon: Double) = !lat.isNaN() && !lon.isNaN() && lat in -90.0..90.0 && lon in -180.0..180.0
        fun parse(lat: String, lon: String): SunLocation? {
            val a = lat.trim().replace(',', '.').toDoubleOrNull() ?: return null; val b = lon.trim().replace(',', '.').toDoubleOrNull() ?: return null
            return if (valid(a, b)) SunLocation(a, b) else null
        }
    }
}

/**
 * Sunrise / sunset, computed offline with the NOAA solar calculator algorithm (Jean Meeus, "Astronomical Algorithms"):
 * apparent sunrise/sunset at a zenith of 90.833° (refraction + solar disc). Accuracy is about a minute for |lat| < 72°.
 * Mirrors windows/Lanternly.Core/Sun.cs.
 */
object Sun {
    private fun rad(d: Double) = d * PI / 180; private fun deg(r: Double) = r * 180 / PI

    private fun geomMeanLong(t: Double) = (280.46646 + t * (36000.76983 + 0.0003032 * t)).mod(360.0)
    private fun geomMeanAnom(t: Double) = 357.52911 + t * (35999.05029 - 0.0001537 * t)
    private fun eccent(t: Double) = 0.016708634 - t * (0.000042037 + 0.0000001267 * t)
    private fun eqOfCenter(t: Double): Double { val m = rad(geomMeanAnom(t)); return sin(m) * (1.914602 - t * (0.004817 + 0.000014 * t)) + sin(2 * m) * (0.019993 - 0.000101 * t) + sin(3 * m) * 0.000289 }
    private fun appLong(t: Double) = geomMeanLong(t) + eqOfCenter(t) - 0.00569 - 0.00478 * sin(rad(125.04 - 1934.136 * t))
    private fun obliq(t: Double): Double { val e0 = 23 + (26 + (21.448 - t * (46.815 + t * (0.00059 - t * 0.001813))) / 60) / 60; return e0 + 0.00256 * cos(rad(125.04 - 1934.136 * t)) }
    private fun decl(t: Double) = deg(asin(sin(rad(obliq(t))) * sin(rad(appLong(t)))))
    private fun eqTime(t: Double): Double {
        val y = tan(rad(obliq(t)) / 2).let { it * it }; val l0 = rad(geomMeanLong(t)); val e = eccent(t); val m = rad(geomMeanAnom(t))
        return 4 * deg(y * sin(2 * l0) - 2 * e * sin(m) + 4 * e * y * sin(m) * cos(2 * l0) - 0.5 * y * y * sin(4 * l0) - 1.25 * e * e * sin(2 * m))
    }
    /** Hour angle in degrees, or NaN when the sun doesn't cross the horizon that day. */
    private fun hourAngle(lat: Double, d: Double) = deg(acos(cos(rad(90.833)) / (cos(rad(lat)) * cos(rad(d))) - tan(rad(lat)) * tan(rad(d))))

    /** Minutes after 00:00 UTC of [date] (may be < 0 or >= 1440), or null (polar day/night). */
    fun utcMinutes(date: LocalDate, lat: Double, lon: Double, rise: Boolean): Double? {
        val jd = date.toEpochDay() + 2440587.5
        var m = 720.0 - 4 * lon
        repeat(3) { // refine at the event time itself
            val t = (jd + m / 1440 - 2451545.0) / 36525
            val ha = hourAngle(lat, decl(t)); if (ha.isNaN()) return null
            m = 720 - 4 * (lon + if (rise) ha else -ha) - eqTime(t)
        }
        return m
    }

    /** The event on local date [date] in [zone], or null when the sun doesn't rise/set that day. */
    fun event(date: LocalDate, loc: SunLocation, rise: Boolean, zone: ZoneId): ZonedDateTime? {
        // The local day may span two UTC dates: try the neighbours and keep the one that falls on [date] locally.
        for (d in longArrayOf(0, -1, 1)) {
            val day = date.plusDays(d)
            val m = utcMinutes(day, loc.lat, loc.lon, rise) ?: continue
            val t = Instant.ofEpochSecond(day.toEpochDay() * 86400 + Math.round(m * 60)).atZone(zone)
            if (t.toLocalDate() == date) return t
        }
        return null
    }
}
