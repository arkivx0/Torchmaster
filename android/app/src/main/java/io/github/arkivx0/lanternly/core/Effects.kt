package io.github.arkivx0.lanternly.core

import kotlin.random.Random

/**
 * Light shows. NATIVE shows run inside the bulb (one command; Lanternly can disconnect). They use the Cync app's
 * "light run mode" command: opcode 0xE2, vendor 0x0211, data 07 <mode> <show id> <random byte>; mode 1 = light show,
 * mode 0 = static (stop). On the wire: E2 11 02 07 01 <id> <rnd>. Same as the official app's SetLightRunModeCommand.
 * APP-SIDE effects are not in the bulb: Lanternly sends timed color/brightness commands while it stays connected
 * (rate limited, see [AppEffects]). Mirrors windows/Lanternly.Core/Effects.cs.
 */
object Effects {
    const val RUN_MODE = 0x07
    const val MODE_STATIC = 0; const val MODE_LIGHT_SHOW = 1
    data class Native(val name: String, val id: Int)
    /** Show ids from the official app's LightRunMode.LightShow table. 65..67 are newer and may not run on older bulbs. */
    val NATIVE = listOf(
        Native("Candlelight", 1), Native("Rainbow", 2), Native("Fireworks", 3), Native("Volcanic", 4),
        Native("Aurora", 5), Native("Happy Holidays", 6), Native("Red, White & Blue", 7), Native("Vegas", 8),
        Native("Party Time", 9), Native("Power Up", 65), Native("Splash", 66), Native("Cyber", 67),
    )
    fun native(id: Int) = NATIVE.firstOrNull { it.id == id }
    fun name(id: Int) = if (id == 0) "Stop effect" else native(id)?.name ?: "Effect $id"
    /** Native shows need a full-color bulb (the official app offers them only on color bulbs). */
    fun supports(b: Bulb) = b.has(Caps.COLOR)
    /** Data bytes for opcode 0xE2. [id] 0 = stop (static mode). */
    fun data(id: Int, rnd: Int = Random.nextInt(256)) =
        byteArrayOf(RUN_MODE.toByte(), (if (id == 0) MODE_STATIC else MODE_LIGHT_SHOW).toByte(), id.toByte(), rnd.toByte())
}

/** App-side effects (Lanternly sends each step; stops when the app stops or disconnects). */
object AppEffects {
    val ALL = listOf("Color cycle", "Candle flicker", "Breathe")
    /** Never more than this many commands per second in total, so the mesh and normal controls stay responsive. */
    const val MAX_CMDS_PER_SEC = 4
    const val MIN_STEP_MS = 400L
    fun baseStepMs(name: String) = when (name) { "Color cycle" -> 1_500L; "Candle flicker" -> 450L; else -> 700L }
    /** Delay between steps for [bulbs] targets (each step sends one command per bulb). */
    fun stepMs(name: String, bulbs: Int) = maxOf(MIN_STEP_MS, baseStepMs(name), 1000L * maxOf(1, bulbs) / MAX_CMDS_PER_SEC)
    fun needsColor(name: String) = name == "Color cycle" || name == "Candle flicker"

    /** Command for step [i] (deterministic for a given [rnd]). */
    fun step(name: String, i: Int, rnd: Random): Cmd = when (name) {
        "Color cycle" -> hsv((i * 24) % 360).let { Cmd.Rgb(it.first, it.second, it.third) }
        "Candle flicker" -> { val g = 70 + rnd.nextInt(50); Cmd.Rgb(255, g, rnd.nextInt(12)) }
        "Breathe" -> { val p = i % 20; Cmd.Brightness(if (p < 10) 10 + p * 9 else 100 - (p - 10) * 9) }
        else -> throw IllegalArgumentException("unknown effect $name")
    }
    /** Candle also dims a little per step on dimmable bulbs. */
    fun extra(name: String, rnd: Random): Cmd? = if (name == "Candle flicker") Cmd.Brightness(35 + rnd.nextInt(30)) else null

    fun hsv(h: Int): Triple<Int, Int, Int> {
        val x = ((1 - Math.abs((h / 60.0) % 2 - 1)) * 255).toInt()
        return when (h / 60) { 0 -> Triple(255, x, 0); 1 -> Triple(x, 255, 0); 2 -> Triple(0, 255, x); 3 -> Triple(0, x, 255); 4 -> Triple(x, 0, 255); else -> Triple(255, 0, x) }
    }

