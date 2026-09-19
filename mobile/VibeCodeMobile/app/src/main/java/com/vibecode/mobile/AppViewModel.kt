package com.vibecode.mobile

import android.app.Application
import android.os.Build
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.vibecode.mobile.data.BridgeClient
import com.vibecode.mobile.data.BridgeException
import com.vibecode.mobile.data.ChatDetail
import com.vibecode.mobile.data.ChatOptions
import com.vibecode.mobile.data.ChatSummary
import com.vibecode.mobile.data.Discovery
import com.vibecode.mobile.data.EnrolmentConfig
import com.vibecode.mobile.data.FolderList
import com.vibecode.mobile.data.Message
import com.vibecode.mobile.data.Pairing
import com.vibecode.mobile.data.PinnedTls
import com.vibecode.mobile.data.SecureStore
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.io.IOException
import java.net.HttpURLConnection

/** Which surface is on screen. */
sealed interface Screen {
    data object Pair : Screen
    /** A generated APK setting itself up against the PC that built it. */
    data object Enrolling : Screen
    data object Chats : Screen
    data class Chat(val id: String) : Screen
    data object NewChat : Screen
}

/** How far through pairing the user is. */
enum class PairStep { Address, Confirm, Code }

/** Whether the PC is actually answering right now. Shown as a dot rather than a dialog — a phone on the move
 *  drops off Wi-Fi constantly and a modal for every hiccup would be unusable. */
enum class Link { Connecting, Online, Offline }

/** Which panel the chat screen's bottom sheet is showing, if any. */
enum class Sheet { None, Controls, Todos, Files, Commands }

data class PairState(
    val step: PairStep = PairStep.Address,
    val address: String = "",
    val pcName: String = "",
    val fingerprint: String = "",
    val code: String = "",
    val busy: Boolean = false,
    val error: String = "",
) {
    val safetyCode: String get() = PinnedTls.safetyCode(fingerprint)
}

/** The "start something new" screen's own state. */
data class NewChatState(
    val loading: Boolean = true,
    val folders: FolderList = FolderList(),
    val cwd: String = "",
    val provider: String = "",
    val title: String = "",
    val busy: Boolean = false,
    val error: String = "",
)

data class UiState(
    val screen: Screen = Screen.Pair,
    val pairing: Pairing? = null,
    val pair: PairState = PairState(),
    val link: Link = Link.Connecting,
    val linkError: String = "",
    val chats: List<ChatSummary> = emptyList(),
    val openChat: ChatSummary? = null,
    val messages: List<Message> = emptyList(),
    val detail: ChatDetail = ChatDetail(),
    val options: ChatOptions? = null,
    val optionsBusy: Boolean = false,
    val sheet: Sheet = Sheet.None,
    val newChat: NewChatState = NewChatState(),
    val draft: String = "",
    val sending: Boolean = false,
    val notice: String = "",
    /** Name of the PC a generated APK belongs to; blank on a generic build. */
    val boundTo: String = "",
    val enrolBusy: Boolean = false,
    val enrolError: String = "",
    /** What enrolment is doing this second — the address being tried, or the wait before the next round. Without
     *  this the screen is a spinner that says "Connecting" forever and tells the user nothing they can act on. */
    val enrolProgress: String = "",
    /** How many full passes over every candidate address have failed. Shown once it is more than one, because at
     *  that point "it is still trying" is genuinely different information from "it just started". */
    val enrolRound: Int = 0,
    /** Set when retrying is pointless — the coupon is spent, or this app was disconnected on purpose. */
    val enrolFatal: Boolean = false,
    /** Set while a rewind is in flight; the transcript is not trustworthy to act on until it lands. */
    val undoing: Boolean = false,
    val locked: Boolean = false,
) {
    val chatsLoaded: Boolean get() = link == Link.Online || chats.isNotEmpty()
}

class AppViewModel(app: Application) : AndroidViewModel(app) {

    private val store = SecureStore(app)
    private val _state = MutableStateFlow(UiState())
    val state: StateFlow<UiState> = _state.asStateFlow()

    private var chatsJob: Job? = null
    private var messagesJob: Job? = null
    private var enrolJob: Job? = null
    private var chatsVersion = -1
    private var messagesVersion = -1

    private val deviceName = "${Build.MANUFACTURER} ${Build.MODEL}".trim().ifBlank { "Phone" }

