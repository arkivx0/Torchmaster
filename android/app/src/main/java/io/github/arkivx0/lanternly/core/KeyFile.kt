package io.github.arkivx0.lanternly.core

import org.json.JSONObject
import java.security.SecureRandom
import java.util.Base64
import javax.crypto.AEADBadTagException
import javax.crypto.Cipher
import javax.crypto.SecretKeyFactory
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.PBEKeySpec
import javax.crypto.spec.SecretKeySpec

/**
 * Key-file backup, same format as the Windows app. Plain files are the Vault JSON. Protected files wrap it with
 * PBKDF2-HMAC-SHA256 (600k iterations, 16-byte salt) + AES-256-GCM (12-byte nonce, 16-byte tag, AAD = format name).
 */
object KeyFile {
    const val MAGIC = "lanternly-keyfile"
    const val ITERATIONS = 600_000
    private val b64e = Base64.getEncoder(); private val b64d = Base64.getDecoder()

    private fun derive(pass: String, salt: ByteArray, it: Int): SecretKeySpec {
        val spec = PBEKeySpec(pass.toCharArray(), salt, it, 256)
        try { return SecretKeySpec(SecretKeyFactory.getInstance("PBKDF2WithHmacSHA256").generateSecret(spec).encoded, "AES") } finally { spec.clearPassword() }
    }

    fun export(v: Vault, passphrase: String?): String {
        val plain = Vault(v.homes).toJson()
        if (passphrase.isNullOrEmpty()) return plain
        val r = SecureRandom(); val salt = ByteArray(16).also(r::nextBytes); val nonce = ByteArray(12).also(r::nextBytes)
        val c = Cipher.getInstance("AES/GCM/NoPadding").apply { init(Cipher.ENCRYPT_MODE, derive(passphrase, salt, ITERATIONS), GCMParameterSpec(128, nonce)); updateAAD(MAGIC.toByteArray()) }
        val ct = c.doFinal(plain.toByteArray(Charsets.UTF_8)) // ciphertext || tag, same layout as Windows
        return JSONObject().put("format", MAGIC).put("version", 1).put("kdf", "pbkdf2-sha256").put("iterations", ITERATIONS)
            .put("salt", b64e.encodeToString(salt)).put("nonce", b64e.encodeToString(nonce)).put("ciphertext", b64e.encodeToString(ct)).toString()
    }

    fun isProtected(text: String) = try { JSONObject(text).optString("format") == MAGIC } catch (_: Exception) { false }

    class WrongPassphrase(msg: String) : Exception(msg)

    fun import(text: String, passphrase: String?): Vault {
        if (!isProtected(text)) return Vault.fromJson(text)
        if (passphrase.isNullOrEmpty()) throw WrongPassphrase("This key file is protected with a passphrase.")
        val o = JSONObject(text); val it = o.getInt("iterations")
        require(it in 100_000..10_000_000) { "bad iteration count" }
        val salt = b64d.decode(o.getString("salt")); val nonce = b64d.decode(o.getString("nonce")); val blob = b64d.decode(o.getString("ciphertext"))
        require(blob.size >= 16 && nonce.size == 12) { "truncated key file" }
        val c = Cipher.getInstance("AES/GCM/NoPadding").apply { init(Cipher.DECRYPT_MODE, derive(passphrase, salt, it), GCMParameterSpec(128, nonce)); updateAAD(MAGIC.toByteArray()) }
        val pt = try { c.doFinal(blob) } catch (_: AEADBadTagException) { throw WrongPassphrase("Wrong passphrase, or the file was changed.") }
        return Vault.fromJson(String(pt, Charsets.UTF_8))
    }
}
