package io.github.arkivx0.lanternly

import android.Manifest
import android.app.AlarmManager
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.content.pm.ServiceInfo
import android.net.Uri
import android.os.Build
import android.os.IBinder
import io.github.arkivx0.lanternly.core.*
import java.text.DateFormat
import java.time.ZonedDateTime
import java.util.Date
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.TimeoutException

/**
 * Schedules run from AlarmManager on this phone: one alarm per enabled schedule, exact when allowed
 * (USE_EXACT_ALARM on Android 13+, SCHEDULE_EXACT_ALARM on 12), otherwise an inexact allow-while-idle alarm.
 * Re-planned after boot, time or time zone changes, app updates and any edit. When a run reaches no bulb, up to
 * two retry alarms are set inside the missed-run grace window.
 */
object Alarms {
    const val ACTION_FIRE = "io.github.arkivx0.lanternly.SCHEDULE_FIRE"
    const val EXTRA_ID = "id"; const val EXTRA_DUE = "due"; const val EXTRA_ATTEMPT = "attempt"

    private fun am(ctx: Context) = ctx.getSystemService(AlarmManager::class.java)

    /** True when exact alarms may be set (always below Android 12). */
    fun exactAllowed(ctx: Context) = Build.VERSION.SDK_INT < 31 || am(ctx).canScheduleExactAlarms()

    /** Regular alarm: request code slot*2; retry alarm: slot*2+1 and its own data Uri, so a retry never replaces the next regular alarm. */
    private fun pi(ctx: Context, id: Long, due: Long, attempt: Int, flags: Int): PendingIntent? {
        val retry = attempt > 0
        val i = Intent(ctx, ScheduleReceiver::class.java).setAction(ACTION_FIRE)
            .setData(Uri.parse("lanternly://schedule/$id" + if (retry) "/retry" else ""))
            .putExtra(EXTRA_ID, id).putExtra(EXTRA_DUE, due).putExtra(EXTRA_ATTEMPT, attempt)
        val code = Store(ctx).alarmSlot(id) * 2 + if (retry) 1 else 0
        return PendingIntent.getBroadcast(ctx, code, i, flags or PendingIntent.FLAG_IMMUTABLE)
    }

    private fun set(ctx: Context, t: Long, p: PendingIntent) {
        val m = am(ctx)
        try {
            if (exactAllowed(ctx)) m.setExactAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, t, p)
            else m.setAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, t, p)
        } catch (e: SecurityException) { // permission revoked between the check and the call
            Diag.log("exact alarm", e); m.setAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, t, p)
        }
    }

    private fun cancelOne(ctx: Context, id: Long, retry: Boolean) {
        pi(ctx, id, 0, if (retry) 1 else 0, PendingIntent.FLAG_NO_CREATE)?.let { am(ctx).cancel(it); it.cancel() }
    }

    /** Cancels the regular and the retry alarm of a schedule. */
    fun cancel(ctx: Context, id: Long) { cancelOne(ctx, id, false); cancelOne(ctx, id, true) }

    /** Cancels and frees the alarm slot of a deleted schedule. */
    fun remove(ctx: Context, id: Long) { cancel(ctx, id); Store(ctx).releaseSlot(id) }

    /** Sets (or clears) the regular alarm of one schedule: next time after now and after the last run that already fired. */
    fun plan(ctx: Context, s: Schedule) {
        cancelOne(ctx, s.id, false)
        if (!s.enabled) { cancelOne(ctx, s.id, true); return }
        val t = Schedules.nextFireAfter(s, ZonedDateTime.now(), Store(ctx).lastFired(s.id))?.toInstant()?.toEpochMilli() ?: return
        set(ctx, t, pi(ctx, s.id, t, 0, PendingIntent.FLAG_UPDATE_CURRENT) ?: return)
    }

    /** Sets retry number [attempt] for the run due at [due]; returns its time, or null if none is left inside the grace window. */
    fun planRetry(ctx: Context, s: Schedule, due: Long, attempt: Int): Long? {
        val t = Schedules.retryAt(due, attempt, System.currentTimeMillis()) ?: return null
        set(ctx, t, pi(ctx, s.id, due, attempt, PendingIntent.FLAG_UPDATE_CURRENT) ?: return null)
        return t
    }

    /** Plans every schedule; one bad entry never stops the others. Returns how many failed. Never throws. */
    fun planAll(ctx: Context, l: List<Schedule>): Int {
        var failed = 0
        for (s in l) try { plan(ctx, s) } catch (e: Exception) { failed++; Diag.log("plan schedule", e) }
        return failed
    }
    fun cancelAll(ctx: Context, l: List<Schedule>) = l.forEach { try { cancel(ctx, it.id) } catch (e: Exception) { Diag.log("cancel schedule", e) } }
}

