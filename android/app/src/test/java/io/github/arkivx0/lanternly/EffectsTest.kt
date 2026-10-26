package io.github.arkivx0.lanternly

import io.github.arkivx0.lanternly.core.*
import org.junit.Assert.*
import org.junit.Test
import kotlin.random.Random

class EffectsTest {
    /** Same bytes as the official app's SetLightRunModeCommand: E2 11 02 07 <mode> <show> <rnd>. */
    @Test fun nativeEffectBytes() {
        assertArrayEquals(byteArrayOf(7, 1, 1, 0x55), Effects.data(1, 0x55)) // Candlelight
        assertArrayEquals(byteArrayOf(7, 1, 67, 0), Effects.data(67, 0)) // Cyber
        assertArrayEquals(byteArrayOf(7, 0, 0, 9), Effects.data(0, 9)) // stop: static mode
        assertEquals(listOf(1, 2, 3, 4, 5, 6, 7, 8, 9, 65, 66, 67), Effects.NATIVE.map { it.id })
        // whole packet through the session: opcode E2, vendor 0211, then the data
        val sent = ArrayList<ByteArray>()
        val g = object : GattPort { override val mac = "AA:BB:CC:DD:EE:01"
            override fun write(ch: java.util.UUID, data: ByteArray, withResponse: Boolean) { sent += data }
            override fun read(ch: java.util.UUID) = byteArrayOf(0x0d) + ByteArray(8)
            override fun subscribe(ch: java.util.UUID, onNotify: (ByteArray) -> Unit) {}
            override fun close() {} }
        val h = Home(1, "H", "n", "p", emptyList(), mutableListOf(Bulb("B", 5, 6, "AA:BB:CC:DD:EE:01")))
        val s = BleSession(g, h); s.login(); sent.clear()
        s.send(h, h.bulbs[0], Cmd.Effect(2))
        assertEquals(1, sent.size); assertEquals(20, sent[0].size)
    }

    @Test fun appEffectsAreRateLimited() {
        for (n in AppEffects.ALL) for (bulbs in 1..20) {
            val ms = AppEffects.stepMs(n, bulbs)
            assertTrue(ms >= AppEffects.MIN_STEP_MS)
            assertTrue("$n $bulbs", bulbs * 1000.0 / ms <= AppEffects.MAX_CMDS_PER_SEC + 1e-9)
        }
        val r = Random(1)
        assertEquals(Cmd.Rgb(255, 0, 0), AppEffects.step("Color cycle", 0, r))
        val c = AppEffects.step("Candle flicker", 3, r) as Cmd.Rgb
        assertEquals(255, c.r); assertTrue(c.g in 70..119)
        val br = (0 until 20).map { (AppEffects.step("Breathe", it, r) as Cmd.Brightness).pct }
        assertTrue(br.all { it in 1..100 }); assertEquals(10, br.first())
    }

    @Test fun appEffectLoopStopsAndSkipsUnknownAddress() {
        val sent = ArrayList<Pair<Int, Cmd>>()
        val link = object : Link { override val kind = Via.BLE; override var onState: ((Home?, BulbState) -> Unit)? = null
            override fun canReach(h: Home, b: Bulb) = true; override fun send(h: Home, b: Bulb, c: Cmd) { sent += b.meshId to c } }
        val h = Home(1, "H", "n", "p", emptyList(), mutableListOf(Bulb("A", 1, 6, ""), Bulb("Z", 0, 6, "")))
        var loops = 0; val sleeps = ArrayList<Long>()
        AppEffects.run(Router(listOf(link)), h, h.bulbs, "Color cycle", stop = { loops++ >= 3 }, sleep = { sleeps += it })
        assertEquals(3, sent.size); assertTrue(sent.all { it.first == 1 })
        assertTrue(sleeps.all { it >= AppEffects.MIN_STEP_MS })
    }

