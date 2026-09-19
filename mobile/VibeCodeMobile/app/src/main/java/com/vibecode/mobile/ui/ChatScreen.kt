package com.vibecode.mobile.ui

import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.automirrored.filled.Send
import androidx.compose.material.icons.filled.Checklist
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.Stop
import androidx.compose.material.icons.filled.Tune
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshotFlow
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.vibecode.mobile.Sheet
import com.vibecode.mobile.UiState
import com.vibecode.mobile.data.Message
import kotlinx.coroutines.flow.distinctUntilChanged

/** Everything the chat screen can ask the view model to do, in one bag so the signature stays readable. */
class ChatActions(
    val onBack: () -> Unit,
    val onDraftChanged: (String) -> Unit,
    val onSend: () -> Unit,
    val onStop: () -> Unit,
    val onPermission: (String, Boolean, Boolean) -> Unit,
    val onAnswer: (String, List<Triple<String, List<String>, String>>) -> Unit,
    val onPlan: (String, Boolean, Boolean, String) -> Unit,
    val onSheet: (Sheet) -> Unit,
    val onDismissSheet: () -> Unit,
    val onModel: (String?) -> Unit,
    val onEffort: (String?) -> Unit,
    val onMode: (String) -> Unit,
    val onFast: (Boolean) -> Unit,
    val onSendQueued: (Int) -> Unit,
    val onCancelQueued: (Int) -> Unit,
    val onUndo: (Int) -> Unit,
    val onPin: () -> Unit,
    val onClosePane: () -> Unit,
)

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ChatScreen(state: UiState, actions: ChatActions) {
    val chat = state.openChat
    val listState = rememberLazyListState()

    // Follow the conversation down only while the reader is already at the bottom. Yanking the view during a
    // long streaming answer while someone is scrolled up reading an earlier tool result is the single most
    // annoying thing a chat client can do.
    var stuckToBottom by remember { mutableStateOf(true) }
    LaunchedEffect(listState) {
        snapshotFlow {
            val last = listState.layoutInfo.visibleItemsInfo.lastOrNull()?.index ?: 0
            last >= listState.layoutInfo.totalItemsCount - 2
        }.distinctUntilChanged().collect { stuckToBottom = it }
    }
    LaunchedEffect(state.messages.size, state.messages.lastOrNull()?.text) {
        if (stuckToBottom && state.messages.isNotEmpty()) {
            listState.animateScrollToItem(state.messages.lastIndex)
        }
    }

    Scaffold(
        containerColor = VibeColors.Bg0,
        topBar = { ChatTopBar(state, actions) },
        bottomBar = {
            Composer(
                draft = state.draft,
                sending = state.sending,
                working = chat?.working == true,
                onDraftChanged = actions.onDraftChanged,
                onSend = actions.onSend,
                onCommands = { actions.onSheet(Sheet.Commands) },
            )
        },
    ) { padding ->
        LazyColumn(
            state = listState,
            modifier = Modifier.fillMaxSize().padding(padding),
            contentPadding = PaddingValues(horizontal = 14.dp, vertical = 12.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            itemsIndexed(state.messages) { index, message ->
                MessageRow(message, index, state, actions)
            }
        }
    }

    when (state.sheet) {
        Sheet.Controls -> ControlsSheet(
            state = state,
            onDismiss = actions.onDismissSheet,
            onModel = actions.onModel,
            onEffort = actions.onEffort,
            onMode = actions.onMode,
            onFast = actions.onFast,
        )
        Sheet.Todos -> TodosSheet(state, actions.onDismissSheet)
        Sheet.Files -> FilesSheet(state, actions.onDismissSheet)
        Sheet.Commands -> CommandsSheet(state, actions.onDismissSheet) { name ->
            actions.onDraftChanged("/$name ")
            actions.onDismissSheet()
        }
        Sheet.None -> Unit
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun ChatTopBar(state: UiState, actions: ChatActions) {
    val chat = state.openChat
    val detail = state.detail
    var menuOpen by remember { mutableStateOf(false) }

    TopAppBar(
        colors = TopAppBarDefaults.topAppBarColors(
            containerColor = VibeColors.Bg1,
            titleContentColor = VibeColors.Text,
        ),
        navigationIcon = {
            IconButton(onClick = actions.onBack) {
                Icon(Icons.AutoMirrored.Filled.ArrowBack, "Back", tint = VibeColors.Muted)
            }
        },
        title = {
            Column {
                Text(
                    chat?.title?.ifBlank { "Chat" } ?: "Chat",
                    style = MaterialTheme.typography.titleMedium,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
                // The pills are the chat's live configuration and they double as the way into the control sheet,
                // which is how the desktop's composer bar works too.
                Row(
                    Modifier.horizontalScroll(rememberScrollState()).padding(top = 2.dp),
                    horizontalArrangement = Arrangement.spacedBy(6.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    chat?.folder?.takeIf { it.isNotBlank() }?.let { Pill(it, VibeColors.Faint) }
                    val model = detail.modelLabel.ifBlank { chat?.model.orEmpty() }
                    if (model.isNotBlank()) Pill(model, VibeColors.Blue) { actions.onSheet(Sheet.Controls) }
                    val mode = detail.modeLabel.ifBlank { chat?.modeLabel.orEmpty() }
                    if (mode.isNotBlank()) {
                        val danger = detail.mode == "bypassPermissions" || chat?.mode == "bypassPermissions"
                        Pill(mode, if (danger) VibeColors.Red else VibeColors.Violet) { actions.onSheet(Sheet.Controls) }
                    }
                    if (detail.fast) Pill("fast", VibeColors.Amber) { actions.onSheet(Sheet.Controls) }
                    detail.effort?.let { Pill("effort $it", VibeColors.Muted) { actions.onSheet(Sheet.Controls) } }
                    if (detail.tokensLabel.isNotBlank()) Pill(detail.tokensLabel, VibeColors.Faint)
                    if (detail.costLabel.isNotBlank()) Pill(detail.costLabel, VibeColors.Faint)
                }
            }
        },
        actions = {
            if (detail.todos.isNotEmpty()) {
                IconButton(onClick = { actions.onSheet(Sheet.Todos) }) {
                    Box(contentAlignment = Alignment.Center) {
                        Icon(Icons.Default.Checklist, "Task list", tint = VibeColors.Muted)
                        // A dot rather than a count: the exact number matters less than "there is a plan running".
                        if (detail.todosDone < detail.todos.size) {
                            Box(
                                Modifier
                                    .padding(start = 14.dp, bottom = 14.dp)
                                    .size(6.dp)
                                    .clip(CircleShape)
                                    .background(VibeColors.Amber)
                            )
                        }
                    }
                }
            }
            if (chat?.working == true) {
                IconButton(onClick = actions.onStop) {
                    Icon(Icons.Default.Stop, "Stop this turn", tint = VibeColors.Red)
                }
            }
            IconButton(onClick = { actions.onSheet(Sheet.Controls) }) {
                Icon(Icons.Default.Tune, "Session controls", tint = VibeColors.Muted)
            }
            Box {
                IconButton(onClick = { menuOpen = true }) {
                    Icon(Icons.Default.MoreVert, "More", tint = VibeColors.Muted)
                }
                DropdownMenu(
                    expanded = menuOpen,
                    onDismissRequest = { menuOpen = false },
                    containerColor = VibeColors.Bg2,
                ) {
                    DropdownMenuItem(
                        text = { Text("Files touched", color = VibeColors.Text) },
                        onClick = { menuOpen = false; actions.onSheet(Sheet.Files) },
                    )
                    DropdownMenuItem(
                        text = { Text("Task list", color = VibeColors.Text) },
                        onClick = { menuOpen = false; actions.onSheet(Sheet.Todos) },
                    )
                    DropdownMenuItem(
                        text = {
                            Text(if (chat?.pinned == true) "Unpin on PC" else "Pin on PC", color = VibeColors.Text)
                        },
                        onClick = { menuOpen = false; actions.onPin() },
                    )
                    DropdownMenuItem(
                        text = { Text("Close on PC", color = VibeColors.Red) },
                        onClick = { menuOpen = false; actions.onClosePane() },
                    )
                }
            }
        },
    )
}

@Composable
private fun MessageRow(message: Message, index: Int, state: UiState, actions: ChatActions) {
    when (message.kind) {
        "user" -> UserBubble(message, state.undoing, actions.onUndo)
        "assistant" -> AssistantText(message)
        "thinking" -> ThinkingCard(message, index)
        "tool" -> ToolCard(message, index)
        "perm" -> when (message.permKind) {
            "question" -> QuestionCard(message, actions.onAnswer)
            "plan" -> PlanCard(message, actions.onPlan)
            else -> PermissionCard(message, actions.onPermission)
        }
        "banner" -> Banner(message)
        "divider" -> Divider(message)
        "queued" -> QueuedCard(message, actions.onSendQueued, actions.onCancelQueued)
        "pending" -> PendingOrb(message)
    }
}

@Composable
private fun UserBubble(message: Message, undoing: Boolean, onUndo: (Int) -> Unit) {
    var confirming by remember(message.ordinal) { mutableStateOf(false) }
    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
        Column(
            modifier = Modifier
                .fillMaxWidth(0.88f)
                .clip(RoundedCornerShape(14.dp, 14.dp, 4.dp, 14.dp))
                .background(VibeColors.AccentSoft)
                .border(1.dp, VibeColors.AccentDim, RoundedCornerShape(14.dp, 14.dp, 4.dp, 14.dp))
                .padding(horizontal = 13.dp, vertical = 10.dp),
        ) {
            if (message.fromSubagent) {
                Text("from a subagent", style = MaterialTheme.typography.labelSmall, color = VibeColors.Faint)
                Spacer(Modifier.height(4.dp))
            }
            RichText(message.text, color = VibeColors.Text)
            if (message.attachments > 0) {
                Spacer(Modifier.height(6.dp))
                Text(
                    "${message.attachments} attachment${if (message.attachments == 1) "" else "s"}",
                    style = MaterialTheme.typography.labelSmall,
                    color = VibeColors.Blue,
                )
            }

            if (message.wasUndone) {
                Spacer(Modifier.height(6.dp))
                Text("Changes undone", style = MaterialTheme.typography.labelSmall, color = VibeColors.Amber)
            } else if (message.canUndo && message.ordinal >= 0) {
                Spacer(Modifier.height(6.dp))
                if (!confirming) {
                    Text(
                        "↩  Rewind to here",
                        style = MaterialTheme.typography.labelSmall,
                        color = VibeColors.Muted,
                        modifier = Modifier
                            .clip(RoundedCornerShape(6.dp))
                            .clickable(enabled = !undoing) { confirming = true }
                            .padding(horizontal = 6.dp, vertical = 3.dp),
                    )
                } else {
                    // Rewinding rolls files back on a machine the user cannot see, so it asks first — and says
                    // how many later turns it would take with it.
                    Text(
                        if (message.cascades > 0)
                            "Undo this and the ${message.cascades} later prompt${if (message.cascades == 1) "" else "s"}?"
                        else "Undo this turn's file changes?",
                        style = MaterialTheme.typography.bodySmall,
                        color = VibeColors.Amber,
                    )
                    Spacer(Modifier.height(4.dp))
                    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        ActionButton("Rewind", VibeColors.Amber, enabled = !undoing) {
                            confirming = false
                            onUndo(message.ordinal)
                        }
                        ActionButton("Cancel", VibeColors.Faint) { confirming = false }
                    }
                }
            }
        }
    }
}

@Composable
private fun AssistantText(message: Message) {
    Column(Modifier.fillMaxWidth()) {
        RichText(message.text, color = VibeColors.Text)
        if (message.live) {
            Spacer(Modifier.height(4.dp))
            TypingDots()
        }
    }
}

@Composable
private fun ThinkingCard(message: Message, index: Int) {
    var expanded by remember(index) { mutableStateOf(false) }
    val hasText = message.text.isNotBlank()
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(10.dp))
            .background(VibeColors.Bg1)
            .clickable(enabled = hasText) { expanded = !expanded }
            .padding(12.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(
                if (message.live) "Thinking…" else "Thought process",
                style = MaterialTheme.typography.labelSmall,
                color = VibeColors.Violet,
            )
            if (hasText) {
                Spacer(Modifier.width(6.dp))
                Text(if (expanded) "▾" else "▸", style = MaterialTheme.typography.labelSmall, color = VibeColors.Faint)
            }
        }
        if (expanded && hasText) {
            Spacer(Modifier.height(8.dp))
            RichText(message.text, style = MaterialTheme.typography.bodySmall, color = VibeColors.Muted)
        }
    }
}

@Composable
private fun ToolCard(message: Message, index: Int) {
    var expanded by remember(index) { mutableStateOf(false) }
    val statusColor = when {
        message.error -> VibeColors.Red
        message.status == "running" -> VibeColors.Amber
        else -> VibeColors.Green
    }
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(10.dp))
            .background(VibeColors.Bg1)
            .border(1.dp, VibeColors.BorderSoft, RoundedCornerShape(10.dp))
            .clickable(enabled = message.text.isNotBlank()) { expanded = !expanded }
            .padding(12.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Box(Modifier.size(6.dp).clip(CircleShape).background(statusColor))
            Spacer(Modifier.width(9.dp))
            Text(
                if (message.agent) "${message.name} (agent)" else message.name,
                style = MaterialTheme.typography.titleSmall,
                color = VibeColors.Text,
            )
            if (message.summary.isNotBlank()) {
                Spacer(Modifier.width(8.dp))
                Text(
                    message.summary,
                    style = MonoStyle,
                    color = VibeColors.Muted,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.weight(1f),
                )
            } else {
                Spacer(Modifier.weight(1f))
            }
            if (message.added > 0 || message.removed > 0) {
                Spacer(Modifier.width(6.dp))
                Text("+${message.added}", style = MaterialTheme.typography.labelSmall, color = VibeColors.Green)
                Spacer(Modifier.width(4.dp))
                Text("−${message.removed}", style = MaterialTheme.typography.labelSmall, color = VibeColors.Red)
            }
        }
        if (expanded && message.text.isNotBlank()) {
            Spacer(Modifier.height(10.dp))
            Box(
                Modifier
                    .fillMaxWidth()
                    .clip(RoundedCornerShape(7.dp))
                    .background(VibeColors.CodeBg)
                    .padding(10.dp)
            ) {
                Text(message.text, style = MonoStyle, color = if (message.error) VibeColors.Red else VibeColors.Muted)
            }
        }
    }
}

