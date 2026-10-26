package io.github.arkivx0.lanternly

import android.Manifest
import android.app.AlarmManager
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import io.github.arkivx0.lanternly.core.*
import java.text.DateFormat
import java.time.ZonedDateTime
import java.util.Date

/**
 * Schedules run from AlarmManager on this phone: one alarm per enabled schedule, exact when allowed
 * (USE_EXACT_ALARM on Android 13+, SCHEDULE_EXACT_ALARM on 12), otherwise an inexact allow-while-idle alarm.
 * Re-planned after boot, time or time zone changes, app updates and any edit.
 */
object Alarms {
    const val ACTION_FIRE = "io.github.arkivx0.lanternly.SCHEDULE_FIRE"
    const val EXTRA_ID = "id"; const val EXTRA_DUE = "due"

    private fun am(ctx: Context) = ctx.getSystemService(AlarmManager::class.java)

    /** True when exact alarms may be set (always below Android 12). */
    fun exactAllowed(ctx: Context) = Build.VERSION.SDK_INT < 31 || am(ctx).canScheduleExactAlarms()

    private fun pi(ctx: Context, id: Long, due: Long, flags: Int): PendingIntent? {
        val i = Intent(ctx, ScheduleReceiver::class.java).setAction(ACTION_FIRE).putExtra(EXTRA_ID, id).putExtra(EXTRA_DUE, due)
        return PendingIntent.getBroadcast(ctx, (id xor (id ushr 32)).toInt(), i, flags or PendingIntent.FLAG_IMMUTABLE)
    }

    fun cancel(ctx: Context, id: Long) { pi(ctx, id, 0, PendingIntent.FLAG_NO_CREATE)?.let { am(ctx).cancel(it); it.cancel() } }

    /** Sets (or clears) the alarm of one schedule, from now. */
    fun plan(ctx: Context, s: Schedule) {
        cancel(ctx, s.id)
        if (!s.enabled) return
        val t = Schedules.nextFire(s, ZonedDateTime.now())?.toInstant()?.toEpochMilli() ?: return
        val p = pi(ctx, s.id, t, PendingIntent.FLAG_UPDATE_CURRENT) ?: return
        val m = am(ctx)
        try {
            if (exactAllowed(ctx)) m.setExactAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, t, p)
            else m.setAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, t, p)
        } catch (e: SecurityException) { // permission revoked between the check and the call
            Diag.log("exact alarm", e); m.setAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, t, p)
        }
    }

    fun planAll(ctx: Context, l: List<Schedule>) = l.forEach { plan(ctx, it) }
    fun cancelAll(ctx: Context, l: List<Schedule>) = l.forEach { cancel(ctx, it.id) }
}

/** Boot, clock/time zone change, app update, exact-alarm permission change: plan every alarm again. */
class SystemEventsReceiver : BroadcastReceiver() {
    override fun onReceive(ctx: Context, intent: Intent) {
        when (intent.action) {
            Intent.ACTION_BOOT_COMPLETED, Intent.ACTION_TIME_CHANGED, Intent.ACTION_TIMEZONE_CHANGED,
            Intent.ACTION_MY_PACKAGE_REPLACED, AlarmManager.ACTION_SCHEDULE_EXACT_ALARM_PERMISSION_STATE_CHANGED -> {
                val r = goAsync()
                Thread { try { Alarms.planAll(ctx, Store(ctx).loadSchedules()) } catch (e: Exception) { Diag.log("replan", e) } finally { r.finish() } }.start()
            }
        }
    }
}

/**
 * One schedule is due: connect to a nearby bulb over Bluetooth, send the command through the mesh, disconnect,
 * then set the next alarm. Skipped (not run late) when more than [Schedules.GRACE_MS] late.
 */
class ScheduleReceiver : BroadcastReceiver() {
    override fun onReceive(ctx: Context, intent: Intent) {
        if (intent.action != Alarms.ACTION_FIRE) return
        val id = intent.getLongExtra(Alarms.EXTRA_ID, 0); val due = intent.getLongExtra(Alarms.EXTRA_DUE, 0)
        val r = goAsync()
        Thread {
            try { fire(ctx.applicationContext, id, due) } catch (e: Exception) { Diag.log("schedule", e) } finally { r.finish() }
        }.start()
    }

    private fun fire(ctx: Context, id: Long, due: Long) {
        val store = Store(ctx)
        val s = store.loadSchedules().firstOrNull { it.id == id } ?: return
        Alarms.plan(ctx, s) // next occurrence first, so a failure below never stops the series
        if (!s.enabled) return
        val label = "%02d:%02d %s".format(s.hour, s.minute, Schedules.actionText(s.action))
        fun done(msg: String) { store.lastRun = "${DateFormat.getDateTimeInstance(DateFormat.SHORT, DateFormat.SHORT).format(Date())} · $label: $msg" }
        if (!Schedules.onTime(due, System.currentTimeMillis())) { done("skipped (more than 10 minutes late)"); return }
        if (Build.VERSION.SDK_INT >= 31 && ctx.checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) != PackageManager.PERMISSION_GRANTED) { done("Bluetooth permission missing"); return }
        if (!store.ble) { done("Bluetooth is off in Lanternly settings"); return }
        val vault = store.loadQuiet() ?: run { done("stored keys unreadable"); return }
        val h = vault.homes.firstOrNull { it.id == s.homeId } ?: run { done("its home was removed"); return }
        val targets = Schedules.targets(h, s)
        if (targets.isEmpty()) { done("no bulbs to control"); return }
        // Try target bulbs first (closest to what we control), then the rest; stay well inside the receiver time limit.
        val macs = (targets + h.bulbs).map { it.mac.uppercase() }.filter { it.length == 17 }.distinct()
        val deadline = System.currentTimeMillis() + 40_000
        var last: String? = null
        for (mac in macs.take(3)) {
            if (System.currentTimeMillis() > deadline) break
            var ses: BleSession? = null
            try {
                ses = BleSession(AndroidGatt.connect(ctx, mac), h)
                ses.login()
                val res = Schedules.run(Router(listOf(ses)), h, s)
                Thread.sleep(400) // let the last unacknowledged write leave the radio before disconnecting
                done(res.toString()); return
            } catch (e: Exception) { Diag.log("schedule connect", e); last = e.message } finally { ses?.close() }
        }
        done("not reachable" + (last?.let { " ($it)" } ?: "") + ". Is the phone near the bulbs with Bluetooth on?")
    }
}
