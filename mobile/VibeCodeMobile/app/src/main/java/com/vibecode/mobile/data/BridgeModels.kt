package com.vibecode.mobile.data

import org.json.JSONArray
import org.json.JSONObject

/** One chat as it appears in the list. Mirrors PhoneBridgeMirror.SummaryJson on the desktop. */
data class ChatSummary(
    val id: String,
    val title: String,
    val provider: String,
    val providerLabel: String,
    val folder: String,
    val cwd: String,
    val status: String,
    val working: Boolean,
    val queued: Boolean,
    val pinned: Boolean,
    val model: String,
    val attention: Boolean,
    val messages: Int,
    val preview: String,
    val mode: String,
    val modeLabel: String,
    val effort: String?,
    val fast: Boolean,
    val canFast: Boolean,
    val interrupt: Boolean,
    val todosDone: Int,
    val todosTotal: Int,
) {
    val hasTodos: Boolean get() = todosTotal > 0

    companion object {
        fun from(o: JSONObject) = ChatSummary(
            id = o.optString("id"),
            title = o.optString("title"),
            provider = o.optString("provider"),
            providerLabel = o.optString("providerLabel"),
            folder = o.optString("folder"),
            cwd = o.optString("cwd"),
            status = o.optString("status"),
            working = o.optBoolean("working"),
            queued = o.optBoolean("queued"),
            pinned = o.optBoolean("pinned"),
            model = o.optString("model"),
            attention = o.optBoolean("attention"),
            messages = o.optInt("messages"),
            preview = o.optString("preview"),
            mode = o.optString("mode"),
            modeLabel = o.optString("modeLabel"),
            effort = o.optStringOrNull("effort"),
            fast = o.optBoolean("fast"),
            canFast = o.optBoolean("canFast"),
            interrupt = o.optBoolean("interrupt"),
            todosDone = o.optInt("todosDone"),
            todosTotal = o.optInt("todosTotal"),
        )

        fun list(array: JSONArray?): List<ChatSummary> =
            (0 until (array?.length() ?: 0)).map { from(array!!.getJSONObject(it)) }
    }
}

/** One question inside an AskUserQuestion permission card. */
data class BridgeQuestion(val question: String, val multi: Boolean, val options: List<String>)

/** One line of a proposed edit, as the desktop's permission card would draw it. */
data class DiffLine(val kind: String, val text: String)

/** One entry in the agent's task list. */
data class TodoEntry(val status: String, val text: String) {
    val done: Boolean get() = status == "completed"
    val active: Boolean get() = status == "in_progress"
}

/** A prompt waiting to be sent when the current turn finishes. */
data class QueueEntry(val ordinal: Int, val text: String)

/** A file the agent created or edited this session. */
data class FileEntry(val path: String, val name: String, val writes: Int)

/**
 * The non-transcript state of an open chat: task list, send queue, touched files, live usage, and the model
 * pills. Arrives alongside the transcript and is versioned with it, so a todo flipping to done wakes the poll.
 */
