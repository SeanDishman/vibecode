package com.vibecode.mobile.data

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.io.ByteArrayInputStream
import java.nio.ByteBuffer
import java.security.KeyPair
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.NetworkInterface
import java.net.SocketTimeoutException

/** A PC that answered a discovery probe. [fingerprint] is what decides whether it is *the* PC. */
data class FoundPc(val host: String, val port: Int, val name: String, val fingerprint: String)

/**
 * Finds the PC on the local network when its address has moved.
 *
 * A generated APK knows the addresses its PC had on the day it was built. Those go stale constantly — DHCP hands
 * out a different lease, the PC moves from Ethernet to Wi-Fi, the router reboots — and when they do, the app has
 * no way to recover: it retries three dead addresses forever while the PC sits there perfectly healthy. That is
 * the failure this closes.
 *
 * Security note: discovery is a *hint*, never a decision. Anything on the Wi-Fi can answer a broadcast and claim
 * to be the PC, so a reply is only ever used to pick an address to attempt — the certificate pinned inside this
 * signed APK still has to match before a single byte of credential is sent, and a liar fails that handshake. The
 * probe carries only an ephemeral public key; the reply is AES-256-GCM encrypted to that key. Discovery is
 * unauthenticated until the subsequent pinned TLS connection, so encryption never makes a reply trusted.
 */
object Discovery {

    /** The desktop ignores anything shorter, so a reply can never be bigger than the packet that asked for it. */
    private const val PROBE_BYTES = AesChannel.DISCOVERY_BYTES

    /**
     * Broadcasts on every network this phone is attached to and collects the PCs that answer.
     *
     * @param port the port the desktop listens on — the same number for UDP discovery and the TLS bridge.
     * @param timeoutMs total time to spend listening. Kept short: this runs before each connection attempt round.
     */
    suspend fun find(port: Int, timeoutMs: Int = 1_200): List<FoundPc> = withContext(Dispatchers.IO) {
        val found = LinkedHashMap<String, FoundPc>()
        val socket = try {
            DatagramSocket().apply {
                broadcast = true
                soTimeout = 250
            }
        } catch (e: Exception) {
            return@withContext emptyList()
        }

        try {
            val key = AesChannel.newKey()
            val publicKey = key.public.encoded
            val payload = ByteBuffer.allocate(PROBE_BYTES).put(AesChannel.DISCOVERY_MAGIC)
                .putInt(publicKey.size).put(publicKey).array()
            for (target in broadcastTargets()) {
                try {
                    socket.send(DatagramPacket(payload, payload.size, InetSocketAddress(target, port)))
                } catch (e: Exception) {
                    // One unusable interface must not stop the others; a phone on Wi-Fi plus a VPN has several.
                }
            }

            val deadline = System.currentTimeMillis() + timeoutMs
            val buffer = ByteArray(2048)
            while (System.currentTimeMillis() < deadline) {
                val packet = DatagramPacket(buffer, buffer.size)
                try {
                    socket.receive(packet)
                } catch (e: SocketTimeoutException) {
                    continue
                } catch (e: Exception) {
                    break
                }
                parse(packet, port, key)?.let { found.putIfAbsent(it.host, it) }
            }
        } finally {
            runCatching { socket.close() }
        }

        found.values.toList()
    }

    /**
     * Convenience for the enrolment and reconnect paths: the addresses of PCs whose certificate fingerprint is the
     * one this APK was built for. Anything else that answered is discarded here rather than attempted.
     */
    suspend fun hostsMatching(fingerprint: String, port: Int, timeoutMs: Int = 1_200): List<String> =
        find(port, timeoutMs)
            .filter { it.fingerprint.equals(fingerprint, ignoreCase = true) }
            .map { it.host }

    private fun parse(packet: DatagramPacket, fallbackPort: Int, key: KeyPair): FoundPc? = runCatching {
        if (packet.length > PROBE_BYTES) return null
        val input = ByteArrayInputStream(packet.data, packet.offset, packet.length)
        val magic = ByteArray(AesChannel.DISCOVERY_MAGIC.size)
        if (input.read(magic) != magic.size || !magic.contentEquals(AesChannel.DISCOVERY_MAGIC)) return null
        val serverPublic = AesChannel.readBlob(input, AesChannel.MAX_PUBLIC_KEY_BYTES)
        val o = AesChannel.create(key, key.public.encoded, serverPublic, server = false, purpose = "discovery").use { channel ->
            val plain = channel.open(input.readBytes())
            try { JSONObject(String(plain, Charsets.UTF_8)) } finally { plain.fill(0) }
        }
        if (o.optString("app") != "vibecode") return null
        val fingerprint = o.optString("fingerprint")
        if (!fingerprint.matches(Regex("[0-9A-Fa-f]{64}"))) return null
        val port = o.optInt("port", fallbackPort)
        if (port !in 1..65535) return null
        FoundPc(
            host = packet.address.hostAddress ?: return null,
            port = port,
            name = o.optString("name"),
            fingerprint = fingerprint,
        )
    }.getOrNull()

    /**
     * Every address worth broadcasting to.
     *
     * 255.255.255.255 alone is not enough: Android drops limited broadcasts on some OEM builds, and a phone with
     * more than one interface only sends it out of the default one. Per-interface directed broadcasts cover the
     * rest.
     */
    private fun broadcastTargets(): List<InetAddress> {
        val targets = mutableListOf<InetAddress>()
        runCatching { targets.add(InetAddress.getByName("255.255.255.255")) }
        runCatching {
            for (nic in NetworkInterface.getNetworkInterfaces()) {
                if (!nic.isUp || nic.isLoopback) continue
                for (address in nic.interfaceAddresses) {
                    val broadcast = address.broadcast ?: continue
                    if (address.address is Inet4Address) targets.add(broadcast)
                }
            }
        }
        return targets.distinct()
    }
}
