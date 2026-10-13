package com.vibecode.mobile.data

import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.EOFException
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.security.KeyFactory
import java.security.KeyPair
import java.security.KeyPairGenerator
import java.security.MessageDigest
import java.security.SecureRandom
import java.security.interfaces.ECPublicKey
import java.security.spec.ECGenParameterSpec
import java.security.spec.X509EncodedKeySpec
import javax.crypto.Cipher
import javax.crypto.KeyAgreement
import javax.crypto.Mac
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

/** One complete request/reply per fresh ECDH exchange, with independent AES-256-GCM keys per direction. */
internal class AesChannel private constructor(
    private val sendKey: ByteArray,
    private val receiveKey: ByteArray,
    private val sendAad: ByteArray,
    private val receiveAad: ByteArray,
) : AutoCloseable {
    private var sent = false
    private var received = false

    fun seal(plain: ByteArray): ByteArray {
        check(!sent) { "A connection carries only one outgoing message." }
        sent = true
        val nonce = ByteArray(12).also { SecureRandom().nextBytes(it) }
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, SecretKeySpec(sendKey, "AES"), GCMParameterSpec(128, nonce))
        cipher.updateAAD(sendAad)
        return nonce + cipher.doFinal(plain)
    }

    fun open(frame: ByteArray): ByteArray {
        check(!received && frame.size >= 28) { "Invalid or repeated encrypted message." }
        received = true
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.DECRYPT_MODE, SecretKeySpec(receiveKey, "AES"), GCMParameterSpec(128, frame, 0, 12))
        cipher.updateAAD(receiveAad)
        return cipher.doFinal(frame, 12, frame.size - 12)
    }

    fun send(output: OutputStream, plain: ByteArray) = writeBlob(output, seal(plain))
    fun receive(input: InputStream, maxPlainBytes: Int): ByteArray = open(readBlob(input, maxPlainBytes + 28))

    override fun close() {
        sent = true
        received = true
        sendKey.fill(0)
        receiveKey.fill(0)
    }

    companion object {
        val MAGIC = "VIBECODE-AES256-GCM-1\r\n\r\n".toByteArray(Charsets.US_ASCII)
        val DISCOVERY_MAGIC = "VIBECODE-DISCOVER-2\n".toByteArray(Charsets.US_ASCII)
        const val MAX_PUBLIC_KEY_BYTES = 128
        const val MAX_REQUEST_BYTES = 1024 * 1024 + 16 * 1024 + 4
        const val MAX_RESPONSE_BYTES = 64 * 1024 * 1024
        const val DISCOVERY_BYTES = 1024

        fun newKey(): KeyPair = KeyPairGenerator.getInstance("EC").apply {
            initialize(ECGenParameterSpec("secp256r1"), SecureRandom())
        }.generateKeyPair()

        fun connect(input: InputStream, output: OutputStream): AesChannel {
            val key = newKey()
            val clientPublic = key.public.encoded
            output.write(MAGIC)
            writeBlob(output, clientPublic)
            val magic = ByteArray(MAGIC.size)
            try { DataInputStream(input).readFully(magic) }
            catch (e: EOFException) { throw IOException("The PC closed the encryption handshake. Update VibeCode on the PC.", e) }
            if (!magic.contentEquals(MAGIC)) throw IOException("Update VibeCode on the PC: AES-256 encryption is required.")
            return create(key, clientPublic, readBlob(input, MAX_PUBLIC_KEY_BYTES), server = false, purpose = "bridge")
        }

        fun create(local: KeyPair, clientPublic: ByteArray, serverPublic: ByteArray, server: Boolean, purpose: String): AesChannel {
            require(clientPublic.size <= MAX_PUBLIC_KEY_BYTES && serverPublic.size <= MAX_PUBLIC_KEY_BYTES)
            val peer = KeyFactory.getInstance("EC").generatePublic(
                X509EncodedKeySpec(if (server) clientPublic else serverPublic)
            ) as ECPublicKey
            val expected = (local.public as ECPublicKey).params
            require(peer.params.curve == expected.curve && peer.params.generator == expected.generator &&
                peer.params.order == expected.order && peer.params.cofactor == expected.cofactor) { "P-256 is required." }
            val secret = KeyAgreement.getInstance("ECDH").run {
                init(local.private)
                doPhase(peer, true)
                generateSecret()
            }
            try {
                val context = "vibecode-aes256-gcm-v1/$purpose".toByteArray(Charsets.US_ASCII)
                val salt = MessageDigest.getInstance("SHA-256").digest(context + clientPublic + serverPublic)
                val toPc = "phone-to-pc".toByteArray(Charsets.US_ASCII)
                val toPhone = "pc-to-phone".toByteArray(Charsets.US_ASCII)
                val requestKey = hkdf(secret, salt, toPc)
                val responseKey = hkdf(secret, salt, toPhone)
                return if (server) AesChannel(responseKey, requestKey, salt + toPhone, salt + toPc)
                else AesChannel(requestKey, responseKey, salt + toPc, salt + toPhone)
            } finally { secret.fill(0) }
        }

        /** RFC 5869 extract + one expand block: exactly 32 bytes, never password-derived or a global app key. */
        internal fun hkdf(secret: ByteArray, salt: ByteArray, info: ByteArray): ByteArray {
            val mac = Mac.getInstance("HmacSHA256")
            mac.init(SecretKeySpec(salt, "HmacSHA256"))
            val prk = mac.doFinal(secret)
            try {
                mac.init(SecretKeySpec(prk, "HmacSHA256"))
                return mac.doFinal(info + byteArrayOf(1))
            } finally { prk.fill(0) }
        }

        fun readBlob(input: InputStream, maxBytes: Int): ByteArray {
            val data = DataInputStream(input)
            val size = data.readInt()
            if (size !in 1..maxBytes) throw IOException("Invalid encrypted frame length.")
            return ByteArray(size).also { data.readFully(it) }
        }

        fun writeBlob(output: OutputStream, bytes: ByteArray) {
            val data = DataOutputStream(output)
            data.writeInt(bytes.size)
            data.write(bytes)
            data.flush()
        }
    }
}