// ---------------- the three kinds of card the agent can block on ----------------

@Composable
private fun PermissionCard(message: Message, onPermission: (String, Boolean, Boolean) -> Unit) {
    CardShell(pending = message.status == "pending", accent = VibeColors.Amber) {
        Text(
            "Permission needed · ${message.name}",
            style = MaterialTheme.typography.titleSmall,
            color = if (message.status == "pending") VibeColors.Amber else VibeColors.Muted,
        )
        if (message.summary.isNotBlank()) {
            Spacer(Modifier.height(4.dp))
            Text(message.summary, style = MonoStyle, color = VibeColors.Muted)
        }
        if (message.diff.isNotEmpty()) {
            Spacer(Modifier.height(10.dp))
            DiffBlock(message)
        } else if (message.text.isNotBlank()) {
            Spacer(Modifier.height(10.dp))
            CodeBlock(message.text)
        }

        Spacer(Modifier.height(10.dp))
        if (message.status == "pending") {
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                ActionButton("Allow", VibeColors.Green) { onPermission(message.requestId, true, false) }
                ActionButton("Deny", VibeColors.Red) { onPermission(message.requestId, false, false) }
            }
            if (message.canAlways) {
                Spacer(Modifier.height(6.dp))
                Text(
                    "Always allow this",
                    style = MaterialTheme.typography.labelSmall,
                    color = VibeColors.Green,
                    modifier = Modifier
                        .clip(RoundedCornerShape(6.dp))
                        .clickable { onPermission(message.requestId, true, true) }
                        .padding(horizontal = 6.dp, vertical = 4.dp),
                )
            }
        } else {
            Decision(message.status)
        }
    }
}