    /** The PC this APK was generated for, or null on a generic build that has to be paired by hand. */
    private val baked = EnrolmentConfig.load(app)

    init {
        val saved = store.load()?.takeIf { trustworthy(it) }
        when {
            saved != null -> {
                // Starts locked, so no transcript is ever painted before the user has been asked who they are.
                // The activity clears this immediately on a device with no screen lock, where the gate cannot
                // be opened.
                _state.update {
                    it.copy(
                        screen = Screen.Chats,
                        pairing = saved,
                        link = Link.Connecting,
                        locked = true,
                        boundTo = baked?.pcName.orEmpty(),
                    )
                }
                startChatsPoll()
            }

            baked != null -> {
                _state.update { it.copy(screen = Screen.Enrolling, link = Link.Connecting, boundTo = baked.pcName) }
                enroll()
            }

            else -> _state.update { it.copy(screen = Screen.Pair, link = Link.Offline) }
        }
    }

    /**
     * Whether a stored pairing may be used.
     *
     * On a generated APK the certificate fingerprint is part of a signed package, so it is the authority — a
     * stored pairing that points anywhere else was not put there by this app's own enrolment and is discarded.
     * That closes the one gap a writable preferences file would otherwise leave: swapping the pin for someone
     * else's and having the app happily talk to a machine it was never built for.
     */
    private fun trustworthy(pairing: Pairing): Boolean {
        val config = baked ?: return true
        return pairing.fingerprint.equals(config.fingerprint, ignoreCase = true)
    }

    // ---------------- enrolment (generated APK) ----------------

    /**
     * Redeems the one-time secret baked into this APK for a real device token.
     *
     * Every candidate address the desktop knew about is tried in turn, because a PC with both Wi-Fi and Ethernet
     * has several and only one of them is the one the phone can actually see. The certificate pin is the same for
     * all of them, so a wrong guess fails the handshake rather than leaking the secret.
     *
     * Two things this deliberately does NOT do. It does not give up: the PC is very often simply not awake yet, or
     * the phone joined the Wi-Fi a moment later, and a screen that dead-ends after one pass makes the app look
     * broken when waiting ten seconds would have worked. And it does not hide what it is doing — every address
     * attempted is named on screen, because "Connecting…" with no detail is unactionable for the one person who
     * could actually fix the network.
     *
     * After the first failed pass it also asks the network where the PC went, which is what rescues an app whose
     * baked-in addresses have gone stale.
     */
    fun enroll() {
        val config = baked ?: return
        enrolJob?.cancel()
        _state.update {
            it.copy(
                screen = Screen.Enrolling,
                enrolBusy = true,
                enrolError = "",
                enrolProgress = "",
                enrolRound = 0,
                enrolFatal = false,
            )
        }
        enrolJob = viewModelScope.launch {
            var backoff = 3_000L
            var round = 0
            while (isActive) {
                round++
                _state.update { it.copy(enrolBusy = true, enrolRound = round, enrolProgress = "") }

                val candidates = LinkedHashSet(config.hosts)
                if (round > 1) {
                    _state.update { it.copy(enrolProgress = "Looking for ${config.pcName} on this network…") }
                    // Only PCs presenting the certificate this APK was built for; everything else that answers
                    // the broadcast is discarded before it is ever dialled.
                    candidates.addAll(Discovery.hostsMatching(config.fingerprint, config.port))
                }

                var lastError = ""
                for (host in candidates) {
                    if (!isActive) return@launch
                    _state.update { it.copy(enrolProgress = "Trying $host…") }
                    try {
                        val result = BridgeClient(host, config.port, config.fingerprint, null)
                            .enroll(config.secret, deviceName)
                        val token = result.optString("token")
                        if (token.isBlank()) throw IOException("The PC did not send a key back.")
                        val pairing = Pairing(
                            host = host,
                            port = config.port,
                            // Always the baked value, never anything the server claimed: the APK's signature is
                            // what makes this trustworthy and a response body is not.
                            fingerprint = config.fingerprint,
                            token = token,
                            pcName = result.optString("host").ifBlank { config.pcName },
                        )
                        store.save(pairing)
                        _state.update {
                            it.copy(
                                screen = Screen.Chats,
                                pairing = pairing,
                                link = Link.Connecting,
                                enrolBusy = false,
                                enrolError = "",
                                enrolProgress = "",
                            )
                        }
                        startChatsPoll()
                        return@launch
                    } catch (e: Exception) {
                        // A 403 means the coupon is spent — this app was already used to set up a phone, and no
                        // amount of retrying changes that. Say so once and stop.
                        if (e is BridgeException && e.status == HttpURLConnection.HTTP_FORBIDDEN) {
                            _state.update {
                                it.copy(
                                    enrolBusy = false,
                                    enrolFatal = true,
                                    enrolProgress = "",
                                    enrolError = friendly(e),
                                )
                            }
                            return@launch
                        }
                        lastError = friendly(e)
                    }
                }

                _state.update {
                    it.copy(
                        enrolBusy = false,
                        enrolError = lastError.ifBlank { "Couldn't reach ${config.pcName} on this network." },
                    )
                }

                // Count down out loud rather than freezing, so the screen is visibly alive between attempts.
                var left = backoff
                while (left > 0 && isActive) {
                    _state.update { it.copy(enrolProgress = "Trying again in ${(left + 999) / 1000}s") }
                    delay(if (left > 1_000L) 1_000L else left)
                    left -= 1_000L
                }
                backoff = (backoff * 2).coerceAtMost(30_000L)
            }
        }
    }

