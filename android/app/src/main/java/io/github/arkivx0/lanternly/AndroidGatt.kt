package io.github.arkivx0.lanternly

import android.annotation.SuppressLint
import android.bluetooth.*
import android.content.Context
import android.os.Build
import io.github.arkivx0.lanternly.core.GattPort
import java.util.UUID
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit

/** Blocking GattPort over BluetoothGatt. All calls must run off the main thread. */
@SuppressLint("MissingPermission")
class AndroidGatt private constructor(override val mac: String) : GattPort {
    private lateinit var gatt: BluetoothGatt
    private val events = LinkedBlockingQueue<Pair<String, Any?>>()
    private val notify = HashMap<UUID, (ByteArray) -> Unit>()
    private var service: BluetoothGattService? = null
    /** Absolute time (ms) after which every wait fails at once; schedule runs pass their overall deadline here. */
    @Volatile var deadline = Long.MAX_VALUE

    private val cb = object : BluetoothGattCallback() {
        override fun onConnectionStateChange(g: BluetoothGatt, status: Int, newState: Int) {
            events.put((if (newState == BluetoothProfile.STATE_CONNECTED && status == BluetoothGatt.GATT_SUCCESS) "up" else "down") to status)
        }
        override fun onServicesDiscovered(g: BluetoothGatt, status: Int) { events.put("svc" to status) }
        override fun onCharacteristicWrite(g: BluetoothGatt, c: BluetoothGattCharacteristic, status: Int) { events.put("w" to status) }
        @Deprecated("pre-33") override fun onCharacteristicRead(g: BluetoothGatt, c: BluetoothGattCharacteristic, status: Int) { events.put("r" to (if (status == 0) c.value else null)) }
        override fun onCharacteristicRead(g: BluetoothGatt, c: BluetoothGattCharacteristic, value: ByteArray, status: Int) { events.put("r" to (if (status == 0) value else null)) }
        @Deprecated("pre-33") override fun onCharacteristicChanged(g: BluetoothGatt, c: BluetoothGattCharacteristic) { notify[c.uuid]?.invoke(c.value) }
        override fun onCharacteristicChanged(g: BluetoothGatt, c: BluetoothGattCharacteristic, value: ByteArray) { notify[c.uuid]?.invoke(value) }
        override fun onDescriptorWrite(g: BluetoothGatt, d: BluetoothGattDescriptor, status: Int) { events.put("d" to status) }
    }

    private fun await(tag: String, ms: Long = 8000): Any? {
        val end = minOf(System.currentTimeMillis() + ms, deadline)
        if (end <= System.currentTimeMillis()) throw java.io.IOException("Bluetooth timeout ($tag, out of time)")
        while (true) {
            val e = events.poll(maxOf(1, end - System.currentTimeMillis()), TimeUnit.MILLISECONDS) ?: throw java.io.IOException("Bluetooth timeout ($tag)")
            if (e.first == "down") throw java.io.IOException("Bluetooth disconnected")
            if (e.first == tag) return e.second
        }
    }

    private fun ch(id: UUID) = service?.getCharacteristic(id) ?: throw java.io.IOException("characteristic missing")

    @Synchronized override fun write(ch: UUID, data: ByteArray, withResponse: Boolean) {
        val c = ch(ch); val type = if (withResponse) BluetoothGattCharacteristic.WRITE_TYPE_DEFAULT else BluetoothGattCharacteristic.WRITE_TYPE_NO_RESPONSE
        val ok = if (Build.VERSION.SDK_INT >= 33) gatt.writeCharacteristic(c, data, type) == BluetoothStatusCodes.SUCCESS
                 else { @Suppress("DEPRECATION") run { c.writeType = type; c.value = data; gatt.writeCharacteristic(c) } }
        if (!ok) throw java.io.IOException("Bluetooth write refused")
        if (await("w") != 0) throw java.io.IOException("Bluetooth write failed")
    }
    @Synchronized override fun read(ch: UUID): ByteArray {
        if (!gatt.readCharacteristic(ch(ch))) throw java.io.IOException("Bluetooth read refused")
        return await("r") as? ByteArray ?: throw java.io.IOException("Bluetooth read failed")
    }
    @Synchronized override fun subscribe(ch: UUID, onNotify: (ByteArray) -> Unit) {
        val c = ch(ch); notify[ch] = onNotify; gatt.setCharacteristicNotification(c, true)
        val d = c.getDescriptor(UUID.fromString("00002902-0000-1000-8000-00805f9b34fb")) ?: return
        val ok = if (Build.VERSION.SDK_INT >= 33) gatt.writeDescriptor(d, BluetoothGattDescriptor.ENABLE_NOTIFICATION_VALUE) == BluetoothStatusCodes.SUCCESS
                 else { @Suppress("DEPRECATION") run { d.value = BluetoothGattDescriptor.ENABLE_NOTIFICATION_VALUE; gatt.writeDescriptor(d) } }
        if (ok) try { await("d", 3000) } catch (_: Exception) {}
    }
    override fun close() { try { gatt.disconnect(); gatt.close() } catch (_: Exception) {} }

    companion object {
        /** Scans for factory-reset bulbs (local name "telink_mesh1"). Blocking. */
        fun findResetBulbs(ctx: Context, ms: Long): List<String> {
            val a = (ctx.getSystemService(Context.BLUETOOTH_SERVICE) as BluetoothManager).adapter ?: throw java.io.IOException("no Bluetooth adapter")
            val sc = a.bluetoothLeScanner ?: throw java.io.IOException("Bluetooth is off")
            val seen = HashMap<String, Int>()
            val cb = object : android.bluetooth.le.ScanCallback() {
                override fun onScanResult(type: Int, r: android.bluetooth.le.ScanResult) {
                    if (r.scanRecord?.deviceName == io.github.arkivx0.lanternly.core.Ble.FACTORY_NAME) synchronized(seen) { seen[r.device.address] = r.rssi }
                }
            }
            sc.startScan(cb); Thread.sleep(ms); sc.stopScan(cb)
            return synchronized(seen) { seen.entries.sortedByDescending { it.value }.map { it.key } }
        }

        /** Connects and discovers the mesh service. Every wait (and later write/read) is capped by [deadline] (epoch ms). */
        fun connect(ctx: Context, mac: String, deadline: Long = Long.MAX_VALUE): AndroidGatt {
            val a = (ctx.getSystemService(Context.BLUETOOTH_SERVICE) as BluetoothManager).adapter ?: throw java.io.IOException("no Bluetooth adapter")
            if (!a.isEnabled) throw java.io.IOException("Bluetooth is off")
            val g = AndroidGatt(mac.uppercase()).also { it.deadline = deadline }
            g.gatt = a.getRemoteDevice(mac.uppercase()).connectGatt(ctx, false, g.cb, BluetoothDevice.TRANSPORT_LE)
            try {
                g.await("up", 12000)
                g.gatt.discoverServices(); g.await("svc", 10000)
                g.service = g.gatt.getService(io.github.arkivx0.lanternly.core.Ble.SERVICE) ?: throw java.io.IOException("mesh service not found")
                return g
            } catch (e: Exception) { g.close(); throw e }
        }
    }
}
