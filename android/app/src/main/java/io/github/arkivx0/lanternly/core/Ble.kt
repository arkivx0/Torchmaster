package io.github.arkivx0.lanternly.core

import java.util.UUID
import javax.crypto.Cipher
import javax.crypto.spec.SecretKeySpec

/** Telink BLE mesh crypto and commands (C by GE / Cync). Mirrors windows/Lanternly.Core/Ble.cs. */
object Ble {
    val SERVICE: UUID = UUID.fromString("00010203-0405-0607-0809-0a0b0c0d1910")
    val NOTIFY: UUID = UUID.fromString("00010203-0405-0607-0809-0a0b0c0d1911")
    val COMMAND: UUID = UUID.fromString("00010203-0405-0607-0809-0a0b0c0d1912")
    val PAIR: UUID = UUID.fromString("00010203-0405-0607-0809-0a0b0c0d1914")
    const val VENDOR = 0x0211
    const val CMD_POWER = 0xd0; const val CMD_BRIGHTNESS = 0xd2; const val CMD_COLOR = 0xe2; const val CMD_STATUS = 0xdc; const val CMD_IDENTIFY = 0xf7; const val CMD_KICK_OUT = 0xe3

    fun aes(key: ByteArray, block: ByteArray): ByteArray {
        val c = Cipher.getInstance("AES/ECB/NoPadding")
        c.init(Cipher.ENCRYPT_MODE, SecretKeySpec(key.reversedArray(), "AES"))
        return c.doFinal(block.reversedArray()).reversedArray()
    }

    private fun pad16(s: String) = ByteArray(16).also { val e = s.toByteArray(); e.copyInto(it, 0, 0, minOf(16, e.size)) }
    private fun nameXorPass(n: String, p: String): ByteArray { val a = pad16(n); val b = pad16(p); for (i in 0 until 16) a[i] = (a[i].toInt() xor b[i].toInt()).toByte(); return a }

    fun pairRequest(name: String, pass: String, rand8: ByteArray): ByteArray {
        val data = ByteArray(16); rand8.copyInto(data, 0, 0, 8)
        return byteArrayOf(0x0c) + rand8.copyOf(8) + aes(data, nameXorPass(name, pass)).copyOf(8)
    }

    fun sessionKey(name: String, pass: String, rand8: ByteArray, reply: ByteArray): ByteArray {
        if (reply.size < 9 || reply[0] != 0x0d.toByte()) throw SecurityException("mesh login rejected (wrong mesh name/password?)")
        return aes(nameXorPass(name, pass), rand8.copyOf(8) + reply.copyOfRange(1, 9))
    }

    fun command(sk: ByteArray, macLe: ByteArray, seq: Int, target: Int, cmd: Int, data: ByteArray): ByteArray {
        val p = ByteArray(20)
        p[0] = seq.toByte(); p[1] = (seq shr 8).toByte(); p[2] = (seq shr 16).toByte()
        p[5] = target.toByte(); p[6] = (target shr 8).toByte(); p[7] = cmd.toByte(); p[8] = (VENDOR and 0xff).toByte(); p[9] = (VENDOR shr 8).toByte()
        data.copyInto(p, 10)
        return encryptPacket(sk, macLe, p)
    }

    fun encryptPacket(sk: ByteArray, mac: ByteArray, packet: ByteArray): ByteArray {
        val p = packet.copyOf()
        val nonce = byteArrayOf(mac[0], mac[1], mac[2], mac[3], 1, p[0], p[1], p[2], 15, 0, 0, 0, 0, 0, 0, 0)
        val auth = aes(sk, nonce)
        for (i in 0 until 15) auth[i] = (auth[i].toInt() xor p[i + 5].toInt()).toByte()
        val mic = aes(sk, auth); p[3] = mic[0]; p[4] = mic[1]
        val ks = aes(sk, byteArrayOf(0, mac[0], mac[1], mac[2], mac[3], 1, p[0], p[1], p[2], 0, 0, 0, 0, 0, 0, 0))
        for (i in 0 until 15) p[i + 5] = (p[i + 5].toInt() xor ks[i].toInt()).toByte()
        return p
    }

