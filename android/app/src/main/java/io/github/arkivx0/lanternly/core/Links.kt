package io.github.arkivx0.lanternly.core

import java.security.SecureRandom
import java.util.concurrent.atomic.AtomicInteger

sealed class Cmd {
    data class Power(val on: Boolean) : Cmd()
    data class Brightness(val pct: Int) : Cmd()
    data class Temp(val ct: Int) : Cmd()   // 1 warm .. 100 cool (0 is invalid)
    data class Rgb(val r: Int, val g: Int, val b: Int) : Cmd()
    data class Identify(val start: Boolean) : Cmd()   // blink to find a bulb; no state change
}

interface Link {
    val kind: Via
    fun canReach(h: Home, b: Bulb): Boolean
    fun send(h: Home, b: Bulb, c: Cmd)
    var onState: ((Home?, BulbState) -> Unit)?
}

class NotReachable(msg: String) : Exception(msg)

/** Result of a multi-bulb operation, e.g. "3/5 bulbs updated". */
data class BatchResult(val ok: Int, val total: Int, val firstError: String?) {
    val allOk get() = ok == total
    override fun toString() = if (allOk) "$total/$total bulbs updated" else "$ok/$total bulbs updated" + (firstError?.let { " ($it)" } ?: "")
}

/** Diagnostics sink; the app routes it to logcat. Exception types only, never keys. */
object Diag { @Volatile var sink: ((String, Throwable?) -> Unit)? = null; fun log(where: String, e: Throwable? = null) { try { sink?.invoke(where, e) } catch (_: Throwable) { } } }

/** First link that can reach the bulb wins. Lanternly is offline-only: the link is the Bluetooth mesh. */
class Router(val links: List<Link>) {
    fun pick(h: Home, b: Bulb) = links.firstOrNull { it.canReach(h, b) }
    fun send(h: Home, b: Bulb, c: Cmd): Via {
        var last: Exception? = null
        for (l in links.filter { it.canReach(h, b) }) {
            try { l.send(h, b, c); b.via = l.kind; return l.kind } catch (e: Exception) { last = e }
        }
        b.via = Via.NONE
        throw last ?: NotReachable("${b.name} is not reachable")
    }
    /** Sends to each bulb; keeps going after failures and reports how many succeeded. */
    fun sendMany(h: Home, bulbs: List<Bulb>, c: Cmd): BatchResult {
        var ok = 0; var first: String? = null
        for (b in bulbs) try { send(h, b, c); ok++ } catch (e: Exception) { if (first == null) first = "${b.name}: ${e.message}"; Diag.log("send", e) }
        return BatchResult(ok, bulbs.size, first)
    }
}

object Scenes {
    class Scene(val name: String, val steps: List<Cmd>)
    val all = listOf(
        Scene("Bright", listOf(Cmd.Power(true), Cmd.Brightness(100), Cmd.Temp(70))),
        Scene("Relax", listOf(Cmd.Power(true), Cmd.Brightness(45), Cmd.Temp(10))),
        Scene("Focus", listOf(Cmd.Power(true), Cmd.Brightness(90), Cmd.Temp(100))),
        Scene("Night", listOf(Cmd.Power(true), Cmd.Brightness(8), Cmd.Temp(1))),
        Scene("Red", listOf(Cmd.Power(true), Cmd.Brightness(100), Cmd.Rgb(255, 0, 0))),
        Scene("Off", listOf(Cmd.Power(false))),
    )
    /** A bulb counts as updated only if every step reached it. */
    fun apply(r: Router, h: Home, s: Scene, only: List<Bulb> = h.bulbs): BatchResult {
        var ok = 0; var first: String? = null
        for (b in only) try { for (c in stepsFor(s, b)) r.send(h, b, c); ok++ } catch (e: Exception) { if (first == null) first = "${b.name}: ${e.message}"; Diag.log("scene", e) }
        return BatchResult(ok, only.size, first)
    }
    fun stepsFor(s: Scene, b: Bulb) = s.steps.filter { when (it) { is Cmd.Brightness -> b.has(Caps.DIM); is Cmd.Temp -> b.has(Caps.TEMP); is Cmd.Rgb -> b.has(Caps.COLOR); else -> true } }
}