    private fun client(pairing: Pairing) =
        BridgeClient(pairing.host, pairing.port, pairing.fingerprint, pairing.token)

    /** The chat the user is looking at, or null when the screen is not a chat. */
    private fun openChatId(): String? = (_state.value.screen as? Screen.Chat)?.id

    // ---------------- app lock ----------------

    /** Called when the app is unlocked by biometrics or device credential — or when no lock exists to satisfy. */
    fun onUnlocked() = _state.update { it.copy(locked = false) }

    /** Re-locks. No-op when nothing is paired: there is no secret on screen to protect yet. */
    fun lock() = _state.update { if (it.pairing == null) it else it.copy(locked = true) }

    // ---------------- pairing ----------------

    fun onAddressChanged(value: String) =
        _state.update { it.copy(pair = it.pair.copy(address = value, error = "")) }

    fun onCodeChanged(value: String) =
        _state.update { it.copy(pair = it.pair.copy(code = value.filter(Char::isDigit).take(6), error = "")) }

    fun backToAddress() =
        _state.update { it.copy(pair = it.pair.copy(step = PairStep.Address, code = "", error = "")) }

    /** Step one: reach the PC and find out which certificate it is offering, so the user can vouch for it. */
    fun discover() {
        val raw = _state.value.pair.address.trim()
        val host = raw.substringBefore(':').trim()
        val port = raw.substringAfter(':', "8765").trim().toIntOrNull() ?: 8765
        if (host.isEmpty()) {
            _state.update { it.copy(pair = it.pair.copy(error = "Type the address shown on your PC.")) }
            return
        }

        _state.update { it.copy(pair = it.pair.copy(busy = true, error = "")) }
        viewModelScope.launch {
            // No pin yet — this is the one exchange that happens before trust exists, which is exactly why the
            // user is about to be asked to compare a safety code before anything is stored.
            var seen = ""
            val probe = BridgeClient(host, port, null, null) { seen = it }
            try {
                val ping = probe.ping()
                if (ping.optString("app") != "vibecode") throw IOException("That address is not running VibeCode.")
                _state.update {
                    it.copy(
                        pair = it.pair.copy(
                            step = PairStep.Confirm,
                            busy = false,
                            address = "$host:$port",
                            pcName = ping.optString("name").ifBlank { host },
                            fingerprint = seen,
                        )
                    )
                }
            } catch (e: Exception) {
                _state.update { it.copy(pair = it.pair.copy(busy = false, error = reachabilityMessage(e, host, port))) }
            }
        }
    }

    fun confirmIdentity() = _state.update { it.copy(pair = it.pair.copy(step = PairStep.Code, error = "")) }

