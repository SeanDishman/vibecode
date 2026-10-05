package com.vibecode.mobile

import android.app.Application
import androidx.lifecycle.ViewModelStore
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.vibecode.mobile.data.BridgeClient
import com.vibecode.mobile.data.BridgeException
import com.vibecode.mobile.data.Pairing
import com.vibecode.mobile.data.PinnedTls
import com.vibecode.mobile.data.SecureStore
import kotlinx.coroutines.runBlocking
import org.json.JSONObject
import org.junit.After
import org.junit.Assert.*
import org.junit.Assume.assumeTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import java.net.URL
import javax.net.ssl.HttpsURLConnection

/** Exercises actual Android networking, Keystore and ViewModel against tests/bridge_fixture.py only. */
@RunWith(AndroidJUnit4::class)
class BridgeFlowTest {
    private val instrumentation = InstrumentationRegistry.getInstrumentation()
    private val app get() = instrumentation.targetContext.applicationContext as Application
    private val host = "10.0.2.2"
    private val port = 18765
    private lateinit var pin: String
    private val viewModels = ViewModelStore()

    @Before fun setup() {
        assumeTrue("Run only the isolated debug package", app.packageName == "com.vibecode.mobile.debug")
        pin = InstrumentationRegistry.getArguments().getString("fixturePin").orEmpty()
        assumeTrue("Start the loopback fixture and pass fixturePin", pin.length == 64)
        fixture("/fixture/reset")
        SecureStore(app).clear()
    }

    @After fun cleanup() {
        instrumentation.runOnMainSync { viewModels.clear() }
        // Opt-in only for a subsequent manual OS-credential check on the disposable emulator.
        val keepPairing = InstrumentationRegistry.getArguments().getString("keepFixturePairing") == "true"
        if (app.packageName == "com.vibecode.mobile.debug" && !keepPairing) SecureStore(app).clear()
    }

    private fun fixture(path: String, body: JSONObject? = null): JSONObject {
        val connection = URL("https://$host:$port$path").openConnection() as HttpsURLConnection
        connection.sslSocketFactory = PinnedTls.socketFactory(pin)
        connection.hostnameVerifier = PinnedTls.hostnameVerifier
        connection.connectTimeout = 5000
        connection.readTimeout = 5000
        try {
            if (body != null) {
                connection.requestMethod = "POST"
                connection.doOutput = true
                connection.outputStream.use { it.write(body.toString().toByteArray(Charsets.UTF_8)) }
            }
            return JSONObject(connection.inputStream.bufferedReader().use { it.readText() })
        } finally { connection.disconnect() }
    }

    private suspend fun pair(): Pairing {
        val result = BridgeClient(host, port, pin, null).pair("123456", "Isolated Android test")
        return Pairing(host, port, pin, result.getString("token"), result.getString("host"))
    }

    private fun client(pairing: Pairing) = BridgeClient(host, port, pin, pairing.token)

    private fun attemptsAt(state: JSONObject, path: String): Int {
        val attempts = state.getJSONArray("attempts")
        return (0 until attempts.length()).count { attempts.getJSONObject(it).getString("path") == path }
    }

    @Test fun pairingDiscoveryPersistsIdentityAndStartsLocked() = runBlocking {
        lateinit var vm: AppViewModel
        main {
            vm = AppViewModel(app)
            viewModels.put("fixture", vm)
            vm.onAddressChanged("https://$host:$port/")
            vm.discover()
        }
        await { vm.state.value.pair.step == PairStep.Confirm || vm.state.value.pair.error.isNotEmpty() }
        assertEquals("", vm.state.value.pair.error)
        assertEquals(pin, vm.state.value.pair.fingerprint)
        main { vm.confirmIdentity(); vm.onCodeChanged("123456"); vm.submitCode() }
        await { vm.state.value.pairing != null || vm.state.value.pair.error.isNotEmpty() }
        assertEquals("", vm.state.value.pair.error)
        assertTrue(vm.state.value.locked)
        assertEquals(vm.state.value.pairing, SecureStore(app).load())
        main { vm.onUnlocked() }
        await { vm.state.value.chats.size == 2 }
        main { vm = AppViewModel(app); viewModels.put("fixture", vm) }
        assertTrue(vm.state.value.locked)
        assertEquals(Screen.Chats, vm.state.value.screen)
        // Exercise generated-APK enrollment's request contract with the same disposable Android identity.
        val enrolled = BridgeClient(host, port, pin, null).enroll("synthetic-enrollment-secret", "Fixture phone")
        assertEquals("synthetic-fixture-token", enrolled.getString("token"))
    }

