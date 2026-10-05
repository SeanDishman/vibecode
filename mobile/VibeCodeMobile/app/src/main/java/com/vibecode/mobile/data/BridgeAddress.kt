package com.vibecode.mobile.data

import java.net.URI
import java.net.URL

/** A bridge origin. Paths, credentials and insecure schemes are never part of a PC address. */
data class BridgeAddress(val host: String, val port: Int = 8765) {
    init {
        require(host.isNotBlank() && host.none { it.isWhitespace() || it in "/?#@\\" }) {
            "Enter the PC's hostname or IP address."
        }
        require(port in 1..65535) { "The port must be between 1 and 65535." }
    }

    val authority: String get() = "${if (':' in host) "[${host.removeSurrounding("[", "]")}]" else host}:$port"

    fun url(path: String): URL {
        require(path.startsWith('/') && !path.startsWith("//"))
        return URL("https://$authority$path")
    }

    companion object {
        fun parse(input: String): BridgeAddress {
            val raw = input.trim()
            require(raw.isNotEmpty()) { "Type the address shown on your PC." }
            val origin = when {
                "://" in raw -> raw
                raw.count { it == ':' } > 1 && !raw.startsWith('[') -> "https://[$raw]"
                else -> "https://$raw"
            }
            val uri = try { URI(origin) } catch (_: Exception) {
                throw IllegalArgumentException("Enter a hostname or IP address, followed by an optional port.")
            }
            require(uri.scheme.equals("https", ignoreCase = true)) { "Phone access requires an HTTPS address." }
            require(uri.rawUserInfo == null && uri.rawQuery == null && uri.rawFragment == null &&
                (uri.rawPath.isNullOrEmpty() || uri.rawPath == "/")) {
                "Use only the PC's address and port, without a path or sign-in details."
            }
            val host = uri.host?.removeSurrounding("[", "]")
            require(!host.isNullOrBlank() && !uri.rawAuthority.endsWith(':')) { "Enter a valid hostname or IP address and port." }
            return BridgeAddress(host, if (uri.port == -1) 8765 else uri.port)
        }
    }
}
