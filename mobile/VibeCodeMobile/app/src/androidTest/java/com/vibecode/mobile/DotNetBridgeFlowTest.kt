package com.vibecode.mobile

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.vibecode.mobile.data.BridgeClient
import com.vibecode.mobile.data.BridgeException
import com.vibecode.mobile.data.DeviceIdentity
import com.vibecode.mobile.data.PinnedTls
import kotlinx.coroutines.runBlocking
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Assume.assumeTrue
import org.junit.Test
import org.junit.runner.RunWith
import java.net.URL
import javax.net.ssl.HttpsURLConnection

/** Actual .NET PhoneBridgeService; only its provider adapter and chat data are synthetic. */
@RunWith(AndroidJUnit4::class)
class DotNetBridgeFlowTest {
    private val host = "10.0.2.2"
    private val port = 18766
    private val pin = InstrumentationRegistry.getArguments().getString("dotnetPin").orEmpty()

    private fun control(path: String): JSONObject {
        val connection = URL("https://$host:18767$path").openConnection() as HttpsURLConnection
        connection.sslSocketFactory = PinnedTls.socketFactory(pin)
        connection.hostnameVerifier = PinnedTls.hostnameVerifier
        connection.connectTimeout = 5000
        connection.readTimeout = 20000
        return try { JSONObject(connection.inputStream.bufferedReader().use { it.readText() }) }
        finally { connection.disconnect() }
    }

    private fun status(path: String, token: String?, epoch: String, validProof: Boolean = true): Int {
        val connection = URL("https://$host:$port$path").openConnection() as HttpsURLConnection
        connection.sslSocketFactory = PinnedTls.socketFactory(pin)
        connection.hostnameVerifier = PinnedTls.hostnameVerifier
        connection.connectTimeout = 5000
        connection.readTimeout = 5000
        if (token != null) {
            connection.setRequestProperty("Authorization", "Bearer $token")
            DeviceIdentity.headers("GET", if (validProof) path else "$path-tampered", byteArrayOf(), token, epoch)
                .forEach { (key, value) -> connection.setRequestProperty(key, value) }
        }
        return try { connection.responseCode } finally { connection.disconnect() }
    }

    @Test fun actualDotNetValidatesAndroidSignaturesDispatchAndRestart() = runBlocking {
        assumeTrue(InstrumentationRegistry.getInstrumentation().targetContext.packageName == "com.vibecode.mobile.debug")
        assumeTrue("Start VibeCode.PhoneAndroidFixture and pass dotnetPin", pin.length == 64)
        val anonymous = BridgeClient(host, port, pin, null)
        val ping = anonymous.ping()
        val result = anonymous.pair("123456", "Synthetic Android-to-.NET test")
        val token = result.getString("token")
        val api = BridgeClient(host, port, pin, token)
        val list = api.chats(-1, 0).getJSONArray("chats")
        assertEquals(1, list.length())
        val id = list.getJSONObject(0).getString("id")
        val initial = api.messages(id, -1, 0)
        assertTrue(initial.items.any { it.text == "Synthetic .NET response" })
        assertEquals(3, initial.items.count { it.requestId.isNotEmpty() })
        assertTrue(api.options(id).models.isNotEmpty())
        assertTrue(api.folders().preferred.endsWith("synthetic-project"))

        val epoch = ping.getString("authEpoch")
        assertEquals(401, status("/api/chats?v=-1&wait=0", null, epoch))
        assertEquals(403, status("/api/chats?v=-1&wait=0", token, epoch, validProof = false))
        for (path in listOf("/api/settings", "/api/files", "/api/exec", "/api/ws"))
            assertEquals("Route must remain unavailable: $path", 404, status(path, token, epoch))
        try { api.setModel(id, "not-a-real-fixture-model"); fail("Unknown model accepted") }
        catch (e: BridgeException) { assertEquals(400, e.status) }
        try { api.setMode(id, "arbitrary-mode"); fail("Unknown mode accepted") }
        catch (e: BridgeException) { assertEquals(400, e.status) }
        try { api.newChat("Z:\\nonexistent-vibecode-fixture-directory", "codex", "No provider may start"); fail("Missing folder accepted") }
        catch (e: BridgeException) { assertEquals(400, e.status) }

        api.setModel(id, api.options(id).models.first().value)
        api.setEffort(id, null)
        api.setMode(id, "plan")
        api.respondToPermission(id, "permission-dotnet", allow = false)
        api.answerQuestion(id, "question-dotnet", listOf(Triple("Which fixture?", listOf("First"), "")))
        api.decidePlan(id, "plan-dotnet", approve = false, autoAccept = false, feedback = "Synthetic revision")
        api.rename(id, "Renamed from real Android")
        val unicode = "Synthetic Unicode: caf\u00e9 \u2615\nsecond line"
        api.send(id, unicode)
        val deadline = System.nanoTime() + 20_000_000_000L
        var state = control("/fixture/state")
        while (state.getInt("sends") == 0 && System.nanoTime() < deadline) {
            Thread.sleep(100)
            state = control("/fixture/state")
        }
        assertEquals("Only the in-memory adapter receives the prompt", 1, state.getInt("sends"))
        val content = JSONArray(state.getString("lastContent"))
        assertTrue(content.getJSONObject(0).getString("text").contains(unicode))
        assertEquals(0, state.getInt("startCalls"))
        assertEquals(3, state.getInt("permissionReplies"))
        assertTrue(state.getInt("modelCalls") > 0)
        assertEquals("plan", state.getString("mode"))
        api.send(id, "Queued synthetic prompt")
        api.cancelQueued(id, 0)
        api.stop(id)
        assertEquals(1, control("/fixture/state").getInt("interrupts"))
        val transcript = api.messages(id, -1, 0)
        assertTrue(transcript.items.any { it.text == unicode })
        assertTrue(transcript.items.any { it.text == "Fixture adapter received Unicode prompt" })

        control("/fixture/restart")
        assertEquals(1, api.chats(-1, 0).getJSONArray("chats").length())
        // A new process builds the watched transcript on its next dispatcher tick, as in the app's long poll.
        val restarted = api.messages(id, 1000000, 2)
        assertEquals(0, restarted.base)
        assertTrue(restarted.applyTo(emptyList()).any { it.text == unicode })
        api.unpair()
        try { api.chats(-1, 0); fail("Revoked device still reads .NET chats") }
        catch (e: BridgeException) { assertEquals(401, e.status) }
        assertEquals(1, control("/fixture/state").getInt("sends"))
    }
}
