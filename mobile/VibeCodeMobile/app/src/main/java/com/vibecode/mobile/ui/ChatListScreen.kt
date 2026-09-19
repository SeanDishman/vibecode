package com.vibecode.mobile.ui

import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.PushPin
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FloatingActionButton
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
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.vibecode.mobile.Link
import com.vibecode.mobile.UiState
import com.vibecode.mobile.data.ChatSummary

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ChatListScreen(
    state: UiState,
    onOpen: (ChatSummary) -> Unit,
    onRetry: () -> Unit,
    onUnpair: () -> Unit,
    onNewChat: () -> Unit,
    onPin: (ChatSummary) -> Unit,
    onRename: (ChatSummary, String) -> Unit,
    onClosePane: (ChatSummary) -> Unit,
) {
    var menuOpen by remember { mutableStateOf(false) }
    var renaming by remember { mutableStateOf<ChatSummary?>(null) }

    renaming?.let { chat ->
        RenameDialog(
            chat = chat,
            onDismiss = { renaming = null },
            onConfirm = { title -> renaming = null; onRename(chat, title) },
        )
    }

    Scaffold(
        containerColor = VibeColors.Bg0,
        topBar = {
            TopAppBar(
                colors = TopAppBarDefaults.topAppBarColors(
                    containerColor = VibeColors.Bg1,
                    titleContentColor = VibeColors.Text,
                ),
                title = {
                    Column {
                        Text("VibeCode", style = MaterialTheme.typography.titleMedium)
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            LinkDot(state.link)
                            Spacer(Modifier.width(6.dp))
                            Text(
                                linkLabel(state),
                                style = MaterialTheme.typography.bodySmall,
                                color = if (state.link == Link.Offline) VibeColors.Amber else VibeColors.Muted,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis,
                            )
                        }
                    }
                },
                actions = {
                    IconButton(onClick = { menuOpen = true }) {
                        Icon(Icons.Default.MoreVert, contentDescription = "More", tint = VibeColors.Muted)
                    }
                    DropdownMenu(
                        expanded = menuOpen,
                        onDismissRequest = { menuOpen = false },
                        modifier = Modifier.background(VibeColors.Bg2),
                    ) {
                        DropdownMenuItem(
                            text = { Text("Reconnect now", color = VibeColors.Text) },
                            onClick = { menuOpen = false; onRetry() },
                        )
                        DropdownMenuItem(
                            text = { Text("Unpair this phone", color = VibeColors.Red) },
                            onClick = { menuOpen = false; onUnpair() },
                        )
                    }
                },
            )
        },
        floatingActionButton = {
            // Only offered while the PC is actually answering: a new chat is a write, and queueing one against a
            // desktop that is not there would just fail a second later.
            if (state.link == Link.Online) {
                FloatingActionButton(
                    onClick = onNewChat,
                    containerColor = VibeColors.Accent,
                    contentColor = VibeColors.OnAccent,
                ) {
                    Icon(Icons.Default.Add, contentDescription = "New chat")
                }
            }
        },
    ) { padding ->
        if (state.chats.isEmpty()) {
            EmptyState(state, Modifier.padding(padding))
        } else {
            LazyColumn(
                modifier = Modifier.fillMaxSize().padding(padding),
                contentPadding = PaddingValues(start = 14.dp, end = 14.dp, top = 14.dp, bottom = 88.dp),
                verticalArrangement = Arrangement.spacedBy(10.dp),
            ) {
                items(state.chats, key = { it.id }) { chat ->
                    ChatCard(
                        chat = chat,
                        onClick = { onOpen(chat) },
                        onPin = { onPin(chat) },
                        onRename = { renaming = chat },
                        onClosePane = { onClosePane(chat) },
                    )
                }
            }
        }
    }
}

@Composable
private fun RenameDialog(chat: ChatSummary, onDismiss: () -> Unit, onConfirm: (String) -> Unit) {
    var text by remember(chat.id) { mutableStateOf(chat.title) }
    AlertDialog(
        onDismissRequest = onDismiss,
        containerColor = VibeColors.Bg2,
        title = { Text("Rename chat", color = VibeColors.Text) },
        text = {
            OutlinedTextField(
                value = text,
                onValueChange = { text = it },
                singleLine = true,
                colors = fieldColors(),
                shape = RoundedCornerShape(9.dp),
                textStyle = MaterialTheme.typography.bodyMedium,
            )
        },
        confirmButton = {
            TextButton(onClick = { onConfirm(text) }, enabled = text.isNotBlank()) {
                Text("Rename", color = if (text.isNotBlank()) VibeColors.Accent else VibeColors.Faint)
            }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) { Text("Cancel", color = VibeColors.Muted) }
        },
    )
}

private fun linkLabel(state: UiState) = when (state.link) {
    Link.Online -> state.pairing?.pcName?.ifBlank { state.pairing.address } ?: "connected"
    Link.Connecting -> "connecting…"
    Link.Offline -> state.linkError.ifBlank { "can't reach the PC" }
}

