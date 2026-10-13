package com.vibecode.mobile.data

import org.junit.Assert.*
import org.junit.Test
import java.io.ByteArrayInputStream
import java.nio.ByteBuffer

class AesChannelTest {
    private fun hex(value: String) = value.chunked(2).map { it.toInt(16).toByte() }.toByteArray()

    @Test fun hkdfMatchesRfc5869CaseOne() {
        val result = AesChannel.hkdf(ByteArray(22) { 0x0b }, hex("000102030405060708090a0b0c"),
            hex("f0f1f2f3f4f5f6f7f8f9"))
        assertArrayEquals(hex("3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf"), result)
        assertEquals(32, result.size)
    }

    @Test fun payloadsAreAuthenticatedBoundToConnectionAndDirection() {
        val phone = AesChannel.newKey()
        val pc = AesChannel.newKey()
        fun client(purpose: String = "bridge") = AesChannel.create(phone, phone.public.encoded, pc.public.encoded, false, purpose)
        fun server(purpose: String = "bridge") = AesChannel.create(pc, phone.public.encoded, pc.public.encoded, true, purpose)
        val plain = "Authorization: Bearer secret-token\r\n/api/chats/private\r\nPrompt café ☕".toByteArray()
        client().use { outgoing ->
            val encrypted = outgoing.seal(plain)
            assertFalse(String(encrypted).contains("secret-token"))
            server().use { incoming ->
                assertArrayEquals(plain, incoming.open(encrypted))
                assertArrayEquals(plain, outgoing.open(incoming.seal(plain)))
                assertThrows(IllegalStateException::class.java) { incoming.open(encrypted) }
            }
            assertThrows(IllegalStateException::class.java) { outgoing.seal(plain) }
            for (index in listOf(0, 12, encrypted.lastIndex)) {
                server().use { target ->
                    val tampered = encrypted.copyOf().also { it[index] = (it[index].toInt() xor 1).toByte() }
                    assertThrows(java.security.GeneralSecurityException::class.java) { target.open(tampered) }
                }
            }
            client().use { reflected -> assertThrows(java.security.GeneralSecurityException::class.java) { reflected.open(encrypted) } }
            server("discovery").use { wrongPurpose ->
                assertThrows(java.security.GeneralSecurityException::class.java) { wrongPurpose.open(encrypted) }
            }
            val otherPc = AesChannel.newKey()
            AesChannel.create(otherPc, phone.public.encoded, otherPc.public.encoded, true, "bridge").use { other ->
                assertThrows(java.security.GeneralSecurityException::class.java) { other.open(encrypted) }
            }
        }
    }

    @Test fun invalidLengthsFailBeforeAllocation() {
        for (length in listOf(-1, 0, Int.MAX_VALUE)) {
            val input = ByteArrayInputStream(ByteBuffer.allocate(4).putInt(length).array())
            assertThrows(java.io.IOException::class.java) { AesChannel.readBlob(input, 128) }
        }
    }
}
