package com.vibecode.mobile.data

import java.io.IOException
import java.net.InetSocketAddress
import java.net.Socket
import javax.net.ssl.SSLSocket

/** HTTP is entirely inside the AES frame: targets, query strings, credentials, payloads and error replies. */
internal object BridgeTransport {
    data class Reply(val status: Int, val body: String)

    fun exchange(address: BridgeAddress, pin: String?, onSeen: ((String) -> Unit)?, method: String,
                 path: String, body: ByteArray?, headers: Map<String, String>): Reply {
        require(method in listOf("GET", "POST") && path.startsWith('/') && path.none { it.isWhitespace() || it.isISOControl() })
        require(headers.all { (name, value) -> name.none { it.isWhitespace() || it.isISOControl() || it == ':' } &&
            value.none { it.isISOControl() } })
        val head = buildString {
            append("$method $path HTTP/1.1\r\nHost: ${address.authority}\r\nAccept: application/json\r\n")
            append("Content-Type: application/json; charset=utf-8\r\nContent-Length: ${body?.size ?: 0}\r\n")
            headers.forEach { (key, value) -> append("$key: $value\r\n") }
            append("Connection: close\r\n\r\n")
        }.toByteArray(Charsets.UTF_8)
        val request = head + (body ?: byteArrayOf())
        try {
            require(request.size <= AesChannel.MAX_REQUEST_BYTES) { "This request is too large." }
            Socket().use { raw ->
                raw.connect(InetSocketAddress(address.host.removeSurrounding("[", "]"), address.port), 8_000)
                raw.soTimeout = 45_000
                (PinnedTls.socketFactory(pin, onSeen).createSocket(raw, address.host, address.port, true) as SSLSocket).use { tls ->
                    tls.soTimeout = 45_000
                    tls.startHandshake()
                    AesChannel.connect(tls.inputStream, tls.outputStream).use { channel ->
                        channel.send(tls.outputStream, request)
                        val response = channel.receive(tls.inputStream, AesChannel.MAX_RESPONSE_BYTES)
                        try { return parseResponse(response) } finally { response.fill(0) }
                    }
                }
            }
        } finally { request.fill(0); head.fill(0) }
    }

    private fun parseResponse(bytes: ByteArray): Reply {
        val end = (0 until minOf(bytes.size - 3, 16 * 1024)).firstOrNull {
            bytes[it] == 13.toByte() && bytes[it + 1] == 10.toByte() &&
                bytes[it + 2] == 13.toByte() && bytes[it + 3] == 10.toByte()
        } ?: throw IOException("Invalid encrypted PC response.")
        val lines = String(bytes, 0, end, Charsets.US_ASCII).split("\r\n")
        val statusLine = lines.first().split(' ', limit = 3)
        if (statusLine.size != 3 || statusLine[0] != "HTTP/1.1") throw IOException("Invalid encrypted PC status.")
        val status = statusLine[1].toIntOrNull()?.takeIf { it in 100..599 }
            ?: throw IOException("Invalid encrypted PC status.")
        val lengths = lines.drop(1).filter { it.startsWith("Content-Length:", ignoreCase = true) }
        if (lengths.size != 1 || lengths.single().substringAfter(':').trim().toIntOrNull() != bytes.size - end - 4)
            throw IOException("Incomplete encrypted PC response.")
        return Reply(status, String(bytes, end + 4, bytes.size - end - 4, Charsets.UTF_8))
    }
}
