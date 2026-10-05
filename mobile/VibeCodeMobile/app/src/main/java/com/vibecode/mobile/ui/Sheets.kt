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
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyListScope
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.Switch
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Close
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.vibecode.mobile.UiState
import com.vibecode.mobile.Link
import com.vibecode.mobile.data.EffortOption
import com.vibecode.mobile.data.ModelOption

/** The permission modes the desktop offers, in the order its own picker lists them. */
private val Modes = listOf(
    Triple("auto", "Auto", "VibeCode decides — allows edits and safe commands, asks about risky ones."),
    Triple("default", "Ask", "Ask before every tool the CLI wants to run."),
    Triple("plan", "Plan", "Research and propose, but change nothing until you approve a plan."),
    Triple("acceptEdits", "Accept edits", "File edits go through without asking. Commands still prompt."),
    Triple("bypassPermissions", "Bypass", "Nothing is ever asked. The agent runs whatever it likes."),
)

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ControlsSheet(
    state: UiState,
    onDismiss: () -> Unit,
    onModel: (String?) -> Unit,
    onEffort: (String?) -> Unit,
    onMode: (String) -> Unit,
    onFast: (Boolean) -> Unit,
) {
    val options = state.options
    val detail = state.detail
    SheetShell(onDismiss) {
        item { SheetTitle("Session settings") }
        if (state.link != Link.Online) {
            item { EmptyNote("Reconnect to your PC to change session settings.") }
        }

        // --- mode ---
        item { SectionLabel("Permissions") }
        items(Modes) { (value, label, description) ->
            val selected = value == (options?.mode?.takeIf { it.isNotBlank() } ?: detail.mode)
            PickerRow(
                label = label,
                description = description,
                selected = selected,
                enabled = state.link == Link.Online,
                // Bypass is the one choice that removes every guardrail on a machine the user cannot see.
                accent = if (value == "bypassPermissions") VibeColors.Red else VibeColors.Accent,
                onClick = { onMode(value) },
            )
        }

        // --- fast mode ---
        if (detail.canFast || options?.canFast == true) {
            item {
            Spacer(Modifier.height(14.dp))
            SectionLabel("Speed")
            PickerRow(
                label = "Fast mode",
                description = if (detail.canFastNow) "Same model, faster output."
                else "Not available on the model this chat is using.",
                selected = detail.fast,
                enabled = state.link == Link.Online && (detail.canFastNow || detail.fast),
                toggle = true,
                onClick = { onFast(!detail.fast) },
            )
            }
        }

        // --- model ---
        if (options == null) {
            item {
            Spacer(Modifier.height(14.dp))
            Text(
                if (state.optionsBusy) "Loading the PC's model list…" else "The PC did not send a model list.",
                style = MaterialTheme.typography.bodySmall,
                color = VibeColors.Faint,
                modifier = Modifier.padding(horizontal = 4.dp, vertical = 8.dp),
            )
            }
        } else {
            if (options.models.isNotEmpty()) {
                item {
                Spacer(Modifier.height(14.dp))
                SectionLabel("Model")
                }
                items(options.models) { model -> ModelRow(model, options.model, state.link == Link.Online, onModel) }
            }
            if (options.efforts.isNotEmpty()) {
                item {
                Spacer(Modifier.height(14.dp))
                SectionLabel("Reasoning effort")
                }
                items(options.efforts) { effort -> EffortRow(effort, options.effort, state.link == Link.Online, onEffort) }
            }
        }
    }
}

@Composable
private fun ModelRow(model: ModelOption, current: String?, enabled: Boolean, onPick: (String?) -> Unit) {
    PickerRow(
        label = model.label,
        description = model.description,
        selected = model.value == current,
        enabled = enabled,
        onClick = { onPick(model.value) },
    )
}