    @Test fun fadePlans() {
        val p = Fades.plan(1, 100, 10)
        assertEquals(1, p.first().pct); assertEquals(100, p.last().pct); assertEquals(0L, p.first().atMs); assertEquals(600_000L, p.last().atMs)
        assertTrue(p.zipWithNext().all { (a, b) -> b.atMs - a.atMs >= Fades.MIN_STEP_MS && b.pct > a.pct })
        assertEquals(21, p.size) // 10 min / 30 s = 20 steps
        val down = Fades.plan(80, 1, 60)
        assertEquals(80, down.size); assertTrue(down.zipWithNext().all { (a, b) -> b.pct < a.pct })
        assertEquals(1, Fades.plan(50, 50, 5).size)
        assertEquals(Fades.MAX_MINUTES * 60_000L, Fades.plan(1, 100, 500).last().atMs)
    }

    @Test fun fadeRunWithFakeClock() {
        val sent = ArrayList<Cmd>()
        val link = object : Link { override val kind = Via.BLE; override var onState: ((Home?, BulbState) -> Unit)? = null
            override fun canReach(h: Home, b: Bulb) = true; override fun send(h: Home, b: Bulb, c: Cmd) { sent += c } }
        val h = Home(1, "H", "n", "p", emptyList(), mutableListOf(Bulb("A", 1, 6, "")))
        var t = 0L
        val r = Fades.run(Router(listOf(link)), h, h.bulbs, true, 5, now = { t }, sleep = { t += it })
        assertTrue(r.allOk)
        assertEquals(Cmd.Brightness(1), sent[0]); assertEquals(Cmd.Power(true), sent[1]); assertEquals(Cmd.Brightness(100), sent.last())
        assertEquals(300_000L, t)
        // fade out, cut short by the deadline: jumps to 1 % and turns off
        sent.clear(); t = 0
        h.bulbs[0].state = BulbState(1, true, true, 60, 50, Triple(0, 0, 0))
        Fades.run(Router(listOf(link)), h, h.bulbs, false, 10, deadlineMs = 60_000, now = { t }, sleep = { t += it })
        assertEquals(Cmd.Brightness(60), sent[0]); assertEquals(Cmd.Power(false), sent.last()); assertEquals(Cmd.Brightness(1), sent[sent.size - 2])
        assertTrue(t <= 60_000)
        // fade out with no state yet: waits for the status report and starts from it
        sent.clear(); t = 0; h.bulbs[0].state = null; var naps = 0
        Fades.run(Router(listOf(link)), h, h.bulbs, false, 1, now = { t }, sleep = { t += it; if (++naps == 3) h.bulbs[0].state = BulbState(1, true, true, 35, 50, Triple(0, 0, 0)) })
        assertEquals(Cmd.Brightness(35), sent[0])
        // an interrupt during a send stops the fade at once (not swallowed)
        val stuck = object : Link { override val kind = Via.BLE; override var onState: ((Home?, BulbState) -> Unit)? = null
            override fun canReach(h: Home, b: Bulb) = true; override fun send(h: Home, b: Bulb, c: Cmd) { throw InterruptedException() } }
        assertThrows(InterruptedException::class.java) { Fades.run(Router(listOf(stuck)), h, h.bulbs, true, 5, now = { t }, sleep = { t += it }) }
    }

    @Test fun scheduleRunsNativeEffectOnColorBulbsOnly() {
        val sent = ArrayList<Pair<Int, Cmd>>()
        val link = object : Link { override val kind = Via.BLE; override var onState: ((Home?, BulbState) -> Unit)? = null
            override fun canReach(h: Home, b: Bulb) = true; override fun send(h: Home, b: Bulb, c: Cmd) { sent += b.meshId to c } }
        val h = Home(1, "H", "n", "p", emptyList(), mutableListOf(Bulb("Color", 1, Caps.ALL_CONTROLS, ""), Bulb("White", 2, 99999, ""), Bulb("Unknown", 0, Caps.ALL_CONTROLS, "")))
        val res = Schedules.run(Router(listOf(link)), h, Schedule(1, true, 7, 0, 127, "effect:5", 1, "all"))
        assertEquals(listOf(1 to Cmd.Effect(5)), sent); assertEquals(1, res.ok)
    }
}