    /** Step two: trade the six-digit code for a token, pinned to the certificate the user just approved. */
    fun submitCode() {
        val pair = _state.value.pair
        if (pair.code.length != 6) {
            _state.update { it.copy(pair = it.pair.copy(error = "The code is six digits.")) }
            return
        }
        val host = pair.address.substringBefore(':')
        val port = pair.address.substringAfter(':', "8765").toIntOrNull() ?: 8765

        _state.update { it.copy(pair = it.pair.copy(busy = true, error = "")) }
        viewModelScope.launch {
            try {
                val client = BridgeClient(host, port, pair.fingerprint, null)
                val result = client.pair(pair.code, deviceName)
                val token = result.optString("token")
                if (token.isBlank()) throw IOException("The PC did not send a key back.")
                val saved = Pairing(
                    host = host,
                    port = port,
                    // Trust what the handshake actually presented, never a fingerprint from the response body:
                    // a hostile server could claim any string it liked there.
                    fingerprint = pair.fingerprint,
                    token = token,
                    pcName = result.optString("host").ifBlank { pair.pcName },
                )
                store.save(saved)
                _state.update {
                    it.copy(
                        screen = Screen.Chats,
                        pairing = saved,
                        pair = PairState(),
                        link = Link.Connecting,
                    )
                }
                startChatsPoll()
            } catch (e: Exception) {
                _state.update { it.copy(pair = it.pair.copy(busy = false, error = friendly(e))) }
            }
        }
    }

    fun unpair() {
        val pairing = _state.value.pairing
        stopPolling()
        store.clear()
        // A generated app cannot simply pair again: its enrolment secret was spent the first time it ran. Saying
        // so on a dedicated screen is more honest than dropping the user on an address box that will never work.
        _state.value = if (baked != null) {
            UiState(
                screen = Screen.Enrolling,
                link = Link.Offline,
                boundTo = baked.pcName,
                enrolFatal = true,
                enrolError = "This app has been disconnected. Generate a new one on ${baked.pcName} to set it up again.",
            )
        } else {
            UiState(screen = Screen.Pair, link = Link.Offline)
        }
        // Best effort: tell the PC to drop this device too, so a stolen phone is not still on its list.
        if (pairing != null) viewModelScope.launch { runCatching { client(pairing).unpair() } }
    }

    // ---------------- navigation ----------------

    fun openChat(chat: ChatSummary) {
        messagesVersion = -1
        _state.update {
            it.copy(
                screen = Screen.Chat(chat.id),
                openChat = chat,
                messages = emptyList(),
                detail = ChatDetail(),
                options = null,
                sheet = Sheet.None,
                draft = "",
            )
        }
        startMessagesPoll(chat.id)
    }

    fun closeChatScreen() {
        messagesJob?.cancel()
        messagesJob = null
        _state.update {
            it.copy(
                screen = Screen.Chats,
                openChat = null,
                messages = emptyList(),
                detail = ChatDetail(),
                options = null,
                sheet = Sheet.None,
            )
        }
    }

    fun showSheet(sheet: Sheet) {
        _state.update { it.copy(sheet = sheet) }
        // The control sheet is the only one whose contents are not already in the poll payload.
        if (sheet == Sheet.Controls || sheet == Sheet.Commands) loadOptions()
    }

    fun dismissSheet() = _state.update { it.copy(sheet = Sheet.None) }

    fun dismissNotice() = _state.update { it.copy(notice = "") }

    // ---------------- polling ----------------

    private fun stopPolling() {
        chatsJob?.cancel(); chatsJob = null
        messagesJob?.cancel(); messagesJob = null
        enrolJob?.cancel(); enrolJob = null
        chatsVersion = -1
        messagesVersion = -1
    }

    /**
     * The chat list poll runs for as long as the app is paired, because it is also the "is the PC there" signal
     * that drives the status dot. It is a long poll, so an idle desktop costs one open socket, not a request a
     * second.
     */
    private fun startChatsPoll() {
        chatsJob?.cancel()
        chatsVersion = -1
        chatsJob = viewModelScope.launch {
            var backoff = 1_000L
            while (isActive) {
                val pairing = _state.value.pairing ?: break
                try {
                    val result = client(pairing).chats(chatsVersion, BridgeClient.POLL_SECONDS)
                    chatsVersion = result.optInt("version", chatsVersion)
                    if (!result.optBoolean("unchanged")) {
                        val chats = ChatSummary.list(result.optJSONArray("chats"))
                        _state.update { current ->
                            current.copy(
                                chats = chats,
                                link = Link.Online,
                                linkError = "",
                                openChat = chats.firstOrNull { it.id == current.openChat?.id } ?: current.openChat,
                            )
                        }
                    } else {
                        _state.update { it.copy(link = Link.Online, linkError = "") }
                    }
                    backoff = 1_000L
                } catch (e: Exception) {
                    if (unrecoverable(e)) { forceUnpair(friendly(e)); return@launch }
                    _state.update { it.copy(link = Link.Offline, linkError = friendly(e)) }
                    delay(backoff)
                    // Once the backoff has topped out, the address itself is the likely problem rather than a
                    // blip — so try the next one the desktop baked in before giving this one another minute.
                    if (backoff >= 15_000L) rotateHost()
                    backoff = (backoff * 2).coerceAtMost(15_000L)
                }
            }
        }
    }

