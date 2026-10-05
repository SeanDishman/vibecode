package com.vibecode.mobile.data

import android.content.Context
import android.os.Build
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import org.json.JSONObject
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/** Everything the app needs to talk to one paired PC. */
data class Pairing(
    val host: String,
    val port: Int,
    val fingerprint: String,
    val token: String,
    val pcName: String,
) {
    val address: String get() = "$host:$port"
}

/**
 * Where the pairing lives on the phone.
 *
 * The bearer token is a standing credential for someone's desktop, so it is not written to preferences in the
 * clear. AES-GCM protects the whole pairing, including its pin, against metadata substitution. Android Keystore
 * keeps the encryption key out of preferences; a compromised OS can still invoke keys and read app memory.
 * Request authentication additionally requires the separate non-exportable DeviceIdentity signing key.
 */
class SecureStore(context: Context) {

    private val prefs = context.getSharedPreferences("vibecode.pairing", Context.MODE_PRIVATE)

    fun load(): Pairing? {
        prefs.getString(KEY_SEALED, null)?.let { stored ->
            return runCatching {
                val o = JSONObject(decrypt(stored) ?: return null)
                Pairing(o.getString("host"), o.getInt("port"), o.getString("fingerprint"),
                    o.getString("token"), o.optString("pc"))
            }.getOrNull()
        }
        val host = prefs.getString(KEY_HOST, null) ?: return null
        val fingerprint = prefs.getString(KEY_FINGERPRINT, null) ?: return null
        val token = decrypt(prefs.getString(KEY_TOKEN, null) ?: return null) ?: return null
        return Pairing(
            host = host,
            port = prefs.getInt(KEY_PORT, 8765),
            fingerprint = fingerprint,
            token = token,
            pcName = prefs.getString(KEY_PC, "") ?: "",
        )
    }

    fun save(pairing: Pairing) {
        val plain = JSONObject().put("host", pairing.host).put("port", pairing.port)
            .put("fingerprint", pairing.fingerprint).put("token", pairing.token).put("pc", pairing.pcName)
        check(prefs.edit().clear().putString(KEY_SEALED, encrypt(plain.toString())).commit()) {
            "The phone could not save its pairing. Revoke the phone on the PC and pair again."
        }
    }

    fun clear() {
        prefs.edit().clear().commit()
        runCatching { keyStore().deleteEntry(KEY_ALIAS) }
    }

    // ---------------- keystore-backed AES-GCM ----------------

    private fun keyStore() = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }

    private fun secretKey(): SecretKey {
        val store = keyStore()
        (store.getEntry(KEY_ALIAS, null) as? KeyStore.SecretKeyEntry)?.let { return it.secretKey }

        // StrongBox puts the key in a separate tamper-resistant chip rather than the main SoC's TEE. Not every
        // device has one, and asking for it where it does not exist throws at generation time rather than
        // degrading — so it is attempted first and the plain keystore is the fallback.
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
            runCatching { return generateKey(strongBox = true) }
            // A failed StrongBox attempt can leave the alias occupied by an unusable entry; clear it so the
            // fallback below is generating into a clean slot rather than colliding.
            runCatching { store.deleteEntry(KEY_ALIAS) }
        }
        return generateKey(strongBox = false)
    }

    private fun generateKey(strongBox: Boolean): SecretKey {
        val generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        val spec = KeyGenParameterSpec.Builder(
            KEY_ALIAS,
            KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT,
        )
            .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
            .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
            .setKeySize(256)
            // Refuse to reuse an IV. The default is already true; stating it means a future edit cannot quietly
            // turn AES-GCM into a nonce-reuse bug.
            .setRandomizedEncryptionRequired(true)
            .apply {
                if (strongBox && Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) setIsStrongBoxBacked(true)
            }
            .build()
        generator.init(spec)
        return generator.generateKey()
    }

    private fun encrypt(plain: String): String {
        val cipher = Cipher.getInstance(TRANSFORMATION).apply { init(Cipher.ENCRYPT_MODE, secretKey()) }
        val sealed = cipher.doFinal(plain.toByteArray())
        // GCM picks its own nonce; it has to travel with the ciphertext or nothing can be opened again.
        val payload = cipher.iv + sealed
        return Base64.encodeToString(payload, Base64.NO_WRAP)
    }

    private fun decrypt(stored: String): String? = runCatching {
        val payload = Base64.decode(stored, Base64.NO_WRAP)
        require(payload.size >= GCM_NONCE_BYTES + 16)
        val iv = payload.copyOfRange(0, GCM_NONCE_BYTES)
        val sealed = payload.copyOfRange(GCM_NONCE_BYTES, payload.size)
        val cipher = Cipher.getInstance(TRANSFORMATION).apply {
            init(Cipher.DECRYPT_MODE, secretKey(), GCMParameterSpec(128, iv))
        }
        String(cipher.doFinal(sealed))
    }.getOrNull()

    private companion object {
        const val KEY_ALIAS = "vibecode.pairing.key"
        const val KEY_SEALED = "sealed_pairing_v2"
        const val TRANSFORMATION = "AES/GCM/NoPadding"
        const val GCM_NONCE_BYTES = 12
        const val KEY_HOST = "host"
        const val KEY_PORT = "port"
        const val KEY_FINGERPRINT = "fingerprint"
        const val KEY_TOKEN = "token"
        const val KEY_PC = "pc"
    }
}