@Composable
private fun QuestionCard(
    message: Message,
    onAnswer: (String, List<Triple<String, List<String>, String>>) -> Unit,
) {
    val pending = message.status == "pending"
    // Selection lives here rather than in the view model: it is scratch state for one card, and the poll that
    // refreshes the transcript must not be able to wipe a half-made choice. Held as immutable maps so every
    // change is a single state write - a mutable set inside a state map would mutate without recomposing.
    var picked by remember(message.requestId) { mutableStateOf<Map<String, Set<String>>>(emptyMap()) }
    var custom by remember(message.requestId) { mutableStateOf<Map<String, String>>(emptyMap()) }

    CardShell(pending = pending, accent = VibeColors.Blue) {
        Text(
            "The agent has a question",
            style = MaterialTheme.typography.titleSmall,
            color = if (pending) VibeColors.Blue else VibeColors.Muted,
        )

        message.questions.forEach { question ->
            Spacer(Modifier.height(12.dp))
            Text(question.question, style = MaterialTheme.typography.bodyMedium, color = VibeColors.Text)
            if (question.multi) {
                Text(
                    "Pick as many as apply",
                    style = MaterialTheme.typography.labelSmall,
                    color = VibeColors.Faint,
                )
            }
            Spacer(Modifier.height(6.dp))

            val chosen = picked[question.question].orEmpty()
            question.options.forEach { option ->
                val selected = option in chosen
                Row(
                    Modifier
                        .fillMaxWidth()
                        .padding(vertical = 2.dp)
                        .clip(RoundedCornerShape(9.dp))
                        .background(if (selected) VibeColors.AccentSoft else Color.Transparent)
                        .border(
                            1.dp,
                            if (selected) VibeColors.Accent else VibeColors.BorderSoft,
                            RoundedCornerShape(9.dp),
                        )
                        .clickable(enabled = pending) {
                            val next = when {
                                selected -> chosen - option
                                // A single-answer question replaces rather than accumulates, which is what makes
                                // these read as radio buttons without needing a second control.
                                question.multi -> chosen + option
                                else -> setOf(option)
                            }
                            picked = picked + (question.question to next)
                        }
                        .padding(horizontal = 11.dp, vertical = 9.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Text(
                        option,
                        style = MaterialTheme.typography.bodyMedium,
                        color = if (selected) VibeColors.Text else VibeColors.Muted,
                        modifier = Modifier.weight(1f),
                    )
                    if (selected) Text("✓", style = MaterialTheme.typography.titleSmall, color = VibeColors.Accent)
                }
            }

            if (pending) {
                Spacer(Modifier.height(6.dp))
                OutlinedTextField(
                    value = custom[question.question].orEmpty(),
                    onValueChange = { custom = custom + (question.question to it) },
                    placeholder = { Text("Something else…", color = VibeColors.Faint) },
                    colors = fieldColors(),
                    shape = RoundedCornerShape(9.dp),
                    maxLines = 3,
                    textStyle = MaterialTheme.typography.bodyMedium,
                    modifier = Modifier.fillMaxWidth(),
                )
            }
        }

        Spacer(Modifier.height(10.dp))
        if (pending) {
            val ready = message.questions.any { q ->
                picked[q.question]?.isNotEmpty() == true || custom[q.question]?.isNotBlank() == true
            }
            ActionButton("Send answer", VibeColors.Accent, enabled = ready) {
                onAnswer(
                    message.requestId,
                    message.questions.map { q ->
                        Triple(q.question, picked[q.question].orEmpty().toList(), custom[q.question].orEmpty())
                    },
                )
            }
        } else {
            Decision(message.status)
        }
    }
}

@Composable
private fun PlanCard(message: Message, onPlan: (String, Boolean, Boolean, String) -> Unit) {
    val pending = message.status == "pending"
    var feedback by remember(message.requestId) { mutableStateOf("") }
    var rejecting by remember(message.requestId) { mutableStateOf(false) }

    CardShell(pending = pending, accent = VibeColors.Violet) {
        Text(
            "Plan ready for review",
            style = MaterialTheme.typography.titleSmall,
            color = if (pending) VibeColors.Violet else VibeColors.Muted,
        )
        if (message.text.isNotBlank()) {
            Spacer(Modifier.height(10.dp))
            RichText(message.text, style = MaterialTheme.typography.bodyMedium, color = VibeColors.Text)
        }

        Spacer(Modifier.height(12.dp))
        if (!pending) {
            Decision(message.status)
            return@CardShell
        }

        if (!rejecting) {
            Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
                ActionButton("Approve · keep asking", VibeColors.Green) {
                    onPlan(message.requestId, true, false, "")
                }
                ActionButton("Approve · auto-accept edits", VibeColors.Accent) {
                    onPlan(message.requestId, true, true, "")
                }
                ActionButton("Keep planning", VibeColors.Amber) { rejecting = true }
            }
        } else {
            Text(
                "What should change? The agent stays in plan mode.",
                style = MaterialTheme.typography.bodySmall,
                color = VibeColors.Muted,
            )
            Spacer(Modifier.height(6.dp))
            OutlinedTextField(
                value = feedback,
                onValueChange = { feedback = it },
                placeholder = { Text("Optional feedback", color = VibeColors.Faint) },
                colors = fieldColors(),
                shape = RoundedCornerShape(9.dp),
                maxLines = 4,
                textStyle = MaterialTheme.typography.bodyMedium,
                modifier = Modifier.fillMaxWidth(),
            )
            Spacer(Modifier.height(8.dp))
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                ActionButton("Send", VibeColors.Amber) {
                    onPlan(message.requestId, false, false, feedback)
                }
                ActionButton("Back", VibeColors.Faint) { rejecting = false }
            }
        }
    }
}

