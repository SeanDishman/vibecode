package com.vibecode.mobile.data

import android.os.Build
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyInfo
import android.security.keystore.KeyProperties
import android.util.Base64
import com.vibecode.mobile.BuildConfig
import java.security.KeyFactory
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.MessageDigest
import java.security.SecureRandom
import java.security.Signature
import java.security.spec.ECGenParameterSpec

/**
 * Per-installation signing identity. The private key stays in Android Keystore, never in preferences or the APK.
 * Release builds require TEE/StrongBox backing. This is a local check, not remote hardware attestation: a
 * compromised OS may still invoke this key, and the desktop cannot establish hardware provenance from SPKI.
 */
object DeviceIdentity {
    private const val ALIAS = "vibecode.device.signing.v1"

    @Synchronized
    private fun identity(): KeyStore.PrivateKeyEntry {
        val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (store.getEntry(ALIAS, null) as? KeyStore.PrivateKeyEntry)?.let {
            requireHardware(it)
            return it
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
            try {
                generate(strongBox = true)
            } catch (_: java.security.GeneralSecurityException) {
                store.deleteEntry(ALIAS)
            } catch (_: android.security.keystore.StrongBoxUnavailableException) {
                store.deleteEntry(ALIAS)
            }
        }
        if (!store.containsAlias(ALIAS)) generate(strongBox = false)
        val entry = store.getEntry(ALIAS, null) as? KeyStore.PrivateKeyEntry
            ?: error("The phone could not create its device identity.")
        requireHardware(entry)
        return entry
    }

    private fun generate(strongBox: Boolean) {
        val spec = KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_SIGN)
            .setAlgorithmParameterSpec(ECGenParameterSpec("secp256r1"))
            .setDigests(KeyProperties.DIGEST_SHA256)
            .apply {
                if (strongBox && Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) setIsStrongBoxBacked(true)
            }
            .build()
        KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, "AndroidKeyStore").apply {
            initialize(spec)
        }.generateKeyPair()
    }

    @Suppress("DEPRECATION")
    private fun requireHardware(entry: KeyStore.PrivateKeyEntry) {
        val info = KeyFactory.getInstance(entry.privateKey.algorithm, "AndroidKeyStore")
            .getKeySpec(entry.privateKey, KeyInfo::class.java)
        val hardware = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            info.securityLevel == KeyProperties.SECURITY_LEVEL_TRUSTED_ENVIRONMENT ||
                info.securityLevel == KeyProperties.SECURITY_LEVEL_STRONGBOX
        } else info.isInsideSecureHardware
        check(hardware || BuildConfig.DEBUG) {
            "This phone does not provide a hardware-backed device key. Pairing is unavailable."
        }
    }

    fun publicKey(): String = Base64.encodeToString(identity().certificate.publicKey.encoded, Base64.NO_WRAP)

    fun headers(method: String, target: String, body: ByteArray, token: String, epoch: String): Map<String, String> {
        require(epoch.matches(Regex("[a-f0-9]{64}"))) { "The PC does not support device-bound authentication. Update VibeCode on the PC." }
        val timestamp = (System.currentTimeMillis() / 1000).toString()
        val nonce = ByteArray(16).also { SecureRandom().nextBytes(it) }.hex()
        val canonical = listOf("vibecode-device-v1", epoch, timestamp, nonce, method, target,
            sha256(body), sha256(token.toByteArray(Charsets.UTF_8))).joinToString("\n")
        val signature = Signature.getInstance("SHA256withECDSA").apply {
            initSign(identity().privateKey)
            update(canonical.toByteArray(Charsets.UTF_8))
        }.sign()
        return mapOf(
            "X-VibeCode-Epoch" to epoch,
            "X-VibeCode-Time" to timestamp,
            "X-VibeCode-Nonce" to nonce,
            "X-VibeCode-Signature" to Base64.encodeToString(signature, Base64.NO_WRAP),
        )
    }

    private fun sha256(bytes: ByteArray): String = MessageDigest.getInstance("SHA-256").digest(bytes).hex()
    private fun ByteArray.hex(): String = joinToString("") { "%02x".format(it) }
}