data class ChatDetail(
    val todos: List<TodoEntry> = emptyList(),
    val queue: List<QueueEntry> = emptyList(),
    val files: List<FileEntry> = emptyList(),
    val model: String? = null,
    val modelLabel: String = "",
    val effort: String? = null,
    val effortLabel: String = "",
    val mode: String = "",
    val modeLabel: String = "",
    val fast: Boolean = false,
    val canFast: Boolean = false,
    val canFastNow: Boolean = false,
    val tokensIn: Long = 0,
    val tokensOut: Long = 0,
    val tokensLabel: String = "",
    val cost: Double = 0.0,
    val costLabel: String = "",
    val canInterrupt: Boolean = false,
    val canSendQueuedNow: Boolean = false,
) {
    val todosDone: Int get() = todos.count { it.done }

    companion object {
        fun from(o: JSONObject?): ChatDetail {
            if (o == null) return ChatDetail()
            return ChatDetail(
                todos = o.optJSONArray("todos").mapObjects { TodoEntry(it.optString("s"), it.optString("t")) },
                queue = o.optJSONArray("queue").mapObjects { QueueEntry(it.optInt("ord"), it.optString("t")) },
                files = o.optJSONArray("files")
                    .mapObjects { FileEntry(it.optString("p"), it.optString("n"), it.optInt("w")) },
                model = o.optStringOrNull("model"),
                modelLabel = o.optString("modelLabel"),
                effort = o.optStringOrNull("effort"),
                effortLabel = o.optString("effortLabel"),
                mode = o.optString("mode"),
                modeLabel = o.optString("modeLabel"),
                fast = o.optBoolean("fast"),
                canFast = o.optBoolean("canFast"),
                canFastNow = o.optBoolean("canFastNow"),
                tokensIn = o.optDouble("tokensIn", 0.0).toLong(),
                tokensOut = o.optDouble("tokensOut", 0.0).toLong(),
                tokensLabel = o.optString("tokensLabel"),
                cost = o.optDouble("cost", 0.0),
                costLabel = o.optString("costLabel"),
                canInterrupt = o.optBoolean("canInterrupt"),
                canSendQueuedNow = o.optBoolean("canSendQueuedNow"),
            )
        }
    }
}

/** One selectable model in the chat's picker. */
data class ModelOption(val value: String, val label: String, val description: String, val fast: Boolean)

/** One reasoning-effort tier. [value] is null for the model's Auto default. */
data class EffortOption(
    val value: String?,
    val label: String,
    val description: String,
    val rank: Int,
    val steps: Int,
)

/** A slash command the desktop's CLI advertises for this chat. */
data class SlashCommand(val name: String, val description: String, val hint: String)

/** Everything the chat's control sheet needs. Fetched on demand — these change rarely. */
data class ChatOptions(
    val models: List<ModelOption> = emptyList(),
    val efforts: List<EffortOption> = emptyList(),
    val commands: List<SlashCommand> = emptyList(),
    val model: String? = null,
    val effort: String? = null,
    val mode: String = "",
    val fast: Boolean = false,
    val canFast: Boolean = false,
    val provider: String = "",
) {
    companion object {
        fun from(o: JSONObject) = ChatOptions(
            models = o.optJSONArray("models").mapObjects {
                ModelOption(
                    value = it.optString("value"),
                    label = it.optString("label"),
                    description = it.optString("description"),
                    fast = it.optBoolean("fast"),
                )
            },
            efforts = o.optJSONArray("efforts").mapObjects {
                EffortOption(
                    value = it.optStringOrNull("value"),
                    label = it.optString("label"),
                    description = it.optString("description"),
                    rank = it.optInt("rank"),
                    steps = it.optInt("steps"),
                )
            },
            commands = o.optJSONArray("commands").mapObjects {
                SlashCommand(it.optString("name"), it.optString("description"), it.optString("hint"))
            },
            model = o.optStringOrNull("model"),
            effort = o.optStringOrNull("effort"),
            mode = o.optString("mode"),
            fast = o.optBoolean("fast"),
            canFast = o.optBoolean("canFast"),
            provider = o.optString("provider"),
        )
    }
}

/** A folder on the PC that a new chat can be started in. */
data class Folder(val cwd: String, val name: String)

/** The desktop's answer to "where could I start something?". */
data class FolderList(
    val folders: List<Folder> = emptyList(),
    val preferred: String = "",
    val defaultProvider: String = "",
) {
    companion object {
        fun from(o: JSONObject) = FolderList(
            folders = o.optJSONArray("folders").mapObjects { Folder(it.optString("cwd"), it.optString("name")) },
            preferred = o.optString("preferred"),
            defaultProvider = o.optString("defaultProvider"),
        )
    }
}

/**
 * One transcript row. The desktop sends a compact, already-flattened shape (a compacted tool group arrives as its
 * individual tools), so the phone never has to reimplement the desktop's grouping rules to draw something sane.
 */