@Composable
private fun CardShell(pending: Boolean, accent: Color, content: @Composable androidx.compose.foundation.layout.ColumnScope.() -> Unit) {
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(12.dp))
            .background(if (pending) accent.copy(alpha = 0.10f) else VibeColors.Bg1)
            .border(1.dp, if (pending) accent else VibeColors.BorderSoft, RoundedCornerShape(12.dp))
            .padding(14.dp),
        content = content,
    )
}

@Composable
private fun Decision(status: String) {
    Text(
        when (status) {
            "allow" -> "allowed"
            "deny" -> "denied"
            "cancelled" -> "cancelled"
            else -> status
        },
        style = MaterialTheme.typography.labelSmall,
        color = VibeColors.Faint,
    )
}

@Composable
private fun CodeBlock(text: String) {
    Box(
        Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(7.dp))
            .background(VibeColors.CodeBg)
            .padding(10.dp)
    ) {
        Text(text, style = MonoStyle, color = VibeColors.Text)
    }
}

@Composable
private fun DiffBlock(message: Message) {
    Column(
        Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(7.dp))
            .background(VibeColors.CodeBg)
            .padding(vertical = 8.dp),
    ) {
        message.diff.forEach { line ->
            val (color, background) = when (line.kind) {
                "add" -> VibeColors.Green to VibeColors.GreenSoft
                "del" -> VibeColors.Red to VibeColors.RedSoft
                else -> VibeColors.Muted to Color.Transparent
            }
            Text(
                (if (line.kind == "add") "+ " else if (line.kind == "del") "− " else "  ") + line.text,
                style = MonoStyle,
                color = color,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.fillMaxWidth().background(background).padding(horizontal = 10.dp),
            )
        }
    }
}

