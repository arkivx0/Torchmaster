package io.github.arkivx0.lanternly

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.AtomicFile
import io.github.arkivx0.lanternly.core.Schedule
import io.github.arkivx0.lanternly.core.Schedules
import io.github.arkivx0.lanternly.core.Vault
import java.io.File
import java.security.KeyStore
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/**
 * Vault (mesh keys and bulbs) is AES-GCM encrypted with a non-exportable Android Keystore key and written with AtomicFile.
 * A vault that cannot be read is renamed to vault.bin.corrupt-<time> and saving is blocked until the user imports a
 * key file or chooses Forget everything, so a failed read never overwrites the good keys.
 */
class Store(ctx: Context) {
    private val file = File(ctx.noBackupFilesDir, "vault.bin")
    private val atomic = AtomicFile(file)
    private val schedFile = AtomicFile(File(ctx.noBackupFilesDir, "schedules.json"))
    private val prefs = ctx.getSharedPreferences("settings", Context.MODE_PRIVATE)
    private val alias = "lanternly.vault"

    var ble: Boolean get() = prefs.getBoolean("ble", true); set(v) = prefs.edit().putBoolean("ble", v).apply()
    var homeIndex: Int get() = prefs.getInt("home", 0); set(v) = prefs.edit().putInt("home", v).apply()
    /** False after bulbs were added locally, until a key file is exported (backup nag). */
    var backedUp: Boolean get() = prefs.getBoolean("backedUp", true); set(v) = prefs.edit().putBoolean("backedUp", v).apply()

    /** Path of the quarantined vault when the last load failed; saves are refused while set. */
    var quarantined: String? = null; private set

    class SaveBlocked : Exception("Saving is blocked because the stored keys could not be read. Import a key file or use Forget everything.")

    private fun key(): SecretKey {
        val ks = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (ks.getKey(alias, null) as? SecretKey)?.let { return it }
        val g = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        g.init(KeyGenParameterSpec.Builder(alias, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
            .setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE).setKeySize(256).build())
        return g.generateKey()
    }

    private fun decrypt(): String {
        val b = atomic.readFully(); require(b.size > 13 && b[0].toInt() == 1) { "bad vault header" }
        val c = Cipher.getInstance("AES/GCM/NoPadding").apply { init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, b.copyOfRange(1, 13))) }
        return String(c.doFinal(b, 13, b.size - 13))
    }

    /** Read-only load for background work (schedules): never quarantines or rewrites; null if unreadable. */
    fun loadQuiet(): Vault? = try { if (file.exists()) Vault.fromJson(decrypt()) else Vault() } catch (e: Exception) {
        android.util.Log.w("Lanternly", "vault read (background): ${e.javaClass.simpleName}"); null
    }

    fun load(): Vault {
        if (!file.exists()) return Vault()
        return try {
            val json = decrypt()
            Vault.fromJson(json).also { if (json.contains("\"Cloud\"")) try { save(it) } catch (e: Exception) { android.util.Log.w("Lanternly", "vault rewrite: ${e.javaClass.simpleName}") } } // 0.1.0 vault with a cloud token: rewrite without it
        } catch (e: Exception) {
            android.util.Log.w("Lanternly", "vault load failed: ${e.javaClass.simpleName}")
            val q = File(file.path + ".corrupt-" + SimpleDateFormat("yyyyMMdd-HHmmss", Locale.US).format(Date()))
            quarantined = if (file.renameTo(q)) q.name else file.name
            Vault()
        }
    }

    /** Clears the save block; call only after the user imported a key file or chose Forget. */
    fun unblock() { quarantined = null }

    fun save(v: Vault) {
        if (quarantined != null) throw SaveBlocked()
        val c = Cipher.getInstance("AES/GCM/NoPadding").apply { init(Cipher.ENCRYPT_MODE, key()) }
        val enc = c.doFinal(v.toJson().toByteArray())
        val out = atomic.startWrite()
        try { out.write(byteArrayOf(1) + c.iv + enc); atomic.finishWrite(out) } catch (e: Exception) { atomic.failWrite(out); throw e }
    }

    /** Schedules: times, days, action and target ids (no keys). Plain JSON next to the vault, written atomically. */
    fun loadSchedules(): List<Schedule> = try { if (schedFile.baseFile.exists()) Schedules.fromJson(String(schedFile.readFully())) else emptyList() } catch (e: Exception) {
        android.util.Log.w("Lanternly", "schedules load: ${e.javaClass.simpleName}"); emptyList()
    }
    @Synchronized fun saveSchedules(l: List<Schedule>) {
        val out = schedFile.startWrite()
        try { out.write(Schedules.toJson(l).toByteArray()); schedFile.finishWrite(out) } catch (e: Exception) { schedFile.failWrite(out); throw e }
    }
    /** Result of the last schedule run, shown on the Schedules page. */
    var lastRun: String? get() = prefs.getString("lastRun", null); set(v) = prefs.edit().putString("lastRun", v).apply()

    fun forget() { schedFile.delete(); atomic.delete(); prefs.edit().clear().apply(); quarantined = null }
}