@Composable
private fun LinkDot(link: Link) {
    val color = when (link) {
        Link.Online -> VibeColors.Green
        Link.Connecting -> VibeColors.Amber
        Link.Offline -> VibeColors.Red
    }
    val pulse = rememberInfiniteTransition(label = "link")
    val alpha by pulse.animateFloat(
        initialValue = 1f,
        targetValue = if (link == Link.Online) 1f else 0.3f,
        animationSpec = infiniteRepeatable(tween(900), RepeatMode.Reverse),
        label = "linkAlpha",
    )
    Box(Modifier.size(7.dp).alpha(alpha).clip(CircleShape).background(color))
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun ChatCard(
    chat: ChatSummary,
    onClick: () -> Unit,
    onPin: () -> Unit,
    onRename: () -> Unit,
    onClosePane: () -> Unit,
) {
    var actions by remember(chat.id) { mutableStateOf(false) }

    Box {
        DropdownMenu(
            expanded = actions,
            onDismissRequest = { actions = false },
            containerColor = VibeColors.Bg2,
        ) {
            DropdownMenuItem(
                text = { Text(if (chat.pinned) "Unpin" else "Pin to top", color = VibeColors.Text) },
                onClick = { actions = false; onPin() },
            )
            DropdownMenuItem(
                text = { Text("Rename", color = VibeColors.Text) },
                onClick = { actions = false; onRename() },
            )
            DropdownMenuItem(
                text = { Text("Close on PC", color = VibeColors.Red) },
                onClick = { actions = false; onClosePane() },
            )
        }
    }

    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(14.dp))
            .background(VibeColors.Bg1)
            .border(1.dp, if (chat.attention) VibeColors.Amber else VibeColors.BorderSoft, RoundedCornerShape(14.dp))
            .combinedClickable(onClick = onClick, onLongClick = { actions = true })
            .padding(14.dp),
        verticalAlignment = Alignment.Top,
    ) {
        Box(
            Modifier
                .padding(top = 5.dp)
                .size(8.dp)
                .clip(CircleShape)
                .background(VibeColors.provider(chat.provider))
        )
        Spacer(Modifier.width(12.dp))
        Column(Modifier.weight(1f)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                if (chat.pinned) {
                    Icon(
                        Icons.Default.PushPin,
                        contentDescription = "Pinned",
                        tint = VibeColors.Faint,
                        modifier = Modifier.size(12.dp).padding(end = 0.dp),
                    )
                    Spacer(Modifier.width(5.dp))
                }
                Text(
                    chat.title.ifBlank { "New chat" },
                    style = MaterialTheme.typography.titleSmall,
                    color = VibeColors.Text,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.weight(1f, fill = false),
                )
            }
            Spacer(Modifier.height(4.dp))
            Row(verticalAlignment = Alignment.CenterVertically) {
                Chip(chat.folder.ifBlank { "—" }, VibeColors.Bg3, VibeColors.Muted)
                Spacer(Modifier.width(6.dp))
                Chip(chat.providerLabel, VibeColors.Bg3, VibeColors.provider(chat.provider))
                if (chat.mode == "bypassPermissions") {
                    Spacer(Modifier.width(6.dp))
                    Chip("bypass", VibeColors.RedSoft, VibeColors.Red)
                }
                if (chat.hasTodos) {
                    Spacer(Modifier.width(6.dp))
                    Chip(
                        "${chat.todosDone}/${chat.todosTotal}",
                        VibeColors.Bg3,
                        if (chat.todosDone == chat.todosTotal) VibeColors.Green else VibeColors.Amber,
                    )
                }
            }
            if (chat.preview.isNotBlank()) {
                Spacer(Modifier.height(7.dp))
                Text(
                    chat.preview,
                    style = MaterialTheme.typography.bodySmall,
                    color = VibeColors.Faint,
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis,
                )
            }
        }
        Spacer(Modifier.width(8.dp))
        Column(horizontalAlignment = Alignment.End) {
            when {
                chat.attention -> Chip("needs you", VibeColors.AmberSoft, VibeColors.Amber)
                chat.working -> WorkingPill()
                chat.queued -> Chip("queued", VibeColors.Bg3, VibeColors.Muted)
                else -> Chip("idle", VibeColors.Bg3, VibeColors.Faint)
            }
        }
    }
}

@Composable
private fun WorkingPill() {
    val pulse = rememberInfiniteTransition(label = "working")
    val alpha by pulse.animateFloat(
        initialValue = 0.45f,
        targetValue = 1f,
        animationSpec = infiniteRepeatable(tween(700), RepeatMode.Reverse),
        label = "workingAlpha",
    )
    Box(Modifier.alpha(alpha)) { Chip("working", VibeColors.GreenSoft, VibeColors.Green) }
}

@Composable
internal fun Chip(text: String, background: androidx.compose.ui.graphics.Color, foreground: androidx.compose.ui.graphics.Color) {
    Box(
        Modifier
            .clip(RoundedCornerShape(999.dp))
            .background(background)
            .padding(horizontal = 8.dp, vertical = 3.dp)
    ) {
        Text(text, style = MaterialTheme.typography.labelSmall, color = foreground, maxLines = 1)
    }
}

@Composable
private fun EmptyState(state: UiState, modifier: Modifier) {
    Box(modifier.fillMaxSize().padding(32.dp), contentAlignment = Alignment.Center) {
        Column(horizontalAlignment = Alignment.CenterHorizontally) {
            Text(
                when (state.link) {
                    Link.Online -> "No chats open"
                    Link.Connecting -> "Looking for your PC…"
                    Link.Offline -> "Can't reach your PC"
                },
                style = MaterialTheme.typography.titleMedium,
                color = VibeColors.Muted,
            )
            Spacer(Modifier.height(8.dp))
            Text(
                when (state.link) {
                    Link.Online -> "Tap + to start one, or open a chat on the PC and it will appear here."
                    Link.Connecting -> "Both devices need to be on the same Wi-Fi."
                    Link.Offline -> state.linkError.ifBlank { "It will reconnect on its own when the PC is back." }
                },
                style = MaterialTheme.typography.bodyMedium,
                color = VibeColors.Faint,
                modifier = Modifier.padding(top = 2.dp),
            )
        }
    }
}
