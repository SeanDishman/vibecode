package com.vibecode.mobile

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.SystemBarStyle
import androidx.activity.compose.BackHandler
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.runtime.*
import com.vibecode.mobile.data.*
import com.vibecode.mobile.ui.*
import org.json.JSONObject

/** Debug-only native layout fixture. All callbacks change in-memory synthetic state, never a PC or pairing. */
class UiFixtureActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge(
            statusBarStyle = SystemBarStyle.dark(android.graphics.Color.TRANSPARENT),
            navigationBarStyle = SystemBarStyle.dark(android.graphics.Color.TRANSPARENT),
        )
        val scenario = intent.getStringExtra("screen") ?: "chats"
        setContent { VibeCodeTheme { Fixture(scenario) } }
    }
}

private val sampleChat = ChatSummary.from(JSONObject("""{
    "id":"fixture-chat","title":"Review a long project conversation on a small phone",
    "provider":"codex","providerLabel":"Codex","folder":"a-project-with-a-long-folder-name",
    "cwd":"C:/Projects/sample-project","model":"Sample reasoning model","mode":"default",
    "modeLabel":"Ask","working":true,"attention":true,"todosTotal":20,"todosDone":3,
    "preview":"A synthetic conversation for native phone layout verification. No real project data."
}"""))

private fun fixtureState(scenario: String): UiState {
    val code = "val description = \"A deliberately long code line which must remain fully readable by horizontal scrolling on a narrow phone\""
    val permission = Message("perm", name = "Edit", status = "pending", requestId = "permission", canAlways = true,
        diff = listOf(DiffLine("del", code), DiffLine("add", code + " // changed")))
    val question = Message("perm", status = "pending", requestId = "question", permKind = "question",
        questions = listOf(BridgeQuestion("Which layout should this sample use?", false,
            listOf("Portrait with larger text", "Landscape with a full-width composer"))))
    val messages = when (scenario) {
        "permission" -> listOf(permission)
        "question" -> listOf(question)
        "plan" -> listOf(Message("perm", text = "Review the layout, keep all phone controls reachable, and verify scrolling.",
            status = "pending", requestId = "plan", permKind = "plan"))
        "queued" -> listOf(Message("queued", text = "A queued sample prompt that is safe to cancel or send in this fixture.", ordinal = 1))
        else -> listOf(
            Message("user", "Please review this sample implementation.", ordinal = 0, canUndo = true),
            Message("tool", text = code, name = "Read", status = "complete", summary = "src/sample.kt"),
            Message("assistant", "**Sample response**\n\nThis is synthetic text for testing reading, scrolling, and code selection.\n\n```kotlin\n$code\n```\n\nThe final line remains visible above the composer."),
        )
    }
    val sheet = when (scenario) { "controls" -> Sheet.Controls; "todos" -> Sheet.Todos; "files" -> Sheet.Files; "commands" -> Sheet.Commands; else -> Sheet.None }
    return UiState(
        screen = Screen.Chat(sampleChat.id), link = if (scenario.contains("offline")) Link.Offline else Link.Online,
        openChat = sampleChat, chats = List(10) { sampleChat.copy(id = "chat-$it", title = "${it + 1}. ${sampleChat.title}") },
        messages = messages, sheet = sheet,
        detail = ChatDetail(mode = "default", modeLabel = "Ask", modelLabel = "Sample reasoning model", canFast = true, canFastNow = true,
            todos = List(20) { TodoEntry(if (it < 3) "completed" else "pending", "Task ${it + 1}: inspect a long sample description and verify all controls remain readable.") },
            files = List(20) { FileEntry("C:/Projects/a-long-project-name/src/feature/sample-file-$it.kt", "sample-file-$it.kt", 2) }),
        options = ChatOptions(mode = "default", model = "model-0", canFast = true,
            models = List(8) { ModelOption("model-$it", "Sample model ${it + 1}", "A long description for checking wrapped selection rows.", true) },
            efforts = listOf(EffortOption(null, "Auto", "Use the model default", 0, 4), EffortOption("high", "High", "More time for difficult tasks", 3, 4)),
            commands = List(18) { SlashCommand("command${it + 1}", "Synthetic command description for scroll testing.", "[optional sample argument]") }),
        newChat = NewChatState(loading = false, cwd = "C:/Projects/sample-project", provider = "codex",
            folders = FolderList(List(15) { Folder("C:/Projects/sample-project-$it", "Sample project ${it + 1}") })),
    )
}