/** The app's open Bluetooth session, shared with schedule runs so they don't need a second connection (many bulbs allow one). */
object Connection {
    @Volatile var session: BleSession? = null
    fun shared(homeId: Long): BleSession? = session?.takeIf { it.loggedIn && it.home.id == homeId }
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
 * A schedule is due. The Bluetooth work runs in [ScheduleService] (a short connectedDevice foreground service), never in
 * the receiver. If the system refuses to start it (possible for an inexact alarm on Android 12+), a short run happens
 * here instead, capped at [ScheduleJob.RECEIVER_BUDGET_MS].
 */
class ScheduleReceiver : BroadcastReceiver() {
    override fun onReceive(ctx: Context, intent: Intent) {
        if (intent.action != Alarms.ACTION_FIRE) return
        val app = ctx.applicationContext
        try {
            app.startForegroundService(Intent(app, ScheduleService::class.java).putExtras(intent))
        } catch (e: Exception) {
            Diag.log("schedule service start", e)
            val r = goAsync()
            Thread { try { ScheduleJob.run(app, intent, ScheduleJob.RECEIVER_BUDGET_MS) } catch (x: Exception) { Diag.log("schedule", x) } finally { r.finish() } }.start()
        }
    }
}

/** Foreground service (type connectedDevice) for schedule runs: one at a time, each with a hard overall timeout. */
class ScheduleService : Service() {
    private val queue = Executors.newSingleThreadExecutor()
    private val worker = Executors.newCachedThreadPool()

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        try { goForeground() } catch (e: Exception) { // e.g. Bluetooth permission revoked (Android 14 requires it for this type)
            Diag.log("schedule foreground", e)
            intent?.let { ScheduleJob.failed(applicationContext, it, "could not start (${e.javaClass.simpleName}); Bluetooth permission?") }
            stopSelf(startId); return START_NOT_STICKY
        }
        val i = intent ?: run { stopSelf(startId); return START_NOT_STICKY }
        queue.execute {
            val f = worker.submit { ScheduleJob.run(applicationContext, i, ScheduleJob.SERVICE_BUDGET_MS) }
            try { f.get(ScheduleJob.SERVICE_BUDGET_MS + 5_000, TimeUnit.MILLISECONDS) }
            catch (e: TimeoutException) { f.cancel(true); ScheduleJob.failed(applicationContext, i, "timed out") }
            catch (e: Exception) { Diag.log("schedule", e) }
            finally { stopSelf(startId) }
        }
        return START_NOT_STICKY
    }

    private fun goForeground() {
        val nm = getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(NotificationChannel(CHANNEL, "Schedule runs", NotificationManager.IMPORTANCE_MIN).apply { setShowBadge(false) })
        val n = Notification.Builder(this, CHANNEL).setSmallIcon(android.R.drawable.stat_sys_data_bluetooth)
            .setContentTitle("Running a schedule").setContentText("Sending to your bulbs over Bluetooth").setOngoing(true).build()
        startForeground(NOTE_ID, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE)
    }

    override fun onDestroy() { queue.shutdown(); worker.shutdown(); super.onDestroy() }

    companion object { const val CHANNEL = "schedule-runs"; const val NOTE_ID = 1 }
}

/**
 * One schedule run: dedupe (clock set back), grace check, "running…" note, then the shared session if the app is
 * connected, else up to 3 bulbs, all inside one deadline that is also passed into connect and every GATT wait.
 * Nothing reached: a retry alarm inside the grace window.
 */
object ScheduleJob {
    const val SERVICE_BUDGET_MS = 40_000L
    const val RECEIVER_BUDGET_MS = 8_000L

    private fun label(s: Schedule) = "%02d:%02d %s".format(s.hour, s.minute, Schedules.actionText(s.action))
    private fun note(store: Store, s: Schedule, msg: String) {
        store.lastRun = "${DateFormat.getDateTimeInstance(DateFormat.SHORT, DateFormat.SHORT).format(Date())} · ${label(s)}: $msg"
    }
    private fun retryText(t: Long?) = t?.let { " · retrying at " + DateFormat.getTimeInstance(DateFormat.SHORT).format(Date(it)) } ?: ""

