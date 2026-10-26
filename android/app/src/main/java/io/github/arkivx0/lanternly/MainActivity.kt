package io.github.arkivx0.lanternly

import android.Manifest
import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Color
import android.graphics.drawable.GradientDrawable
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.text.InputType
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.*
import io.github.arkivx0.lanternly.core.*
import java.util.concurrent.Executors

class MainActivity : Activity() {
    companion object {
        const val BG = 0xFF1A1B26.toInt(); const val CARD = 0xFF24283B.toInt(); const val CTL = 0xFF2F3549.toInt()
        const val FG = 0xFFC0CAF5.toInt(); const val DIM = 0xFF7982A9.toInt(); const val ACCENT = 0xFF7AA2F7.toInt()
        const val GREEN = 0xFF9ECE6A.toInt(); const val YELLOW = 0xFFE0AF68.toInt()
        const val REQ_IMPORT = 1; const val REQ_EXPORT = 2; const val REQ_PERMS = 3
        val SWATCHES = intArrayOf(0xFFF7768E.toInt(), 0xFFFF9E64.toInt(), 0xFFE0AF68.toInt(), 0xFF9ECE6A.toInt(), 0xFF73DACA.toInt(), 0xFF7AA2F7.toInt(), 0xFFBB9AF7.toInt())
    }

    private lateinit var store: Store
    private var vault = Vault()
    private val io = Executors.newSingleThreadExecutor()
    private val ui = Handler(Looper.getMainLooper())
    private var router = Router(emptyList())
    private var ble: BleSession? = null
    private var exportPass: String? = null
    private lateinit var status: TextView
    private lateinit var body: LinearLayout
    private var settingsOpen = false
    private val refreshers = ArrayList<() -> Unit>()
    private var demo = false

