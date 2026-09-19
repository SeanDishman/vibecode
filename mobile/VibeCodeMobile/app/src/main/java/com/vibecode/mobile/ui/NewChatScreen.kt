package com.vibecode.mobile.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
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
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material3.CircularProgressIndicator
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
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.vibecode.mobile.NewChatState

/** The CLIs the desktop can start a chat with, labelled the way the desktop labels them. */
private val Providers = listOf(
    "claude" to "Claude",
    "codex" to "Codex",
    "kimi" to "Kimi",
    "grok" to "Grok",
)

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun NewChatScreen(
    state: NewChatState,
    onFolder: (String) -> Unit,
    onProvider: (String) -> Unit,
    onTitle: (String) -> Unit,
    onCreate: () -> Unit,
    onCancel: () -> Unit,
) {
    Scaffold(
        containerColor = VibeColors.Bg0,
        topBar = {
            TopAppBar(
                colors = TopAppBarDefaults.topAppBarColors(
                    containerColor = VibeColors.Bg1,
                    titleContentColor = VibeColors.Text,
                ),
                navigationIcon = {
                    IconButton(onClick = onCancel) {
                        Icon(Icons.AutoMirrored.Filled.ArrowBack, "Back", tint = VibeColors.Muted)
                    }
                },
                title = { Text("New chat", style = MaterialTheme.typography.titleMedium) },
            )
        },
        bottomBar = {
            Column(
                Modifier
                    .fillMaxWidth()
                    .background(VibeColors.Bg1)
                    .imePadding()
                    .navigationBarsPadding()
                    .padding(horizontal = 16.dp, vertical = 12.dp),
            ) {
                if (state.error.isNotBlank()) {
                    Text(state.error, style = MaterialTheme.typography.bodySmall, color = VibeColors.Red)
                    Spacer(Modifier.height(8.dp))
                }
                TextButton(
                    onClick = onCreate,
                    enabled = !state.busy && state.cwd.isNotBlank(),
                    modifier = Modifier
                        .fillMaxWidth()
                        .clip(RoundedCornerShape(11.dp))
                        .background(
                            if (!state.busy && state.cwd.isNotBlank()) VibeColors.Accent else VibeColors.Bg3
                        )
                        .padding(vertical = 4.dp),
                ) {
                    if (state.busy) {
                        CircularProgressIndicator(
                            Modifier.size(18.dp),
                            color = VibeColors.OnAccent,
                            strokeWidth = 2.dp,
                        )
                    } else {
                        Text(
                            "Start on the PC",
                            style = MaterialTheme.typography.titleSmall,
                            color = if (state.cwd.isNotBlank()) VibeColors.OnAccent else VibeColors.Faint,
                        )
                    }
                }
            }
        },
    ) { padding ->
        if (state.loading) {
            Box(Modifier.fillMaxSize().padding(padding), contentAlignment = Alignment.Center) {
                CircularProgressIndicator(color = VibeColors.Accent, strokeWidth = 2.dp)
            }
            return@Scaffold
        }

        LazyColumn(
            Modifier.fillMaxSize().padding(padding),
            contentPadding = PaddingValues(16.dp),
            verticalArrangement = Arrangement.spacedBy(4.dp),
        ) {
            item {
                Label("Agent")
                Row(
                    Modifier.fillMaxWidth().padding(bottom = 6.dp),
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    Providers.forEach { (value, label) ->
                        val selected = state.provider == value
                        Box(
                            Modifier
                                .weight(1f)
                                .clip(RoundedCornerShape(10.dp))
                                .background(
                                    if (selected) VibeColors.provider(value).copy(alpha = 0.16f)
                                    else VibeColors.Bg1
                                )
                                .border(
                                    1.dp,
                                    if (selected) VibeColors.provider(value) else VibeColors.BorderSoft,
                                    RoundedCornerShape(10.dp),
                                )
                                .clickable { onProvider(value) }
                                .padding(vertical = 11.dp),
                            contentAlignment = Alignment.Center,
                        ) {
                            Text(
                                label,
                                style = MaterialTheme.typography.titleSmall,
                                color = if (selected) VibeColors.provider(value) else VibeColors.Muted,
                            )
                        }
                    }
                }
            }

            item {
                Spacer(Modifier.height(10.dp))
                Label("Name (optional)")
                OutlinedTextField(
                    value = state.title,
                    onValueChange = onTitle,
                    placeholder = { Text("Untitled", color = VibeColors.Faint) },
                    colors = fieldColors(),
                    shape = RoundedCornerShape(10.dp),
                    singleLine = true,
                    textStyle = MaterialTheme.typography.bodyMedium,
                    modifier = Modifier.fillMaxWidth().padding(bottom = 6.dp),
                )
            }

            item {
                Spacer(Modifier.height(10.dp))
                Label("Folder on the PC")
                if (state.folders.folders.isEmpty()) {
                    Text(
                        "The PC has no recent folders to offer. Open a project there once and it will show up here.",
                        style = MaterialTheme.typography.bodySmall,
                        color = VibeColors.Faint,
                        modifier = Modifier.padding(vertical = 10.dp),
                    )
                }
            }

            items(state.folders.folders) { folder ->
                val selected = folder.cwd == state.cwd
                Row(
                    Modifier
                        .fillMaxWidth()
                        .padding(vertical = 3.dp)
                        .clip(RoundedCornerShape(10.dp))
                        .background(if (selected) VibeColors.AccentSoft else VibeColors.Bg1)
                        .border(
                            1.dp,
                            if (selected) VibeColors.Accent else VibeColors.BorderSoft,
                            RoundedCornerShape(10.dp),
                        )
                        .clickable { onFolder(folder.cwd) }
                        .padding(horizontal = 12.dp, vertical = 11.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Column(Modifier.weight(1f)) {
                        Text(
                            folder.name,
                            style = MaterialTheme.typography.titleSmall,
                            fontWeight = if (selected) FontWeight.SemiBold else FontWeight.Normal,
                            color = if (selected) VibeColors.Text else VibeColors.Muted,
                        )
                        Text(
                            folder.cwd,
                            style = MonoStyle,
                            color = VibeColors.Faint,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                        )
                    }
                    if (selected) {
                        Spacer(Modifier.width(10.dp))
                        Box(Modifier.size(8.dp).clip(CircleShape).background(VibeColors.Accent))
                    }
                }
            }
        }
    }
}

@Composable
private fun Label(text: String) {
    Text(
        text.uppercase(),
        style = MaterialTheme.typography.labelSmall,
        color = VibeColors.Faint,
        modifier = Modifier.padding(bottom = 7.dp),
    )
}