@Composable
private fun ActionButton(label: String, color: Color, enabled: Boolean = true, onClick: () -> Unit) {
    TextButton(
        onClick = onClick,
        enabled = enabled,
        modifier = Modifier
            .clip(RoundedCornerShape(8.dp))
            .border(1.dp, if (enabled) color else VibeColors.Border, RoundedCornerShape(8.dp)),
    ) {
        Text(
            label,
            style = MaterialTheme.typography.titleSmall,
            color = if (enabled) color else VibeColors.Faint,
        )
    }
}

@Composable
private fun Banner(message: Message) {
    val color = when (message.level) {
        "error", "auth" -> VibeColors.Red
        "warn" -> VibeColors.Amber
        else -> VibeColors.Blue
    }
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(10.dp))
            .background(color.copy(alpha = 0.12f))
            .padding(12.dp),
    ) {
        Text(message.text, style = MaterialTheme.typography.bodySmall, color = color)
    }
}

@Composable
private fun Divider(message: Message) {
    Row(Modifier.fillMaxWidth().padding(vertical = 4.dp), verticalAlignment = Alignment.CenterVertically) {
        Box(Modifier.weight(1f).height(1.dp).background(VibeColors.BorderSoft))
        Text(
            message.text,
            style = MaterialTheme.typography.labelSmall,
            color = VibeColors.Faint,
            modifier = Modifier.padding(horizontal = 10.dp),
        )
        Box(Modifier.weight(1f).height(1.dp).background(VibeColors.BorderSoft))
    }
}