    private val home: Home? get() = vault.homes.getOrNull(store.homeIndex.coerceIn(0, maxOf(0, vault.homes.size - 1)))
    private fun dp(v: Int) = (v * resources.displayMetrics.density).toInt()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        store = Store(this)
        demo = intent?.getBooleanExtra("demo", false) == true
        vault = if (demo) demoVault() else store.load()
        Diag.sink = { where, e -> android.util.Log.w("Lanternly", where + (e?.let { ": " + it.javaClass.simpleName } ?: "")) }
        window.statusBarColor = BG; window.navigationBarColor = BG
        val root = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setBackgroundColor(BG); fitsSystemWindows = true }
        val top = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL; setPadding(dp(16), dp(12), dp(12), dp(6)) }
        val titles = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        titles.addView(text("Lanternly", 20f, FG, bold = true))
        status = text("", 12f, DIM); titles.addView(status)
        top.addView(titles, LinearLayout.LayoutParams(0, -2, 1f))
        top.addView(button("⚙") { settingsOpen = !settingsOpen; render() })
        top.addView(button("✕") { finishAndRemoveTask() }.apply { (layoutParams as? ViewGroup.MarginLayoutParams)?.leftMargin = dp(6) })
        root.addView(top)
        body = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(dp(12), 0, dp(12), dp(16)) }
        root.addView(ScrollView(this).apply { addView(body) }, LinearLayout.LayoutParams(-1, 0, 1f))
        setContentView(root)
        render()
        store.quarantined?.let { q ->
            AlertDialog.Builder(this).setTitle("Stored keys unreadable")
                .setMessage("Lanternly could not read its stored keys, so it moved them aside ($q). Nothing will be saved until you import a key file or choose Forget everything. Your bulbs are not affected.")
                .setPositiveButton("OK", null).show()
        }
        if (!demo) ensurePermsThenConnect() else { router = Router(listOf(DemoLink())); setStatus("Demo · sample data") ; vault.homes.flatMap { it.bulbs }.forEach { it.via = Via.BLE }; render() }
    }

    override fun onDestroy() { super.onDestroy(); teardown(); io.shutdown() }

    // ---------- connections ----------
    private fun ensurePermsThenConnect() {
        val need = if (Build.VERSION.SDK_INT >= 31) arrayOf(Manifest.permission.BLUETOOTH_CONNECT, Manifest.permission.BLUETOOTH_SCAN) else arrayOf(Manifest.permission.ACCESS_FINE_LOCATION)
        val missing = need.filter { checkSelfPermission(it) != PackageManager.PERMISSION_GRANTED }
        if (store.ble && missing.isNotEmpty()) requestPermissions(missing.toTypedArray(), REQ_PERMS) else connect()
    }
    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) { connect() }

    private fun teardown() { ble?.close(); ble = null }

    /** Saves the vault; false (with a visible reason) when saving is blocked after a vault read failure. */
    private fun save(): Boolean = try { if (!demo) store.save(vault); true } catch (e: Exception) {
        Diag.log("vault save", e); post { setStatus("Not saved: ${e.message}") }; false
    }

    private fun connect() {
        val h = home ?: run { router = Router(emptyList()); setStatus("No bulbs yet: tap ⚙ to add a reset bulb or import keys"); return }
        io.execute {
            teardown()
            val links = ArrayList<Link>()
            if (store.ble) {
                val macs = h.bulbs.map { it.mac.uppercase() }.filter { it.length == 17 }.distinct()
                for (mac in macs.take(5)) {
                    post { setStatus("Bluetooth: trying ${h.bulbs.first { it.mac.equals(mac, true) }.name}…") }
                    try {
                        val s = BleSession(AndroidGatt.connect(this, mac), h)
                        s.onState = { _, st -> onState(h, st) } // before login: the first status notifications arrive during it
                        try { s.login() } catch (e: Exception) { s.close(); throw e }
                        ble = s; links += s; break
                    } catch (e: Exception) { Diag.log("ble connect", e); post { setStatus("Bluetooth: ${e.message}") } }
                }
            }
            router = Router(links)
            h.bulbs.forEach { it.via = router.pick(h, it)?.kind ?: Via.NONE }
            post { updateStatus(); refresh() }
        }
    }

    private fun updateStatus() {
        val p = ArrayList<String>()
        if (store.ble) p += if (ble?.loggedIn == true) "BLE ✓" else "BLE –"
        var s = if (p.isEmpty()) "Bluetooth is off" else p.joinToString(" · ") + " · offline only"
        if (store.quarantined != null) s = "⚠ Stored keys unreadable, saving blocked · $s"
        else if (!store.backedUp && !demo) s = "⚠ Export a key file backup (⚙) · $s"
        setStatus(s)
    }
    private fun setStatus(s: String) { status.text = s }
    private fun post(f: () -> Unit) = ui.post(f)

    private fun onState(h: Home, s: BulbState) { h.bulbs.filter { it.meshId == s.meshId }.forEach { it.state = s }; post { refresh() } }

    private fun send(h: Home, bulbs: List<Bulb>, c: Cmd) {
        io.execute {
            var err: String? = null; var ok = 0
            for (b in bulbs) try {
                router.send(h, b, c)
                val st = b.state ?: BulbState(b.meshId, true, false, 50, 50, Triple(0, 0, 0))
                b.state = when (c) { is Cmd.Power -> st.copy(on = c.on); is Cmd.Brightness -> st.copy(brightness = c.pct, on = c.pct > 0); is Cmd.Temp -> st.copy(mode = c.ct); is Cmd.Rgb -> st.copy(mode = 0xfe, rgb = Triple(c.r, c.g, c.b)); is Cmd.Identify -> st }
                ok++
            } catch (e: Exception) { if (err == null) err = "${b.name}: ${e.message}"; Diag.log("send", e) }
            val r = BatchResult(ok, bulbs.size, err)
            post { if (!r.allOk || r.total > 1) setStatus(r.toString()); refresh() }
        }
    }
    private fun scene(h: Home, s: Scenes.Scene) = io.execute { val r = Scenes.apply(router, h, s); post { setStatus(r.toString()); refresh() } }

    // ---------- UI ----------
    private fun refresh() = refreshers.forEach { it() }

    private fun render() {
        body.removeAllViews(); refreshers.clear()
        val h = home
        when { settingsOpen -> settings(); h == null -> empty(); else -> homeView(h) }
    }

    private fun empty() {
        body.addView(text("No bulbs yet", 18f, FG, bold = true).apply { gravity = Gravity.CENTER; setPadding(0, dp(40), 0, dp(8)) }, -1, -2)
        body.addView(text("Lanternly works only over Bluetooth and never contacts GE or any other server.\nTwo ways to start (open settings):\n• Add a reset bulb: bulbs already paired with the Cync app must be factory reset first. A reset loses the current color; set it again right after adding.\n• Import a Lanternly key file from another phone or PC.", 13f, DIM).apply { gravity = Gravity.CENTER }, -1, -2)
        body.addView(button("Open settings", accent = true) { settingsOpen = true; render() }, LinearLayout.LayoutParams(-2, -2).apply { gravity = Gravity.CENTER_HORIZONTAL; topMargin = dp(16) })
    }

    private fun homeView(h: Home) {
        if (vault.homes.size > 1) {
            val row = LinearLayout(this)
            vault.homes.forEachIndexed { i, x -> row.addView(chip(x.name, i == store.homeIndex) { store.homeIndex = i; render(); connect() }) }
            body.addView(HorizontalScrollView(this).apply { addView(row) })
        }
        val sc = LinearLayout(this).apply { setPadding(0, dp(4), 0, dp(8)) }
        Scenes.all.forEach { s -> sc.addView(chip(s.name, false) { scene(h, s) }) }
        body.addView(HorizontalScrollView(this).apply { isHorizontalScrollBarEnabled = false; addView(sc) })
        val assigned = HashSet<Bulb>()
        for (r in h.rooms) { val bs = h.bulbs.filter { it.meshId in r.meshIds }; if (bs.isEmpty()) continue; assigned += bs; group(h, r.name, bs) }
        val other = h.bulbs.filter { it !in assigned }
        if (other.isNotEmpty()) group(h, if (h.rooms.isEmpty()) "All bulbs" else "Other", other)
        refresh()
    }

    private fun group(h: Home, name: String, bulbs: List<Bulb>) {
        val card = card()
        val head = LinearLayout(this).apply { gravity = Gravity.CENTER_VERTICAL }
        head.addView(text(name, 16f, FG, bold = true), LinearLayout.LayoutParams(0, -2, 1f))
        val sw = switch(); sw.setOnClickListener { send(h, bulbs, Cmd.Power(sw.isChecked)) }
        head.addView(sw); card.addView(head)
        refreshers += { sw.isChecked = bulbs.any { it.state?.on == true } }
        if (bulbs.any { it.has(Caps.DIM) }) card.addView(seek(1, 100, 50) { v -> send(h, bulbs.filter { it.has(Caps.DIM) }, Cmd.Brightness(v)) })
        card.addView(View(this).apply { setBackgroundColor(CTL) }, LinearLayout.LayoutParams(-1, dp(1)).apply { topMargin = dp(4); bottomMargin = dp(4) })
        bulbs.forEach { bulbRow(card, h, it) }
        body.addView(card, LinearLayout.LayoutParams(-1, -2).apply { bottomMargin = dp(10) })
    }

    private fun bulbRow(parent: LinearLayout, h: Home, b: Bulb) {
        val line = LinearLayout(this).apply { gravity = Gravity.CENTER_VERTICAL; setPadding(0, dp(4), 0, 0) }
        val dot = View(this); line.addView(dot, LinearLayout.LayoutParams(dp(10), dp(10)).apply { rightMargin = dp(8) })
        line.addView(text(b.name, 14f, FG), LinearLayout.LayoutParams(0, -2, 1f))
        val extra = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; visibility = View.GONE; setPadding(dp(18), 0, 0, dp(4)) }
        var blinking = false
        val stopBlink = Runnable { if (blinking) { blinking = false; send(h, listOf(b), Cmd.Identify(false)) } }
        line.addView(button("◎") {
            ui.removeCallbacks(stopBlink)
            if (blinking) stopBlink.run() else { blinking = true; send(h, listOf(b), Cmd.Identify(true)); ui.postDelayed(stopBlink, 5000) }
        }.apply { contentDescription = "Find this bulb" })
        if (b.has(Caps.TEMP) || b.has(Caps.COLOR)) line.addView(button("⋯") { extra.visibility = if (extra.visibility == View.VISIBLE) View.GONE else View.VISIBLE })
        val via = text("", 10f, DIM).apply { setPadding(dp(6), dp(2), dp(6), dp(2)); background = round(CTL, 4) }
        line.addView(via, LinearLayout.LayoutParams(-2, -2).apply { leftMargin = dp(6); rightMargin = dp(6) })
        val sw = switch(); sw.setOnClickListener { send(h, listOf(b), Cmd.Power(sw.isChecked)) }
        line.addView(sw); parent.addView(line)
        var bri: SeekBar? = null
        if (b.has(Caps.DIM)) { bri = seek(1, 100, b.state?.brightness ?: 50) { v -> send(h, listOf(b), Cmd.Brightness(v)) }; parent.addView(bri) }
        if (b.has(Caps.TEMP)) {
            val r = LinearLayout(this).apply { gravity = Gravity.CENTER_VERTICAL }
            r.addView(text("Warm", 11f, DIM)); r.addView(seek(0, 100, 50) { v -> send(h, listOf(b), Cmd.Temp(v)) }, LinearLayout.LayoutParams(0, -2, 1f)); r.addView(text("Cool", 11f, DIM))
            extra.addView(r)
        }
        if (b.has(Caps.COLOR)) {
            val r = LinearLayout(this).apply { setPadding(0, dp(4), 0, 0) }
            SWATCHES.forEach { c -> r.addView(View(this).apply { background = round(c, 6); setOnClickListener { send(h, listOf(b), Cmd.Rgb(Color.red(c), Color.green(c), Color.blue(c))) } }, LinearLayout.LayoutParams(dp(30), dp(26)).apply { rightMargin = dp(6) }) }
            extra.addView(r)
        }
        parent.addView(extra)
        refreshers += {
            val s = b.state
            sw.isChecked = s?.on == true
            if (s != null && bri != null && !bri.isPressed) bri.progress = maxOf(1, s.brightness) - 1
            via.text = when (b.via) { Via.BLE -> "BLE"; else -> "OUT OF RANGE" }
            via.setTextColor(when (b.via) { Via.BLE -> ACCENT; else -> DIM })
            dot.background = round(if (s?.on == true) (if (s.mode == 0xfe) Color.rgb(s.rgb.first, s.rgb.second, s.rgb.third) else YELLOW) else CTL, 5)
        }
    }

    private fun settings() {
        val c1 = card()
        c1.addView(text("Connections", 16f, FG, bold = true))
        c1.addView(toggle("Bluetooth (direct to nearby bulbs)", store.ble) { store.ble = it; ensurePermsThenConnect() })
        c1.addView(note("Offline only: Lanternly has no internet permission and talks to bulbs over Bluetooth. Keep at least one bulb in range; the mesh relays to the rest."))
        body.addView(c1, lp())

        val c2 = card()
        c2.addView(text("Bulbs and mesh keys", 16f, FG, bold = true))
        c2.addView(note(if (vault.homes.isEmpty()) "No key file loaded." else "${vault.homes.size} home(s), ${vault.homes.sumOf { it.bulbs.size }} bulb(s), encrypted with Android Keystore."))
        val r = LinearLayout(this)
        r.addView(button("Import key file") { startActivityForResult(Intent(Intent.ACTION_OPEN_DOCUMENT).apply { addCategory(Intent.CATEGORY_OPENABLE); type = "*/*" }, REQ_IMPORT) })
        if (vault.homes.isNotEmpty()) r.addView(button("Export key file") { exportKeys() }.apply { (layoutParams as? ViewGroup.MarginLayoutParams)?.leftMargin = dp(8) })
        c2.addView(r); body.addView(c2, lp())

        val ca = card()
        ca.addView(text("Add a reset bulb (experimental)", 16f, FG, bold = true))
        ca.addView(note("This is how bulbs get into Lanternly. A bulb already paired with the Cync app must be factory reset first (see the guide below); the reset loses its current color, which you can set again right after. Lanternly then gives it a private Bluetooth mesh key generated on this phone. No account. Keep the bulb close to the phone."))
        val amsg = note("")
        ca.addView(button("Find reset bulbs") { if (!demo) adopt(amsg) }); ca.addView(amsg); body.addView(ca, lp())

        val cr = card()
        cr.addView(text("Bulb reset guide", 16f, FG, bold = true))
        cr.addView(note(ResetText.INTRO))
        ResetText.METHODS.forEach { (t, st) -> cr.addView(text(t, 13f, FG, bold = true)); cr.addView(note(st)) }

        body.addView(cr, lp())

        val c4 = card()
        c4.addView(text("About", 16f, FG, bold = true))
        c4.addView(note("Lanternly 0.1.2 · MIT license. Not affiliated with GE, Savant or Cync. No telemetry, no cloud, no internet permission. Keys are never logged."))
        val r2 = LinearLayout(this)
        r2.addView(button("Forget everything") {
            AlertDialog.Builder(this).setMessage("Delete stored keys and bulbs from this phone? Bulbs you added will only answer to a key-file backup or another factory reset.").setPositiveButton("Delete") { _, _ -> io.execute { teardown(); store.forget(); vault = Vault(); post { render(); setStatus("Forgotten") } } }.setNegativeButton("Cancel", null).show()
        })
        r2.addView(button("Done", accent = true) { settingsOpen = false; render() }.apply { (layoutParams as? ViewGroup.MarginLayoutParams)?.leftMargin = dp(8) })
        c4.addView(r2); body.addView(c4, lp())
    }

    private fun adopt(msg: TextView) {
        msg.text = "Listening for reset bulbs (8 s)…"
        io.execute {
            teardown()
            val macs = try { AndroidGatt.findResetBulbs(this, 8000) } catch (e: Exception) { post { msg.text = e.message }; return@execute }
            if (macs.isEmpty()) { post { msg.text = "No reset bulb found. A bulb is ready when it was reset (it blinks) and was not added to another app since." }; return@execute }
            var h = home
            if (h == null) {
                val (n, p) = Ble.newMeshCredentials()
                h = Home(System.currentTimeMillis() and 0x7fffffff, "My bulbs", n, p, emptyList(), mutableListOf())
                vault = Vault(vault.homes + h); store.homeIndex = vault.homes.size - 1
            }
            var added = 0
            for (mac in macs) {
                post { msg.text = "Adding $mac…" }
                try {
                    val g = AndroidGatt.connect(this, mac); try { BleSession.provision(g, h.meshName, h.meshPassword) } finally { g.close() }
                    Thread.sleep(1500)
                    val b = Bulb("Bulb ${h.bulbs.size + 1}", 0, Caps.ALL_CONTROLS, mac)
                    try {
                        val s = BleSession(AndroidGatt.connect(this, mac), h); val latch = java.util.concurrent.CountDownLatch(1)
                        s.onState = { _, st -> b.meshId = st.meshId; latch.countDown() }
                        s.login(); latch.await(4, java.util.concurrent.TimeUnit.SECONDS); s.close()
                    } catch (e: Exception) { Diag.log("adopt address", e) } // address unknown: 0 = the directly connected node
                    h.bulbs += b; added++; store.backedUp = false; save()
                } catch (e: Exception) { Diag.log("adopt", e); post { msg.text = "$mac: ${e.message}" } }
            }
            post {
                if (added > 0) {
                    msg.text = "Added $added of ${macs.size} bulb(s)."
                    AlertDialog.Builder(this).setTitle("Back up your new mesh key")
                        .setMessage("These bulbs now only answer to the mesh key stored on this phone. If it is lost, the only way back is another factory reset.\n\nSave a key-file backup now?")
                        .setPositiveButton("Back up") { _, _ -> exportKeys() }.setNegativeButton("Later", null).show()
                }
                connect()
            }
        }
    }

    /** Export: warn, offer a passphrase (recommended), then pick a file. */
    private fun exportKeys() {
        if (vault.homes.isEmpty()) return
        val p1 = edit("Passphrase (8+ characters)", InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_PASSWORD)
        val p2 = edit("Repeat passphrase", InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_PASSWORD)
        val box = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(dp(20), dp(8), dp(20), 0); addView(p1); addView(p2) }
        fun pick(pass: String?) { exportPass = pass; startActivityForResult(Intent(Intent.ACTION_CREATE_DOCUMENT).apply { addCategory(Intent.CATEGORY_OPENABLE); type = "application/json"; putExtra(Intent.EXTRA_TITLE, "lanternly-keys.json") }, REQ_EXPORT) }
        val d = AlertDialog.Builder(this).setTitle("Export key file")
            .setMessage("The key file contains your mesh password. Anyone with it can control your bulbs over Bluetooth. A passphrase is recommended; it cannot be recovered.")
            .setView(box).setPositiveButton("With passphrase", null).setNeutralButton("Without", null).setNegativeButton("Cancel", null).show()
        d.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener {
            val a = p1.text.toString()
            when { a.length < 8 -> p1.error = "At least 8 characters"; a != p2.text.toString() -> p2.error = "Doesn't match"; else -> { d.dismiss(); pick(a) } }
        }
        d.getButton(AlertDialog.BUTTON_NEUTRAL).setOnClickListener { d.dismiss(); pick(null) }
    }

    private fun askPassphrase(onOk: (String) -> Unit) {
        val e = edit("Passphrase", InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_PASSWORD)
        val box = LinearLayout(this).apply { setPadding(dp(20), dp(8), dp(20), 0); addView(e) }
        AlertDialog.Builder(this).setTitle("Protected key file").setView(box)
            .setPositiveButton("Import") { _, _ -> onOk(e.text.toString()) }.setNegativeButton("Cancel", null).show()
    }

    @Deprecated("SAF result") override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        val uri = data?.data ?: return
        if (resultCode != RESULT_OK) return
        fun fail(e: Exception) { Diag.log("key file", e); post { Toast.makeText(this, "Failed: ${e.message}", Toast.LENGTH_LONG).show() } }
        try {
            if (requestCode == REQ_IMPORT) {
                val text = contentResolver.openInputStream(uri)!!.bufferedReader().use { it.readText() }
                fun doImport(pass: String?) = io.execute {
                    try {
                        val v = KeyFile.import(text, pass)
                        require(v.homes.isNotEmpty()) { "no homes in file" }
                        vault = Vault(v.homes); store.unblock(); store.backedUp = true
                        if (save()) post { settingsOpen = false; render(); connect() }
                    } catch (e: Exception) { fail(e) }
                }
                if (KeyFile.isProtected(text)) askPassphrase { doImport(it) } else doImport(null)
            } else if (requestCode == REQ_EXPORT) {
                val pass = exportPass; exportPass = null
                io.execute {
                    try {
                        val out = KeyFile.export(vault, pass)
                        contentResolver.openOutputStream(uri)!!.use { it.write(out.toByteArray()) }
                        store.backedUp = true
                        post { Toast.makeText(this, if (pass != null) "Exported (passphrase-protected)" else "Exported", Toast.LENGTH_SHORT).show(); updateStatus() }
                    } catch (e: Exception) { fail(e) }
                }
            }
        } catch (e: Exception) { fail(e) }
    }

    // ---------- widgets ----------
    private fun lp() = LinearLayout.LayoutParams(-1, -2).apply { bottomMargin = dp(10) }
    private fun round(c: Int, r: Int) = GradientDrawable().apply { setColor(c); cornerRadius = dp(r).toFloat() }
    private fun card() = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; background = round(CARD, 10); setPadding(dp(14), dp(10), dp(14), dp(10)) }
    private fun text(s: String, size: Float, color: Int, bold: Boolean = false) = TextView(this).apply { text = s; textSize = size; setTextColor(color); if (bold) setTypeface(typeface, android.graphics.Typeface.BOLD) }
    private fun note(s: String) = text(s, 12f, DIM).apply { setPadding(0, dp(2), 0, dp(6)) }
    private fun button(s: String, accent: Boolean = false, f: () -> Unit) = Button(this).apply {
        text = s; isAllCaps = false; setTextColor(if (accent) BG else FG); background = round(if (accent) ACCENT else CTL, 8)
        minHeight = dp(36); minimumHeight = dp(36); minWidth = dp(44); minimumWidth = dp(44); setPadding(dp(12), 0, dp(12), 0); setOnClickListener { f() }
        layoutParams = LinearLayout.LayoutParams(-2, dp(36))
    }
    private fun chip(s: String, selected: Boolean, f: () -> Unit) = TextView(this).apply {
        text = s; textSize = 13f; setTextColor(if (selected) BG else FG); background = round(if (selected) ACCENT else CTL, 16)
        setPadding(dp(14), dp(7), dp(14), dp(7)); setOnClickListener { f() }
        layoutParams = LinearLayout.LayoutParams(-2, -2).apply { rightMargin = dp(8) }
    }
    private fun switch() = Switch(this).apply {
        thumbTintList = android.content.res.ColorStateList(arrayOf(intArrayOf(android.R.attr.state_checked), intArrayOf()), intArrayOf(ACCENT, DIM))
        trackTintList = android.content.res.ColorStateList(arrayOf(intArrayOf(android.R.attr.state_checked), intArrayOf()), intArrayOf(0x887AA2F7.toInt(), CTL))
    }
    private fun toggle(s: String, v: Boolean, f: (Boolean) -> Unit) = switch().apply { text = s; setTextColor(FG); isChecked = v; setPadding(0, dp(6), 0, dp(6)); setOnCheckedChangeListener { _, c -> f(c) } }
    private fun edit(hint: String, type: Int) = EditText(this).apply { this.hint = hint; inputType = type; setTextColor(FG); setHintTextColor(DIM); background = round(CTL, 6); setPadding(dp(10), dp(8), dp(10), dp(8)); layoutParams = LinearLayout.LayoutParams(-1, -2).apply { bottomMargin = dp(6) } }
    /** Sends while dragging at most ~6/s and always on release. */
    private fun seek(min: Int, max: Int, v: Int, f: (Int) -> Unit) = SeekBar(this).apply {
        this.max = max - min; progress = v - min
        progressTintList = android.content.res.ColorStateList.valueOf(ACCENT); thumbTintList = progressTintList
        progressBackgroundTintList = android.content.res.ColorStateList.valueOf(CTL)
        var last = 0L
        setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
            override fun onProgressChanged(s: SeekBar, p: Int, user: Boolean) { if (user && System.currentTimeMillis() - last > 160) { last = System.currentTimeMillis(); f(p + min) } }
            override fun onStartTrackingTouch(s: SeekBar) {}
            override fun onStopTrackingTouch(s: SeekBar) { f(s.progress + min) }
        })
        setPadding(dp(8), dp(8), dp(8), dp(8))
    }

    private class DemoLink : Link {
        override val kind = Via.BLE
        override var onState: ((Home?, BulbState) -> Unit)? = null
        override fun canReach(h: Home, b: Bulb) = true
        override fun send(h: Home, b: Bulb, c: Cmd) {}
    }
    private fun demoVault() = Vault(listOf(Home(1, "Home", "demo", "demo",
        listOf(Room(1, "Living room", listOf(1, 2)), Room(2, "Bedroom", listOf(3))),
        mutableListOf(Bulb("Floor lamp", 1, 6, "").apply { state = BulbState(1, true, true, 70, 30, Triple(0, 0, 0)) },
               Bulb("Shelf strip", 2, 6, "").apply { state = BulbState(2, true, true, 40, 0xfe, Triple(122, 162, 247)) },
               Bulb("Bedside", 3, 9, "").apply { state = BulbState(3, true, false, 20, 0, Triple(0, 0, 0)) }))))
}