@Composable
private fun EffortRow(effort: EffortOption, current: String?, enabled: Boolean, onPick: (String?) -> Unit) {
    // The desktop draws effort as a filled/empty dot meter; reproducing it keeps the two surfaces legible as one app.
    val meter = if (effort.steps > 0) {
        "●".repeat(effort.rank) + "○".repeat((effort.steps - effort.rank).coerceAtLeast(0))
    } else ""
    PickerRow(
        label = effort.label,
        description = effort.description,
        trailing = meter,
        selected = effort.value == current,
        enabled = enabled,
        onClick = { onPick(effort.value) },
    )
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun TodosSheet(state: UiState, onDismiss: () -> Unit) {
    SheetShell(onDismiss) {
        val todos = state.detail.todos
        item { SheetTitle(if (todos.isEmpty()) "Task list" else "Task list · ${state.detail.todosDone}/${todos.size}") }
        if (todos.isEmpty()) {
            item { EmptyNote("The agent's task list appears here once it starts planning.") }
        } else {
            items(todos) { todo ->
                val color = when {
                    todo.done -> VibeColors.Green
                    todo.active -> VibeColors.Amber
                    else -> VibeColors.Faint
                }
                Row(
                    Modifier.fillMaxWidth().padding(vertical = 7.dp),
                    verticalAlignment = Alignment.Top,
                ) {
                    Text(
                        when {
                            todo.done -> "✓"
                            todo.active -> "▸"
                            else -> "○"
                        },
                        style = MaterialTheme.typography.bodyMedium,
                        color = color,
                        modifier = Modifier.width(22.dp),
                    )
                    Text(
                        todo.text,
                        style = MaterialTheme.typography.bodyMedium,
                        color = if (todo.done) VibeColors.Faint else VibeColors.Text,
                    )
                }
            }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun FilesSheet(state: UiState, onDismiss: () -> Unit) {
    SheetShell(onDismiss) {
        val files = state.detail.files
        item { SheetTitle(if (files.isEmpty()) "Files" else "Files · ${files.size}") }
        if (files.isEmpty()) {
            item { EmptyNote("Files the agent creates or edits show up here.") }
        } else {
                items(files) { file ->
                    Column(Modifier.fillMaxWidth().padding(vertical = 7.dp)) {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Text(
                                file.name,
                                style = MaterialTheme.typography.bodyMedium,
                                color = VibeColors.Text,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis,
                                modifier = Modifier.weight(1f),
                            )
                            if (file.writes > 1) {
                                Spacer(Modifier.width(8.dp))
                                Text(
                                    "${file.writes} edits",
                                    style = MaterialTheme.typography.labelSmall,
                                    color = VibeColors.Faint,
                                )
                            }
                        }
                        Text(
                            file.path,
                            style = MonoStyle,
                            color = VibeColors.Faint,
                        )
                    }
                }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun CommandsSheet(state: UiState, onDismiss: () -> Unit, onPick: (String) -> Unit) {
    SheetShell(onDismiss) {
        val commands = state.options?.commands.orEmpty()
        item { SheetTitle("Slash commands") }
        if (commands.isEmpty()) {
            item {
            EmptyNote(
                if (state.optionsBusy) "Asking the PC what this chat supports…"
                else "This chat's CLI has not advertised any commands."
            )
            }
        } else {
                items(commands) { command ->
                    Column(
                        Modifier
                            .fillMaxWidth()
                            .clip(RoundedCornerShape(9.dp))
                            .clickable(role = Role.Button) { onPick(command.name) }
                            .heightIn(min = 48.dp)
                            .padding(horizontal = 8.dp, vertical = 9.dp),
                    ) {
                        Column {
                            Text("/${command.name}", style = MonoStyle, color = VibeColors.Accent)
                            if (command.hint.isNotBlank()) {
                                Text(command.hint, style = MonoStyle, color = VibeColors.Faint)
                            }
                        }
                        if (command.description.isNotBlank()) {
                            Text(
                                command.description,
                                style = MaterialTheme.typography.bodySmall,
                                color = VibeColors.Muted,
                            )
                        }
                    }
                }
        }
    }
}

// ---------------- shared sheet furniture ----------------

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun SheetShell(onDismiss: () -> Unit, content: LazyListScope.() -> Unit) {
    ModalBottomSheet(
        onDismissRequest = onDismiss,
        sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true),
        containerColor = VibeColors.Bg1,
        contentColor = VibeColors.Text,
    ) {
        Row(Modifier.fillMaxWidth().padding(horizontal = 8.dp), horizontalArrangement = Arrangement.End) {
            IconButton(onClick = onDismiss, modifier = Modifier.size(48.dp)) {
                Icon(Icons.Default.Close, contentDescription = "Close panel", tint = VibeColors.Muted)
            }
        }
        LazyColumn(
            Modifier.fillMaxWidth().weight(1f, fill = false),
            contentPadding = PaddingValues(start = 16.dp, end = 16.dp, bottom = 24.dp),
            content = content,
        )
    }
}

@Composable
private fun SheetTitle(text: String) {
    Text(
        text,
        style = MaterialTheme.typography.titleMedium,
        color = VibeColors.Text,
        modifier = Modifier.padding(bottom = 10.dp),
    )
}

@Composable
private fun SectionLabel(text: String) {
    Text(
        text.uppercase(),
        style = MaterialTheme.typography.labelSmall,
        color = VibeColors.Faint,
        modifier = Modifier.padding(bottom = 6.dp, top = 2.dp),
    )
}

@Composable
private fun EmptyNote(text: String) {
    Text(
        text,
        style = MaterialTheme.typography.bodySmall,
        color = VibeColors.Faint,
        modifier = Modifier.padding(vertical = 14.dp),
    )
}

@Composable
private fun PickerRow(
    label: String,
    description: String = "",
    trailing: String = "",
    selected: Boolean,
    enabled: Boolean = true,
    toggle: Boolean = false,
    accent: Color = VibeColors.Accent,
    onClick: () -> Unit,
) {
    val border = if (selected) accent else VibeColors.BorderSoft
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .padding(vertical = 4.dp)
            .clip(RoundedCornerShape(10.dp))
            .background(if (selected) accent.copy(alpha = 0.10f) else Color.Transparent)
            .border(1.dp, border, RoundedCornerShape(10.dp))
            .selectable(selected = selected, enabled = enabled, role = if (toggle) Role.Switch else Role.RadioButton, onClick = onClick)
            .heightIn(min = 48.dp)
            .padding(horizontal = 12.dp, vertical = 11.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(Modifier.weight(1f)) {
            Text(
                label,
                style = MaterialTheme.typography.titleSmall,
                fontWeight = if (selected) FontWeight.SemiBold else FontWeight.Normal,
                color = when {
                    !enabled -> VibeColors.Faint
                    selected -> accent
                    else -> VibeColors.Text
                },
            )
            if (description.isNotBlank()) {
                Text(description, style = MaterialTheme.typography.bodySmall, color = VibeColors.Faint)
            }
        }
        if (trailing.isNotBlank()) {
            Spacer(Modifier.width(10.dp))
            Text(trailing, style = MaterialTheme.typography.bodySmall, color = VibeColors.Muted)
        }
        if (toggle) {
            Spacer(Modifier.width(10.dp))
            Switch(checked = selected, onCheckedChange = null, enabled = enabled)
        } else if (selected) {
            Spacer(Modifier.width(10.dp))
            Text("✓", style = MaterialTheme.typography.titleSmall, color = accent)
        }
    }
}

/** Small rounded status pill used across the chat header. */
@Composable
fun Pill(
    text: String,
    color: Color = VibeColors.Muted,
    onClick: (() -> Unit)? = null,
) {
    Box(
        Modifier
            .clip(RoundedCornerShape(7.dp))
            .background(color.copy(alpha = 0.13f))
            .then(if (onClick != null) Modifier.clickable(role = Role.Button, onClick = onClick).heightIn(min = 48.dp) else Modifier)
            .padding(horizontal = 8.dp, vertical = 4.dp),
        contentAlignment = Alignment.Center,
    ) {
        Text(
            text,
            style = MaterialTheme.typography.labelSmall,
            color = color,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
    }
}

/** Row of pills under the chat title. Shared so the list and the transcript read identically. */
@Composable
fun PillRow(items: List<Pair<String, Color>>, contentPadding: PaddingValues = PaddingValues(0.dp)) {
    Row(
        Modifier.padding(contentPadding),
        horizontalArrangement = Arrangement.spacedBy(6.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        items.forEach { (text, color) -> Pill(text, color) }
    }
}