@Composable
private fun Fixture(initial: String) {
    var screen by remember { mutableStateOf(initial) }
    var state by remember { mutableStateOf(fixtureState(initial)) }
    var pair by remember { mutableStateOf(PairState(
        step = when(initial) { "pair-confirm" -> PairStep.Confirm; "pair-code" -> PairStep.Code; else -> PairStep.Address },
        address = "192.0.2.10:8765", pcName = "Sample PC", fingerprint = "a".repeat(64),
        error = if (initial == "pair-error") "Could not reach the sample PC. Check the address and try again." else "",
    )) }
    BackHandler(enabled = state.sheet == Sheet.None && screen !in listOf("chats", "offline")) {
        if (screen.startsWith("pair") && pair.step != PairStep.Address) pair = pair.copy(step = PairStep.Address)
        else screen = "chats"
    }
    val back = { screen = "chats" }
    when {
        screen.startsWith("pair") -> PairScreen(pair, { pair = pair.copy(address = it) },
            { pair = pair.copy(step = PairStep.Confirm) }, { pair = pair.copy(step = PairStep.Code) },
            { pair = pair.copy(step = PairStep.Address) }, { pair = pair.copy(code = it.filter(Char::isDigit).take(6)) }, { screen = "chats" })
        screen.startsWith("enrol") -> EnrollingScreen(UiState(boundTo = "Sample PC", enrolRound = 3,
            enrolFatal = screen == "enrol-fatal", enrolBusy = screen == "enrol-busy",
            enrolProgress = "Trying sample PC · next attempt in 3 seconds",
            enrolError = "This synthetic PC is unavailable. Check the connection and try again."), { screen = "enrol-busy" })
        screen.startsWith("lock") -> LockScreen({ screen = "chats" }, authenticationUnavailable = screen == "lock-unavailable")
        screen == "chats" || screen == "offline" -> ChatListScreen(state,
            { screen = "chat" }, { state = state.copy(link = Link.Online) }, { screen = "pair" }, { screen = "new" },
            { picked -> state = state.copy(chats = state.chats.map { if (it.id == picked.id) it.copy(pinned = !it.pinned) else it }) },
            { picked, title -> state = state.copy(chats = state.chats.map { if (it.id == picked.id) it.copy(title = title) else it }) },
            { picked -> state = state.copy(chats = state.chats.filterNot { it.id == picked.id }) })
        screen == "new" -> NewChatScreen(state.newChat,
            { state = state.copy(newChat = state.newChat.copy(cwd = it)) },
            { state = state.copy(newChat = state.newChat.copy(provider = it)) },
            { state = state.copy(newChat = state.newChat.copy(title = it)) }, { screen = "chat" }, back)
        else -> ChatScreen(state, ChatActions(
            onBack = back, onDraftChanged = { state = state.copy(draft = it) },
            onSend = { state = state.copy(messages = state.messages + Message("user", state.draft), draft = "") },
            onStop = { state = state.copy(openChat = sampleChat.copy(working = false)) },
            onPermission = { id, allow, _ -> state = state.copy(messages = state.messages.map { if (it.requestId == id) it.copy(status = if (allow) "allow" else "deny") else it }) },
            onAnswer = { id, _ -> state = state.copy(messages = state.messages.map { if (it.requestId == id) it.copy(status = "answered") else it }) },
            onPlan = { id, allow, _, _ -> state = state.copy(messages = state.messages.map { if (it.requestId == id) it.copy(status = if (allow) "allow" else "deny") else it }) },
            onSheet = { state = state.copy(sheet = it) }, onDismissSheet = { state = state.copy(sheet = Sheet.None) },
            onModel = { state = state.copy(options = state.options?.copy(model = it)) },
            onEffort = { state = state.copy(options = state.options?.copy(effort = it)) },
            onMode = { state = state.copy(detail = state.detail.copy(mode = it), options = state.options?.copy(mode = it)) },
            onFast = { state = state.copy(detail = state.detail.copy(fast = it)) },
            onSendQueued = { state = state.copy(messages = emptyList()) }, onCancelQueued = { state = state.copy(messages = emptyList()) },
            onUndo = { state = state.copy(messages = state.messages.map { it.copy(wasUndone = true) }) },
            onPin = {}, onClosePane = back,
        ))
    }
}