    fun decryptNotify(sk: ByteArray, mac: ByteArray, packet: ByteArray): ByteArray {
        val p = packet.copyOf()
        val ks = aes(sk, byteArrayOf(0, mac[0], mac[1], mac[2], p[0], p[1], p[2], p[3], p[4], 0, 0, 0, 0, 0, 0, 0))
        for (i in 0 until p.size - 7) p[i + 7] = (p[i + 7].toInt() xor ks[i].toInt()).toByte()
        return p
    }

    // ---- provisioning a factory-reset bulb ----
    const val FACTORY_NAME = "telink_mesh1"; const val FACTORY_PASSWORD = "123"
    const val PAIR_NAME = 4; const val PAIR_PASSWORD = 5; const val PAIR_LTK = 6; const val PAIR_CONFIRMED = 7
    fun provisionWrites(sk: ByteArray, name: String, pass: String, ltk: ByteArray): List<ByteArray> {
        fun w(op: Int, v: ByteArray) = byteArrayOf(op.toByte()) + aes(sk, v)
        return listOf(w(PAIR_NAME, pad16(name)), w(PAIR_PASSWORD, pad16(pass)), w(PAIR_LTK, ltk))
    }
    fun newMeshCredentials(): Pair<String, String> {
        val a = "abcdefghjkmnpqrstuvwxyz23456789"; val r = java.security.SecureRandom()
        fun s(n: Int) = String(CharArray(n) { a[r.nextInt(a.length)] })
        return ("ln" + s(10)) to s(16)
    }

    fun powerData(on: Boolean) = byteArrayOf(if (on) 1 else 0)
    fun brightnessData(pct: Int) = byteArrayOf(pct.coerceIn(1, 100).toByte())
    fun colorTempData(ct: Int) = byteArrayOf(0x05, ct.coerceIn(1, 100).toByte())
    fun rgbData(r: Int, g: Int, b: Int) = byteArrayOf(0x04, r.toByte(), g.toByte(), b.toByte())
    /** Identify (cmd 0xF7): 03 01 starts blinking, 03 02 stops. On the wire: F7 11 02 03 01/02. */
    fun identifyData(start: Boolean) = byteArrayOf(0x03, if (start) 1 else 2)
    /** Kick-out param 0x01: leave the mesh and return to factory defaults (telink_mesh1 / 123). */
    const val KICK_OUT_FACTORY_RESET = 0x01
    /** Factory reset (Telink kick-out, cmd 0xE3, data 01). On the wire: E3 11 02 01, as the official app's KickOutDeviceCommand. */
    fun kickOutData() = byteArrayOf(KICK_OUT_FACTORY_RESET.toByte())

    fun parseStatus(d: ByteArray): List<BulbState> {
        if (d.size < 18 || u(d, 7) != CMD_STATUS) return emptyList()
        val l = ArrayList<BulbState>()
        for (o in intArrayOf(10, 14)) {
            if (d[o + 1].toInt() == 0) continue
            var bri = u(d, o + 2); val rgb = bri >= 128; if (rgb) bri -= 128
            val c = u(d, o + 3)
            val col = if (rgb) Triple((c shr 5 and 7) * 255 / 7, (c shr 2 and 7) * 255 / 7, (c and 3) * 255 / 3) else Triple(0, 0, 0)
            l += BulbState(u(d, o), true, bri > 0, bri, if (rgb) 0xfe else c, col)
        }
        return l
    }

    private fun u(b: ByteArray, i: Int) = b[i].toInt() and 0xff

    fun macLe(mac: String): ByteArray {
        val h = mac.replace(":", "").replace("-", "")
        require(h.length == 12) { "bad MAC" }
        return ByteArray(6) { h.substring(it * 2, it * 2 + 2).toInt(16).toByte() }.reversedArray()
    }
}