    @Test fun signedClientCoversChatControlsAndLifecycle() = runBlocking {
        val api = client(pair())
        assertEquals(2, api.chats(-1, 0).getJSONArray("chats").length())
        assertEquals(2, api.messages("fixture-a", -1, 0).applyTo(emptyList()).size)
        assertEquals("fixture-model", api.options("fixture-a").models.single().value)
        assertEquals("C:\\fixture", api.folders().preferred)
        assertEquals("fixture-new", api.newChat("C:\\fixture", "codex", "Created fixture"))
        api.send("fixture-a", "Synthetic prompt: café ☕\nsecond line")
        api.stop("fixture-a")
        api.respondToPermission("fixture-a", "permission-1", allow = true, always = true)
        api.answerQuestion("fixture-a", "question-1", listOf(Triple("Which?", listOf("First"), "Custom answer")))
        api.decidePlan("fixture-a", "plan-1", approve = false, autoAccept = false, feedback = "Revise the fixture")
        api.setModel("fixture-a", null)
        api.setEffort("fixture-a", null)
        api.setMode("fixture-a", "plan")
        api.setFastMode("fixture-a", true)
        api.sendQueuedNow("fixture-a", 0)
        api.cancelQueued("fixture-a", 1)
        assertEquals("Fixture rewind complete", api.undoPrompt("fixture-a", 0))
        assertTrue(api.togglePin("fixture-a"))
        api.rename("fixture-a", "Renamed fixture")
        api.close("fixture-new")
        val actions = fixture("/fixture/state").getJSONArray("actions")
        assertEquals(16, actions.length())
        val send = (0 until actions.length()).map { actions.getJSONObject(it) }.single { it.getString("path").endsWith("/send") }
        assertEquals("Synthetic prompt: café ☕\nsecond line", send.getJSONObject("body").getString("text"))
        api.unpair()
        try { api.chats(-1, 0); fail("Revoked token must fail") } catch (e: BridgeException) { assertEquals(401, e.status) }
    }

    @Test fun restartRetriesOnlyRejectedEpochAndRedirectsAreRejected() = runBlocking {
        val api = client(pair())
        fixture("/fixture/control", JSONObject().put("restart", true))
        api.send("fixture-a", "After simulated desktop restart")
        val state = fixture("/fixture/state")
        val actions = state.getJSONArray("actions")
        assertEquals(actions.toString(), 1, (0 until actions.length()).count {
            actions.getJSONObject(it).getString("path") == "/api/chats/fixture-a/send"
        })
        // A cancelled ViewModel's already-started GET may finish during the next test. Count this mutation only.
        assertEquals(2, attemptsAt(state, "/api/chats/fixture-a/send"))
        try { api.rename("fixture-a", "redirect-fixture"); fail("Redirect must be returned, never followed") }
        catch (e: BridgeException) { assertEquals(307, e.status) }
        fixture("/fixture/control", JSONObject().put("revoked", true))
        val before = attemptsAt(fixture("/fixture/state"), "/api/chats/fixture-a/stop")
        try { api.stop("fixture-a"); fail("Revocation must fail") }
        catch (e: BridgeException) { assertEquals(401, e.status) }
        assertEquals(before + 1, attemptsAt(fixture("/fixture/state"), "/api/chats/fixture-a/stop"))
    }

    @Test fun wrongPinAndMissingCredentialsCannotReadChats() = runBlocking {
        val pairing = pair()
        try { BridgeClient(host, port, "0".repeat(64), pairing.token).chats(-1, 0); fail("Wrong pin was accepted") }
        catch (_: javax.net.ssl.SSLHandshakeException) { }
        try { BridgeClient(host, port, null, pairing.token).chats(-1, 0); fail("Unpinned request was accepted") }
        catch (_: IllegalArgumentException) { }
        assertEquals(0, fixture("/fixture/state").getJSONArray("attempts").length())
    }

    @Test fun viewModelKeepsDraftsAndOptionsScopedToCurrentChat() = runBlocking {
        val pairing = pair()
        SecureStore(app).save(pairing)
        lateinit var vm: AppViewModel
        main { vm = AppViewModel(app); viewModels.put("fixture", vm); vm.onUnlocked() }
        await { vm.state.value.chats.size == 2 }
        val a = vm.state.value.chats[0]
        val b = vm.state.value.chats[1]
        fixture("/fixture/control", JSONObject().put("options_delay", 0.6))
        main {
            vm.openChat(a); vm.showSheet(Sheet.Controls)
            vm.closeChatScreen(); vm.openChat(b); vm.showSheet(Sheet.Controls)
        }
        await { vm.state.value.options != null && !vm.state.value.optionsBusy }
        assertEquals(Screen.Chat(b.id), vm.state.value.screen)
        fixture("/fixture/control", JSONObject().put("fail_send", true).put("send_delay", 0.5))
        main {
            vm.openChat(a); vm.onDraftChanged("Original pending prompt"); vm.send()
            vm.onDraftChanged("New text for first chat"); vm.openChat(b); vm.onDraftChanged("Second chat draft")
        }
        await { vm.state.value.notice == "Fixture send failure" }
        assertEquals("Second chat draft", vm.state.value.draft)
        main { vm.openChat(a) }
        assertEquals("Original pending prompt\n\nNew text for first chat", vm.state.value.draft)
        assertFalse(vm.state.value.sending)
        main { vm.openNewChat() }
        await { !vm.state.value.newChat.loading }
        main { vm.onNewChatTitle("Created once"); vm.createChat(); vm.createChat() }
        await { vm.state.value.screen == Screen.Chat("fixture-new") }
        assertNotNull(vm.state.value.openChat)
        val actions = fixture("/fixture/state").getJSONArray("actions")
        assertEquals(1, (0 until actions.length()).count { actions.getJSONObject(it).getString("path") == "/api/chats/new" })
        fixture("/fixture/control", JSONObject().put("revoked", true))
        main { vm.stopTurn() }
        await { vm.state.value.pairing == null }
        assertEquals(Screen.Pair, vm.state.value.screen)
        assertTrue(vm.state.value.draft.isEmpty())
    }

    private fun main(action: () -> Unit) = instrumentation.runOnMainSync(action)
    private fun await(condition: () -> Boolean) {
        val until = System.nanoTime() + 15_000_000_000L
        while (!condition()) {
            if (System.nanoTime() > until) fail("Timed out waiting for fixture state")
            Thread.sleep(50)
        }
    }
}