/** Minimal GATT surface (Android BluetoothGatt or a test fake). Calls block until done. */
interface GattPort {
    val mac: String
    fun write(ch: java.util.UUID, data: ByteArray, withResponse: Boolean)
    fun read(ch: java.util.UUID): ByteArray
    fun subscribe(ch: java.util.UUID, onNotify: (ByteArray) -> Unit)
    fun close()
}

/** Logged-in Telink mesh session through one bulb; reaches every node of that mesh. */
class BleSession(private val g: GattPort, private val home: Home) : Link {
    override val kind = Via.BLE
    override var onState: ((Home?, BulbState) -> Unit)? = null
    @Volatile private var sk: ByteArray? = null
    private val mac = Ble.macLe(g.mac)
    private val seq = AtomicInteger(SecureRandom().nextInt(0xfff0) + 1)
    /** 24-bit packet sequence; never 0 (skipped on wrap). */
    private fun nextSeq(): Int { while (true) { val v = seq.incrementAndGet() and 0xffffff; if (v != 0) return v } }
    val loggedIn get() = sk != null

    fun login() {
        val rand = ByteArray(8).also { SecureRandom().nextBytes(it) }
        g.write(Ble.PAIR, Ble.pairRequest(home.meshName, home.meshPassword, rand), true)
        Thread.sleep(300)
        val key = Ble.sessionKey(home.meshName, home.meshPassword, rand, g.read(Ble.PAIR))
        sk = key
        g.subscribe(Ble.NOTIFY) { d -> Ble.parseStatus(Ble.decryptNotify(key, mac, d)).forEach { onState?.invoke(home, it) } }
        g.write(Ble.NOTIFY, byteArrayOf(1), true)
    }
    override fun canReach(h: Home, b: Bulb) = loggedIn && h === home
    override fun send(h: Home, b: Bulb, c: Cmd) {
        val (cmd, data) = when (c) {
            is Cmd.Power -> Ble.CMD_POWER to Ble.powerData(c.on)
            is Cmd.Brightness -> Ble.CMD_BRIGHTNESS to Ble.brightnessData(c.pct)
            is Cmd.Temp -> Ble.CMD_COLOR to Ble.colorTempData(c.ct)
            is Cmd.Rgb -> Ble.CMD_COLOR to Ble.rgbData(c.r, c.g, c.b)
            is Cmd.Identify -> Ble.CMD_IDENTIFY to Ble.identifyData(c.start)
        }
        val key = sk ?: throw NotReachable("BLE not logged in")
        g.write(Ble.COMMAND, Ble.command(key, mac, nextSeq(), b.meshId, cmd, data), false)
    }
    fun close() { sk = null; g.close() }

    companion object {
        /** Moves a factory-reset bulb onto our own mesh. Experimental (verified against a simulated node only). */
        fun provision(g: GattPort, meshName: String, meshPassword: String) {
            val rand = ByteArray(8).also { SecureRandom().nextBytes(it) }
            g.write(Ble.PAIR, Ble.pairRequest(Ble.FACTORY_NAME, Ble.FACTORY_PASSWORD, rand), true)
            Thread.sleep(300)
            val sk = Ble.sessionKey(Ble.FACTORY_NAME, Ble.FACTORY_PASSWORD, rand, g.read(Ble.PAIR))
            val ltk = ByteArray(16).also { SecureRandom().nextBytes(it) }
            for (w in Ble.provisionWrites(sk, meshName, meshPassword, ltk)) g.write(Ble.PAIR, w, true)
            Thread.sleep(300)
            val r = g.read(Ble.PAIR)
            if (r.isEmpty() || r[0].toInt() != Ble.PAIR_CONFIRMED) throw java.io.IOException("bulb did not confirm the new mesh key")
        }
    }
}