    /** Records a run that was cut off from outside (timeout, service refused) and sets a retry if one is left. */
    fun failed(ctx: Context, intent: Intent, why: String) {
        try {
            val id = intent.getLongExtra(Alarms.EXTRA_ID, 0); val due = intent.getLongExtra(Alarms.EXTRA_DUE, 0); val attempt = intent.getIntExtra(Alarms.EXTRA_ATTEMPT, 0)
            val store = Store(ctx); val s = store.loadSchedules().firstOrNull { it.id == id } ?: return
            if (attempt == 0) { Alarms.plan(ctx, s); if (!Schedules.isReplay(due, store.lastFired(id))) store.setLastFired(id, due) }
            note(store, s, why + retryText(Alarms.planRetry(ctx, s, due, attempt + 1)))
        } catch (e: Exception) { Diag.log("schedule failed note", e) }
    }

    fun run(ctx: Context, intent: Intent, budgetMs: Long) {
        val start = System.currentTimeMillis(); val deadline = start + budgetMs
        val id = intent.getLongExtra(Alarms.EXTRA_ID, 0); val due = intent.getLongExtra(Alarms.EXTRA_DUE, 0); val attempt = intent.getIntExtra(Alarms.EXTRA_ATTEMPT, 0)
        val store = Store(ctx)
        val s = store.loadSchedules().firstOrNull { it.id == id } ?: return
        if (attempt == 0) {
            if (Schedules.isReplay(due, store.lastFired(id))) { Diag.log("schedule replay skipped (clock set back)"); Alarms.plan(ctx, s); return }
            store.setLastFired(id, due)
            Alarms.plan(ctx, s) // next occurrence first, so a failure below never stops the series
        } else if (store.lastFired(id) != due) return // stale retry (schedule edited or a newer run started)
        if (!s.enabled) return
        if (!Schedules.onTime(due, start)) { note(store, s, "skipped (more than ${Schedules.GRACE_MS / 60_000} minutes late)"); return }
        note(store, s, if (attempt > 0) "running… (retry $attempt)" else "running…") // visible even if the process is killed
        if (Build.VERSION.SDK_INT >= 31 && ctx.checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) != PackageManager.PERMISSION_GRANTED) { note(store, s, "Bluetooth permission missing"); return }
        if (!store.ble) { note(store, s, "Bluetooth is off in Lanternly settings"); return }
        val vault = store.loadQuiet() ?: run { note(store, s, "stored keys unreadable"); return }
        val h = vault.homes.firstOrNull { it.id == s.homeId } ?: run { note(store, s, "its home was removed"); return }
        if (Schedules.targets(h, s).isEmpty()) { note(store, s, "no bulbs to control"); return }
        var last: String? = null
        // 1. The app's own open session, if it is logged in to this home.
        Connection.shared(h.id)?.let { ses ->
            try {
                val res = Schedules.run(Router(listOf(ses)), ses.home, s)
                if (res.ok > 0) { note(store, s, "$res (app connection)"); return }
                last = res.firstError
            } catch (e: Exception) { Diag.log("schedule shared", e); last = e.message }
        }
        // 2. Own connection: target bulbs first (closest to what we control), then the rest.
        val macs = (Schedules.targets(h, s) + h.bulbs).map { it.mac.uppercase() }.filter { it.length == 17 }.distinct()
        for (mac in macs.take(3)) {
            if (System.currentTimeMillis() > deadline - 2_000) break
            var ses: BleSession? = null
            try {
                ses = BleSession(AndroidGatt.connect(ctx, mac, deadline), h)
                ses.login()
                val res = Schedules.run(Router(listOf(ses)), h, s)
                Thread.sleep(400) // let the last unacknowledged write leave the radio before disconnecting
                if (res.ok > 0) { note(store, s, res.toString()); return }
                last = res.firstError
            } catch (e: InterruptedException) { last = "timed out"; break
            } catch (e: Exception) { Diag.log("schedule connect", e); last = e.message } finally { ses?.close() }
        }
        val retry = Alarms.planRetry(ctx, s, due, attempt + 1)
        note(store, s, "not reachable" + (last?.let { " ($it)" } ?: "") + retryText(retry) + (if (retry == null) ". Is the phone near the bulbs with Bluetooth on?" else ""))
    }
}