@Composable
private fun QueuedCard(message: Message, onSendNow: (Int) -> Unit, onCancel: (Int) -> Unit) {
    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
        Column(
            modifier = Modifier
                .fillMaxWidth(0.88f)
                .clip(RoundedCornerShape(14.dp, 14.dp, 4.dp, 14.dp))
                .border(1.dp, VibeColors.Border, RoundedCornerShape(14.dp, 14.dp, 4.dp, 14.dp))
                .padding(horizontal = 13.dp, vertical = 10.dp),
        ) {
            Text(
                "Queued · sends when the agent finishes",
                style = MaterialTheme.typography.labelSmall,
                color = VibeColors.Faint,
            )
            Spacer(Modifier.height(5.dp))
            Text(message.text, style = MaterialTheme.typography.bodyMedium, color = VibeColors.Muted)
            if (message.ordinal >= 0) {
                Spacer(Modifier.height(8.dp))
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    ActionButton("Send now", VibeColors.Accent) { onSendNow(message.ordinal) }
                    ActionButton("Cancel", VibeColors.Red) { onCancel(message.ordinal) }
                }
            }
        }
    }
}

@Composable
private fun PendingOrb(message: Message) {
    if (!message.quiet) return   // words are already arriving; a spinner under them is noise
    Row(Modifier.fillMaxWidth().padding(vertical = 2.dp), verticalAlignment = Alignment.CenterVertically) {
        TypingDots()
        Spacer(Modifier.width(8.dp))
        Text("working…", style = MaterialTheme.typography.bodySmall, color = VibeColors.Faint)
    }
}