    /**
     * Points the app at a different address for the same PC.
     *
     * First preference is whatever actually answers a discovery broadcast right now, because that is a fact about
     * the network as it is; the baked-in list is only a memory of how it looked when the APK was built. A PC whose
     * lease changed is invisible to the list and obvious to the broadcast, and that is the case where an app that
     * cannot rotate simply never comes back.
     *
     * Only replies presenting the pinned certificate are considered, so this cannot be steered by a stranger.
     */
    private suspend fun rotateHost() {
        val current = _state.value.pairing ?: return
        _state.update { it.copy(linkError = "Looking for ${current.pcName.ifBlank { "your PC" }}…") }

        val live = runCatching { Discovery.hostsMatching(current.fingerprint, current.port) }.getOrDefault(emptyList())
        val next = live.firstOrNull { it != current.host }
            ?: live.firstOrNull()
            ?: baked?.hosts?.takeIf { it.size > 1 }
                ?.let { hosts -> hosts[(hosts.indexOf(current.host) + 1).mod(hosts.size)] }
            ?: return

        if (next == current.host) return
        val moved = current.copy(host = next)
        store.save(moved)
        _state.update { it.copy(pairing = moved, linkError = "Trying ${moved.address}…") }
    }

    /** Transcript polling only exists while a chat is actually open — the desktop mirrors a full transcript only
     *  for chats a phone is asking about, so closing one here stops that work over there too. */
    private fun startMessagesPoll(chatId: String) {
        messagesJob?.cancel()
        messagesJob = viewModelScope.launch {
            var backoff = 1_000L
            while (isActive) {
                val pairing = _state.value.pairing ?: break
                try {
                    val update = client(pairing).messages(chatId, messagesVersion, BridgeClient.POLL_SECONDS)
                    messagesVersion = update.version
                    if (!update.unchanged) {
                        _state.update { current ->
                            if ((current.screen as? Screen.Chat)?.id != chatId) current
                            else current.copy(
                                messages = update.applyTo(current.messages),
                                openChat = update.chat ?: current.openChat,
                                // An incremental reply always carries detail; keep the old block if it somehow does not.
                                detail = update.detail ?: current.detail,
                                link = Link.Online,
                                linkError = "",
                            )
                        }
                    }
                    backoff = 1_000L
                } catch (e: Exception) {
                    if (unrecoverable(e)) { forceUnpair(friendly(e)); return@launch }
                    if (e is BridgeException && e.status == HttpURLConnection.HTTP_NOT_FOUND) {
                        _state.update { it.copy(notice = "That chat was closed on the PC.") }
                        closeChatScreen()
                        return@launch
                    }
                    _state.update { it.copy(link = Link.Offline, linkError = friendly(e)) }
                    delay(backoff)
                    backoff = (backoff * 2).coerceAtMost(15_000L)
                }
            }
        }
    }

    // ---------------- the turn ----------------

    fun onDraftChanged(value: String) = _state.update { it.copy(draft = value) }

    fun send() {
        val text = _state.value.draft.trim()
        val chatId = openChatId() ?: return
        val pairing = _state.value.pairing ?: return
        if (text.isEmpty() || _state.value.sending) return

        // Clear the composer immediately. The prompt reappears as a real transcript row within one poll, and
        // holding the text hostage until the round trip finishes makes the app feel broken on a slow link.
        _state.update { it.copy(draft = "", sending = true) }
        viewModelScope.launch {
            try {
                client(pairing).send(chatId, text)
            } catch (e: Exception) {
                _state.update { it.copy(draft = text, notice = friendly(e)) }
            } finally {
                _state.update { it.copy(sending = false) }
            }
        }
    }

    fun stopTurn() = act { client, chatId -> client.stop(chatId) }

    // ---------------- answering the agent ----------------

    fun respondToPermission(requestId: String, allow: Boolean, always: Boolean = false) =
        act { client, chatId -> client.respondToPermission(chatId, requestId, allow, always) }