    /** Blocking loop until [stop] returns true. [sleep] is injectable for tests. */
    fun run(r: Router, h: Home, bulbs: List<Bulb>, name: String, stop: () -> Boolean, sleep: (Long) -> Unit = { Thread.sleep(it) }) {
        val targets = bulbs.filter { it.addressKnown && (!needsColor(name) || it.has(Caps.COLOR)) && (name != "Breathe" || it.has(Caps.DIM)) }
        val rnd = Random(System.nanoTime()); var i = 0
        val wait = stepMs(name, targets.size * (if (name == "Candle flicker") 2 else 1))
        while (!stop()) {
            for (b in targets) try { r.send(h, b, step(name, i, rnd)); if (b.has(Caps.DIM)) extra(name, rnd)?.let { r.send(h, b, it) } } catch (e: Exception) { Diag.log("app effect", e) }
            i++; sleep(wait)
        }
    }
}

/** Fade-in / fade-out over N minutes, as timed brightness steps (the bulbs have no long native fade). */
object Fades {
    const val MAX_MINUTES = 60
    /** At least this long between brightness steps. */
    const val MIN_STEP_MS = 30_000L
    data class Step(val atMs: Long, val pct: Int)
    /** Brightness steps from [from] to [to] (1..100) over [minutes]; first at 0, last at the end, distinct values, spacing >= MIN_STEP_MS. */
    fun plan(from: Int, to: Int, minutes: Int): List<Step> {
        val a = from.coerceIn(1, 100); val b = to.coerceIn(1, 100); val total = minutes.coerceIn(1, MAX_MINUTES) * 60_000L
        val n = minOf(Math.abs(b - a), (total / MIN_STEP_MS).toInt()).coerceAtLeast(1)
        val out = ArrayList<Step>()
        for (k in 0..n) { val p = a + Math.round((b - a) * k / n.toDouble()).toInt(); if (out.isEmpty() || out.last().pct != p) out += Step(total * k / n, p) }
        return out
    }
    fun durationMs(minutes: Int) = minutes.coerceIn(1, MAX_MINUTES) * 60_000L

    /**
     * Blocking. Fade in: on at 1 % then up to 100 %. Fade out: from the brightest known level (100 if unknown) down to 1 %,
     * then off. Stops early at [deadlineMs] (jumping to the final level). A bulb counts if its first step reached it.
     */
    fun run(r: Router, h: Home, bulbs: List<Bulb>, fadeIn: Boolean, minutes: Int, deadlineMs: Long = Long.MAX_VALUE,
            now: () -> Long = System::currentTimeMillis, sleep: (Long) -> Unit = { Thread.sleep(it) }): BatchResult {
        val dim = bulbs.filter { it.has(Caps.DIM) }
        val start = if (fadeIn) 1 else (bulbs.mapNotNull { it.state?.takeIf { s -> s.on }?.brightness }.maxOrNull() ?: 100).coerceIn(1, 100)
        val steps = plan(start, if (fadeIn) 100 else 1, minutes)
        val reached = HashSet<Bulb>(); var first: String? = null
        fun each(l: List<Bulb>, c: Cmd, count: Boolean) { for (b in l) try { r.send(h, b, c); if (count) reached += b } catch (e: Exception) { if (first == null) first = "${b.name}: ${e.message}"; Diag.log("fade", e) } }
        if (fadeIn) { each(dim, Cmd.Brightness(1), true); each(bulbs, Cmd.Power(true), true) }
        val t0 = now()
        for ((k, s) in steps.withIndex()) {
            if (k > 0) { val wait = t0 + s.atMs - now(); if (t0 + s.atMs > deadlineMs) { each(dim, Cmd.Brightness(steps.last().pct), false); break }; if (wait > 0) sleep(wait) }
            each(dim, Cmd.Brightness(s.pct), k == 0)
            if (k == 0 && reached.isEmpty()) return BatchResult(0, bulbs.size, first ?: "no dimmable bulb") // nothing answered: don't wait out the fade
        }
        if (!fadeIn) each(bulbs, Cmd.Power(false), dim.isEmpty())
        return BatchResult(reached.size, bulbs.size, first)
    }
}