@Composable
private fun TypingDots() {
    val transition = rememberInfiniteTransition(label = "dots")
    Row(verticalAlignment = Alignment.CenterVertically) {
        repeat(3) { i ->
            val alpha by transition.animateFloat(
                initialValue = 0.25f,
                targetValue = 1f,
                animationSpec = infiniteRepeatable(tween(600, delayMillis = i * 180), RepeatMode.Reverse),
                label = "dot$i",
            )
            Box(
                Modifier
                    .padding(end = 4.dp)
                    .size(5.dp)
                    .alpha(alpha)
                    .clip(CircleShape)
                    .background(VibeColors.Accent)
            )
        }
    }
}

@Composable
private fun Composer(
    draft: String,
    sending: Boolean,
    working: Boolean,
    onDraftChanged: (String) -> Unit,
    onSend: () -> Unit,
    onCommands: () -> Unit,
) {
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .background(VibeColors.Bg1)
            .imePadding()
            .navigationBarsPadding()
            .padding(horizontal = 12.dp, vertical = 10.dp),
    ) {
        if (working) {
            Text(
                "The agent is working — your message will be queued and sent when it finishes.",
                style = MaterialTheme.typography.labelSmall,
                color = VibeColors.Faint,
                modifier = Modifier.padding(start = 4.dp, bottom = 6.dp),
            )
        }
        Row(verticalAlignment = Alignment.Bottom) {
            IconButton(
                onClick = onCommands,
                modifier = Modifier.size(44.dp).clip(CircleShape).background(VibeColors.Bg3),
            ) {
                Text("/", style = MaterialTheme.typography.titleMedium, color = VibeColors.Muted)
            }
            Spacer(Modifier.width(8.dp))
            OutlinedTextField(
                value = draft,
                onValueChange = onDraftChanged,
                placeholder = { Text("Message", color = VibeColors.Faint) },
                colors = fieldColors(),
                shape = RoundedCornerShape(20.dp),
                maxLines = 6,
                textStyle = MaterialTheme.typography.bodyMedium,
                modifier = Modifier.weight(1f),
            )
            Spacer(Modifier.width(8.dp))
            val enabled = draft.isNotBlank() && !sending
            IconButton(
                onClick = onSend,
                enabled = enabled,
                modifier = Modifier
                    .size(48.dp)
                    .clip(CircleShape)
                    .background(if (enabled) VibeColors.Accent else VibeColors.Bg3),
            ) {
                if (sending) {
                    CircularProgressIndicator(Modifier.size(18.dp), color = VibeColors.OnAccent, strokeWidth = 2.dp)
                } else {
                    Icon(
                        Icons.AutoMirrored.Filled.Send,
                        "Send",
                        tint = if (enabled) VibeColors.OnAccent else VibeColors.Faint,
                    )
                }
            }
        }
    }
}