data class Message(
    val kind: String,
    val text: String = "",
    val name: String = "",
    val status: String = "",
    val summary: String = "",
    val live: Boolean = false,
    val error: Boolean = false,
    val agent: Boolean = false,
    val added: Int = 0,
    val removed: Int = 0,
    val fromSubagent: Boolean = false,
    val attachments: Int = 0,
    val level: String = "",
    val requestId: String = "",
    val permKind: String = "",
    val quiet: Boolean = true,
    val questions: List<BridgeQuestion> = emptyList(),
    /** Position among this chat's user prompts (or queued prompts). -1 for every other row. */
    val ordinal: Int = -1,
    /** Whether tapping rewind on this prompt would currently do anything. */
    val canUndo: Boolean = false,
    val wasUndone: Boolean = false,
    /** How many later prompts would be rewound along with this one. */
    val cascades: Int = 0,
    /** Whether this permission offers a rule to remember, i.e. whether "Always allow" is real here. */
    val canAlways: Boolean = false,
    val diff: List<DiffLine> = emptyList(),
) {
    companion object {
        fun from(o: JSONObject): Message {
            val questions = o.optJSONArray("questions").mapObjects { q ->
                val options = q.optJSONArray("options")
                BridgeQuestion(
                    question = q.optString("q"),
                    multi = q.optBoolean("multi"),
                    options = (0 until (options?.length() ?: 0)).map { options!!.getString(it) },
                )
            }

            return Message(
                kind = o.optString("k"),
                text = o.optString("t"),
                name = o.optString("n"),
                status = o.optString("st"),
                summary = o.optString("sum"),
                live = o.optBoolean("live"),
                error = o.optBoolean("err"),
                agent = o.optBoolean("agent"),
                added = o.optInt("add"),
                removed = o.optInt("del"),
                fromSubagent = o.optBoolean("sub"),
                attachments = o.optInt("att"),
                level = o.optString("level"),
                requestId = o.optString("id"),
                permKind = o.optString("kind"),
                quiet = o.optBoolean("quiet", true),
                questions = questions,
                ordinal = o.optInt("ord", -1),
                canUndo = o.optBoolean("undo"),
                wasUndone = o.optBoolean("undone"),
                cascades = o.optInt("cascades"),
                canAlways = o.optBoolean("always"),
                diff = o.optJSONArray("diff").mapObjects { DiffLine(it.optString("s"), it.optString("t")) },
            )
        }
    }
}

/**
 * The result of one transcript poll. The desktop only resends from [base] onward, so applying an update is a
 * splice rather than a replace — which is what keeps a long transcript from being re-downloaded every time a
 * single streaming message grows by a word.
 */
data class TranscriptUpdate(
    val version: Int,
    val unchanged: Boolean,
    val total: Int,
    val base: Int,
    val items: List<Message>,
    val chat: ChatSummary?,
    val detail: ChatDetail?,
) {
    fun applyTo(existing: List<Message>): List<Message> {
        if (unchanged) return existing
        val kept = if (base <= 0) emptyList() else existing.take(minOf(base, existing.size))
        // A splice can only be trusted when the prefix we are keeping is actually as long as the server assumed.
        // If it is short (the app was killed and restarted mid-transcript), fall back to what we were sent.
        if (kept.size < base) return items
        return kept + items
    }
}

// ---------------- small JSON helpers ----------------

/** JSONObject.optString returns the literal string "null" for a JSON null; this returns a real null. */
internal fun JSONObject.optStringOrNull(key: String): String? =
    if (isNull(key)) null else optString(key).takeIf { it.isNotEmpty() }

internal inline fun <T> JSONArray?.mapObjects(transform: (JSONObject) -> T): List<T> {
    if (this == null) return emptyList()
    return (0 until length()).mapNotNull { i -> optJSONObject(i)?.let(transform) }
}