    fun answerQuestion(requestId: String, answers: List<Triple<String, List<String>, String>>) =
        act { client, chatId -> client.answerQuestion(chatId, requestId, answers) }

    fun decidePlan(requestId: String, approve: Boolean, autoAccept: Boolean, feedback: String = "") =
        act { client, chatId -> client.decidePlan(chatId, requestId, approve, autoAccept, feedback) }

    // ---------------- session controls ----------------

    /** Fetches the model / effort / command menus for the open chat. */
    fun loadOptions() {
        val chatId = openChatId() ?: return
        val pairing = _state.value.pairing ?: return
        if (_state.value.optionsBusy) return
        _state.update { it.copy(optionsBusy = true) }
        viewModelScope.launch {
            try {
                val options = client(pairing).options(chatId)
                _state.update {
                    if (openChatId() != chatId) it else it.copy(options = options, optionsBusy = false)
                }
            } catch (e: Exception) {
                _state.update { it.copy(optionsBusy = false, notice = friendly(e)) }
            }
        }
    }

    fun setModel(model: String?) = act(reload = true) { client, chatId -> client.setModel(chatId, model) }

    fun setEffort(effort: String?) = act(reload = true) { client, chatId -> client.setEffort(chatId, effort) }

    fun setMode(mode: String) = act(reload = true) { client, chatId -> client.setMode(chatId, mode) }

    fun setFastMode(on: Boolean) = act(reload = true) { client, chatId -> client.setFastMode(chatId, on) }

    // ---------------- queue and rewind ----------------

    fun sendQueuedNow(ordinal: Int) = act { client, chatId -> client.sendQueuedNow(chatId, ordinal) }

    fun cancelQueued(ordinal: Int) = act { client, chatId -> client.cancelQueued(chatId, ordinal) }

    /**
     * Rewinds the workspace to just before a prompt. This one is not fire-and-forget: it can take a while, it can
     * fail for reasons the user needs to read, and acting on the transcript while it runs is not meaningful — so
     * the screen is marked busy until the desktop answers.
     */
    fun undoPrompt(ordinal: Int) {
        val chatId = openChatId() ?: return
        val pairing = _state.value.pairing ?: return
        if (_state.value.undoing) return
        _state.update { it.copy(undoing = true) }
        viewModelScope.launch {
            try {
                val message = client(pairing).undoPrompt(chatId, ordinal)
                _state.update {
                    it.copy(notice = message.ifBlank { "Changes undone — your prompt is back on the PC." })
                }
            } catch (e: Exception) {
                _state.update { it.copy(notice = friendly(e)) }
            } finally {
                _state.update { it.copy(undoing = false) }
            }
        }
    }

    // ---------------- chat lifecycle ----------------

    fun togglePin(chat: ChatSummary) {
        val pairing = _state.value.pairing ?: return
        viewModelScope.launch {
            runCatching { client(pairing).togglePin(chat.id) }
                .onFailure { e -> _state.update { it.copy(notice = friendly(e)) } }
        }
    }

    fun rename(chat: ChatSummary, title: String) {
        val pairing = _state.value.pairing ?: return
        val trimmed = title.trim()
        if (trimmed.isEmpty()) return
        viewModelScope.launch {
            runCatching { client(pairing).rename(chat.id, trimmed) }
                .onFailure { e -> _state.update { it.copy(notice = friendly(e)) } }
        }
    }

    fun closeChatOnPc(chat: ChatSummary) {
        val pairing = _state.value.pairing ?: return
        viewModelScope.launch {
            runCatching { client(pairing).close(chat.id) }
                .onSuccess { if (openChatId() == chat.id) closeChatScreen() }
                .onFailure { e -> _state.update { it.copy(notice = friendly(e)) } }
        }
    }

    // ---------------- starting a chat ----------------

    fun openNewChat() {
        val pairing = _state.value.pairing ?: return
        _state.update { it.copy(screen = Screen.NewChat, newChat = NewChatState(loading = true)) }
        viewModelScope.launch {
            try {
                val folders = client(pairing).folders()
                _state.update {
                    it.copy(
                        newChat = it.newChat.copy(
                            loading = false,
                            folders = folders,
                            cwd = folders.preferred.ifBlank { folders.folders.firstOrNull()?.cwd.orEmpty() },
                            provider = folders.defaultProvider,
                        )
                    )
                }
            } catch (e: Exception) {
                _state.update { it.copy(newChat = it.newChat.copy(loading = false, error = friendly(e))) }
            }
        }
    }

