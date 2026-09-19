package com.vibecode.mobile.data

import java.net.InetAddress
import java.net.Socket
import java.security.MessageDigest
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import javax.net.ssl.HostnameVerifier
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLSocket
import javax.net.ssl.SSLSocketFactory
import javax.net.ssl.X509TrustManager

/**
 * Certificate pinning for the desktop bridge.
 *
 * The PC signs its own certificate — there is no certificate authority on a home network that could vouch for it,
 * and asking the user to install a root CA on their phone would be far more dangerous than this. So the app
 * records the exact certificate at pairing time and from then on accepts nothing else. That makes the pairing
 * moment the only window in which a machine on the same Wi-Fi could impersonate the PC, and the safety code the
 * user compares closes it: the code is derived from the very certificate being offered, so an impostor's code
 * cannot match the one the desktop is displaying.
 *
 * Hostname verification is deliberately replaced rather than skipped. A pinned leaf certificate is a strictly
 * stronger statement than "the name matches", and the PC's address legitimately changes when DHCP reshuffles the
 * network — pinning survives that, a name check would not.
 */
object PinnedTls {

    fun fingerprintOf(certificate: X509Certificate): String =
        MessageDigest.getInstance("SHA-256").digest(certificate.encoded)
            .joinToString("") { "%02X".format(it) }

    /** Formats a fingerprint the way the desktop's "safety code" line does. */
    fun safetyCode(fingerprint: String): String =
        if (fingerprint.length >= 8) "${fingerprint.substring(0, 4)}-${fingerprint.substring(4, 8)}" else ""

    /**
     * @param pin the fingerprint that must be presented, or null during pairing discovery — before the user has
     *        anything to pin, and before any credential exists to leak.
     * @param onSeen receives whatever certificate the server actually offered, so the pairing screen can show its
     *        safety code for comparison.
     */
    fun socketFactory(pin: String?, onSeen: ((String) -> Unit)? = null): SSLSocketFactory {
        val trust = object : X509TrustManager {
            override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) =
                throw CertificateException("this app is never a TLS server")

            override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
                val leaf = chain?.firstOrNull() ?: throw CertificateException("the server sent no certificate")
                val actual = fingerprintOf(leaf)
                onSeen?.invoke(actual)
                if (pin != null && !pin.equals(actual, ignoreCase = true)) {
                    throw CertificateException(
                        "This is not the PC you paired with. Its security key changed, or something else is " +
                            "answering on that address."
                    )
                }
            }

            override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
        }

        // "TLS" negotiates the best protocol the device supports, which is TLS 1.3 on API 29+ and TLS 1.2 below.
        // Asking for "TLSv1.2" by name, as this used to, caps every modern phone at 1.2 for no reason.
        val context = SSLContext.getInstance("TLS").apply {
            init(null, arrayOf(trust), java.security.SecureRandom())
        }
        return ModernProtocolsOnly(context.socketFactory)
    }

    /** Pinning has already established identity; the certificate's subject name is not the check that matters. */
    val hostnameVerifier = HostnameVerifier { _, _ -> true }

    /**
     * Forces every socket to TLS 1.2/1.3.
     *
     * Android still *enables* TLS 1.0 and 1.1 by default on some OEM images, and a downgrade is exactly what an
     * attacker on the same Wi-Fi would try. The desktop only offers 1.2 and 1.3, so refusing anything older here
     * costs nothing and removes the negotiation entirely.
     */
    private class ModernProtocolsOnly(private val delegate: SSLSocketFactory) : SSLSocketFactory() {

        private fun harden(socket: Socket): Socket {
            if (socket is SSLSocket) {
                val allowed = socket.supportedProtocols.filter { it == "TLSv1.2" || it == "TLSv1.3" }
                if (allowed.isNotEmpty()) socket.enabledProtocols = allowed.toTypedArray()
            }
            return socket
        }

        override fun getDefaultCipherSuites(): Array<String> = delegate.defaultCipherSuites
        override fun getSupportedCipherSuites(): Array<String> = delegate.supportedCipherSuites

        override fun createSocket(): Socket = harden(delegate.createSocket())
        override fun createSocket(host: String?, port: Int): Socket = harden(delegate.createSocket(host, port))
        override fun createSocket(host: String?, port: Int, localHost: InetAddress?, localPort: Int): Socket =
            harden(delegate.createSocket(host, port, localHost, localPort))
        override fun createSocket(host: InetAddress?, port: Int): Socket = harden(delegate.createSocket(host, port))
        override fun createSocket(
            address: InetAddress?,
            port: Int,
            localAddress: InetAddress?,
            localPort: Int,
        ): Socket = harden(delegate.createSocket(address, port, localAddress, localPort))
        override fun createSocket(s: Socket?, host: String?, port: Int, autoClose: Boolean): Socket =
            harden(delegate.createSocket(s, host, port, autoClose))
    }
}
