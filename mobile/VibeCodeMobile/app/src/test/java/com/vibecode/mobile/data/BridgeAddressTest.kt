package com.vibecode.mobile.data

import org.junit.Assert.*
import org.junit.Test

class BridgeAddressTest {
    @Test fun parsesDesktopOrigins() {
        assertEquals(BridgeAddress("desktop.local", 8765), BridgeAddress.parse(" desktop.local "))
        assertEquals(BridgeAddress("192.0.2.10", 18765), BridgeAddress.parse("https://192.0.2.10:18765/"))
        assertEquals(BridgeAddress("2001:db8::1", 8765), BridgeAddress.parse("2001:db8::1"))
        assertEquals(BridgeAddress("2001:db8::1", 18765), BridgeAddress.parse("[2001:db8::1]:18765"))
        assertEquals("https://[2001:db8::1]:18765/api/ping", BridgeAddress("2001:db8::1", 18765).url("/api/ping").toString())
    }

    @Test fun rejectsMalformedAndNonOriginAddresses() {
        listOf("", "https://", "http://desktop:8765", "desktop:bad", "desktop:", "desktop:0", "desktop:65536",
            "https://user:pass@desktop", "desktop/path", "desktop?query=1", "desktop#fragment", "bad host").forEach {
            assertThrows("must reject $it", IllegalArgumentException::class.java) { BridgeAddress.parse(it) }
        }
    }
}
