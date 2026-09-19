package com.vibecode.mobile.data

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL
import java.net.URLEncoder
import javax.net.ssl.HttpsURLConnection

/** A request the desktop refused, carried with the status so the UI can say something specific. */
class BridgeException(val status: Int, message: String) : IOException(message)

/**
 * The phone's half of the bridge protocol.
 *
 * Deliberately built on the JDK's own HttpsURLConnection rather than a networking library: the whole surface is
 * one host, and the thing that actually needs to be right — which certificate is trusted — is easier to be sure
 * of when the socket factory is set explicitly on every connection.
 */
class BridgeClient(
    private val host: String,
    private val port: Int,
    private val fingerprint: String?,
    private val token: String?,
    private val onCertificateSeen: ((String) -> Unit)? = null,
) {
    companion object {
        /** Long-poll ceiling on the desktop is 30s; stay just inside it so the server, not the phone, decides. */
        const val POLL_SECONDS = 25
        private const val CONNECT_TIMEOUT_MS = 8_000
        private const val READ_TIMEOUT_MS = 45_000
    }

    // ---------------- discovery and pairing ----------------

    suspend fun ping(): JSONObject = request("GET", "/api/ping", auth = false)

    suspend fun pair(code: String, deviceName: String): JSONObject =
        request("POST", "/api/pair", auth = false, body = JSONObject().put("code", code).put("name", deviceName))

    /**
     * Trades the secret baked into a generated APK for a real device token. Works exactly once per generated
     * APK; after that the desktop answers 403 and this app has to be regenerated on the PC.
     */
    suspend fun enroll(secret: String, deviceName: String): JSONObject =
        request("POST", "/api/enroll", auth = false, body = JSONObject().put("secret", secret).put("name", deviceName))

    suspend fun unpair() {
        request("POST", "/api/unpair", body = JSONObject())
    }

    // ---------------- reading ----------------

    suspend fun chats(version: Int, waitSeconds: Int): JSONObject =
        request("GET", "/api/chats?v=$version&wait=$waitSeconds")

    suspend fun messages(chatId: String, version: Int, waitSeconds: Int): TranscriptUpdate {
        val o = request("GET", "/api/chats/${chatId.esc()}/messages?v=$version&wait=$waitSeconds")
        val items = o.optJSONArray("items")
        return TranscriptUpdate(
            version = o.optInt("version"),
            unchanged = o.optBoolean("unchanged"),
            total = o.optInt("total"),
            base = o.optInt("base"),
            items = (0 until (items?.length() ?: 0)).map { Message.from(items!!.getJSONObject(it)) },
            chat = o.optJSONObject("chat")?.let { ChatSummary.from(it) },
            // Absent on an "unchanged" reply — the caller keeps whatever it already had.
            detail = o.optJSONObject("detail")?.let { ChatDetail.from(it) },
        )
    }

    suspend fun options(chatId: String): ChatOptions =
        ChatOptions.from(request("GET", "/api/chats/${chatId.esc()}/options"))

    suspend fun folders(): FolderList = FolderList.from(request("GET", "/api/folders"))

    // ---------------- the turn ----------------

    suspend fun send(chatId: String, text: String) {
        request("POST", "/api/chats/${chatId.esc()}/send", body = JSONObject().put("text", text))
    }

    suspend fun stop(chatId: String) {
        request("POST", "/api/chats/${chatId.esc()}/stop", body = JSONObject())
    }

    // ---------------- answering the agent ----------------

    suspend fun respondToPermission(chatId: String, requestId: String, allow: Boolean, always: Boolean = false) {
        request(
            "POST", "/api/chats/${chatId.esc()}/permission",
            body = JSONObject().put("requestId", requestId).put("allow", allow).put("always", always),
        )
    }

    /** Answers an AskUserQuestion card. [answers] maps each question's text to the labels chosen for it. */
    suspend fun answerQuestion(
        chatId: String,
        requestId: String,
        answers: List<Triple<String, List<String>, String>>,
    ) {
        val array = JSONArray()
        answers.forEach { (question, choices, custom) ->
            array.put(
                JSONObject()
                    .put("q", question)
                    .put("choices", JSONArray().apply { choices.forEach { put(it) } })
                    .put("custom", custom),
            )
        }
        request(
            "POST", "/api/chats/${chatId.esc()}/answer",
            body = JSONObject().put("requestId", requestId).put("answers", array),
        )
    }

    suspend fun decidePlan(
        chatId: String,
        requestId: String,
        approve: Boolean,
        autoAccept: Boolean,
        feedback: String,
    ) {
        request(
            "POST", "/api/chats/${chatId.esc()}/plan",
            body = JSONObject()
                .put("requestId", requestId)
                .put("approve", approve)
                .put("autoAccept", autoAccept)
                .put("feedback", feedback),
        )
    }

    // ---------------- session controls ----------------

    suspend fun setModel(chatId: String, model: String?) {
        request(
            "POST", "/api/chats/${chatId.esc()}/model",
            body = JSONObject().put("model", model ?: JSONObject.NULL),
        )
    }

    suspend fun setEffort(chatId: String, effort: String?) {
        request(
            "POST", "/api/chats/${chatId.esc()}/effort",
            body = JSONObject().put("effort", effort ?: JSONObject.NULL),
        )
    }

    suspend fun setMode(chatId: String, mode: String) {
        request("POST", "/api/chats/${chatId.esc()}/mode", body = JSONObject().put("mode", mode))
    }

    suspend fun setFastMode(chatId: String, on: Boolean) {
        request("POST", "/api/chats/${chatId.esc()}/fast", body = JSONObject().put("on", on))
    }

    // ---------------- queue, rewind, lifecycle ----------------

    suspend fun sendQueuedNow(chatId: String, ordinal: Int) = queue(chatId, ordinal, "send")

    suspend fun cancelQueued(chatId: String, ordinal: Int) = queue(chatId, ordinal, "cancel")

    private suspend fun queue(chatId: String, ordinal: Int, action: String) {
        request(
            "POST", "/api/chats/${chatId.esc()}/queue",
            body = JSONObject().put("ord", ordinal).put("action", action),
        )
    }

    /** Rewinds the workspace to just before the prompt at [ordinal]. Returns the desktop's summary of what moved. */
    suspend fun undoPrompt(chatId: String, ordinal: Int): String =
        request("POST", "/api/chats/${chatId.esc()}/undo", body = JSONObject().put("ord", ordinal))
            .optString("message")

    suspend fun togglePin(chatId: String): Boolean =
        request("POST", "/api/chats/${chatId.esc()}/pin", body = JSONObject()).optBoolean("pinned")

    suspend fun rename(chatId: String, title: String) {
        request("POST", "/api/chats/${chatId.esc()}/title", body = JSONObject().put("title", title))
    }

    suspend fun close(chatId: String) {
        request("POST", "/api/chats/${chatId.esc()}/close", body = JSONObject())
    }

    suspend fun newChat(cwd: String, provider: String?, title: String?): String =
        request(
            "POST", "/api/chats/new",
            body = JSONObject()
                .put("cwd", cwd)
                .put("provider", provider.orEmpty())
                .put("title", title.orEmpty()),
        ).optString("id")

    // ---------------- transport ----------------

    /** Chat ids are server-issued hex, but they land in a URL path, so they are escaped rather than trusted. */
    private fun String.esc(): String = URLEncoder.encode(this, "UTF-8").replace("+", "%20")

    private suspend fun request(
        method: String,
        path: String,
        auth: Boolean = true,
        body: JSONObject? = null,
    ): JSONObject = withContext(Dispatchers.IO) {
        val connection = URL("https://$host:$port$path").openConnection() as HttpsURLConnection
        connection.sslSocketFactory = PinnedTls.socketFactory(fingerprint, onCertificateSeen)
        connection.hostnameVerifier = PinnedTls.hostnameVerifier
        connection.requestMethod = method
        connection.connectTimeout = CONNECT_TIMEOUT_MS
        connection.readTimeout = READ_TIMEOUT_MS
        connection.setRequestProperty("Accept", "application/json")
        if (auth && token != null) connection.setRequestProperty("Authorization", "Bearer $token")

        try {
            if (body != null) {
                val bytes = body.toString().toByteArray()
                connection.doOutput = true
                connection.setRequestProperty("Content-Type", "application/json; charset=utf-8")
                connection.setFixedLengthStreamingMode(bytes.size)
                connection.outputStream.use { it.write(bytes) }
            }

            val status = connection.responseCode
            val stream = if (status in 200..299) connection.inputStream else connection.errorStream
            val text = stream?.bufferedReader()?.use { it.readText() }.orEmpty()
            if (status !in 200..299) {
                val reason = runCatching { JSONObject(text).optString("error") }.getOrNull()
                throw BridgeException(status, reason?.takeIf { it.isNotBlank() } ?: describe(status))
            }
            if (text.isBlank()) JSONObject() else JSONObject(text)
        } finally {
            connection.disconnect()
        }
    }

    private fun describe(status: Int) = when (status) {
        HttpURLConnection.HTTP_UNAUTHORIZED -> "This phone is not paired with that PC any more."
        HttpURLConnection.HTTP_FORBIDDEN -> "The PC refused that."
        HttpURLConnection.HTTP_NOT_FOUND -> "That chat is no longer open on the PC."
        HttpURLConnection.HTTP_BAD_REQUEST -> "The PC would not accept that."
        429 -> "Too many attempts — wait a minute and try again."
        503 -> "VibeCode on the PC is still starting up."
        else -> "The PC replied with an error ($status)."
    }
}