    fun onNewChatFolder(cwd: String) = _state.update { it.copy(newChat = it.newChat.copy(cwd = cwd, error = "")) }

    fun onNewChatProvider(provider: String) =
        _state.update { it.copy(newChat = it.newChat.copy(provider = provider, error = "")) }

    fun onNewChatTitle(title: String) = _state.update { it.copy(newChat = it.newChat.copy(title = title)) }

    fun cancelNewChat() = _state.update { it.copy(screen = Screen.Chats, newChat = NewChatState()) }

    fun createChat() {
        val pairing = _state.value.pairing ?: return
        val form = _state.value.newChat
        if (form.cwd.isBlank()) {
            _state.update { it.copy(newChat = it.newChat.copy(error = "Pick a folder first.")) }
            return
        }
        _state.update { it.copy(newChat = it.newChat.copy(busy = true, error = "")) }
        viewModelScope.launch {
            try {
                val id = client(pairing).newChat(form.cwd, form.provider.ifBlank { null }, form.title.ifBlank { null })
                if (id.isBlank()) throw IOException("The PC did not open a chat.")
                // The list poll has not necessarily seen the new chat yet, so navigate on a summary built from
                // what was just asked for; the next poll replaces it with the real one.
                messagesVersion = -1
                _state.update {
                    it.copy(
                        screen = Screen.Chat(id),
                        newChat = NewChatState(),
                        openChat = it.chats.firstOrNull { c -> c.id == id },
                        messages = emptyList(),
                        detail = ChatDetail(),
                        options = null,
                        draft = "",
                    )
                }
                startMessagesPoll(id)
            } catch (e: Exception) {
                _state.update { it.copy(newChat = it.newChat.copy(busy = false, error = friendly(e))) }
            }
        }
    }

    fun retryNow() {
        if (_state.value.pairing == null) return
        startChatsPoll()
        openChatId()?.let { startMessagesPoll(it) }
    }

    // ---------------- plumbing ----------------

    /**
     * Runs a one-shot call against the open chat. Failures surface as a notice rather than throwing — every one
     * of these is a button tap, and a phone that loses Wi-Fi mid-tap should say so, not crash.
     */
    private fun act(reload: Boolean = false, block: suspend (BridgeClient, String) -> Unit) {
        val chatId = openChatId() ?: return
        val pairing = _state.value.pairing ?: return
        viewModelScope.launch {
            try {
                block(client(pairing), chatId)
                if (reload) loadOptions()
            } catch (e: Exception) {
                _state.update { it.copy(notice = friendly(e)) }
            }
        }
    }

    // ---------------- failure handling ----------------

    /** A 401 means the PC revoked this phone. Staying "connected" after that is a lie, so drop the pairing. */
    private fun unrecoverable(e: Throwable) =
        e is BridgeException && e.status == HttpURLConnection.HTTP_UNAUTHORIZED

    private fun forceUnpair(reason: String) {
        stopPolling()
        store.clear()
        _state.value = if (baked != null) {
            UiState(
                screen = Screen.Enrolling,
                link = Link.Offline,
                boundTo = baked.pcName,
                // The PC revoked this phone; its enrolment coupon was spent long ago. Retrying cannot help, and
                // pretending otherwise would put the user back on an endless spinner.
                enrolFatal = true,
                enrolError = reason,
                notice = reason,
            )
        } else {
            UiState(screen = Screen.Pair, link = Link.Offline, notice = reason)
        }
    }

    private fun friendly(e: Throwable): String = when {
        e is BridgeException -> e.message ?: "The PC refused that."
        e is javax.net.ssl.SSLHandshakeException ->
            "The PC's security key does not match the one this phone paired with. Pair again if you reinstalled VibeCode."
        e is java.net.SocketTimeoutException -> "The PC did not answer in time."
        e is java.net.ConnectException -> "Can't reach the PC. Is VibeCode open and on the same Wi-Fi?"
        else -> e.message ?: "Something went wrong."
    }

    private fun reachabilityMessage(e: Throwable, host: String, port: Int): String = when (e) {
        is java.net.ConnectException, is java.net.SocketTimeoutException, is java.net.NoRouteToHostException ->
            "Nothing answered at $host:$port. Check the address on the PC, that VibeCode's phone access is on, and that both are on the same Wi-Fi."
        else -> friendly(e)
    }
}
