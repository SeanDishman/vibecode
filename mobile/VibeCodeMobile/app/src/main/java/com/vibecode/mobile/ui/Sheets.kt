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
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.vibecode.mobile.UiState
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
        SheetTitle("Session")

        // --- mode ---
        SectionLabel("Permissions")
        Modes.forEach { (value, label, description) ->
            val selected = detail.mode == value || options?.mode == value
            PickerRow(
                label = label,
                description = description,
                selected = selected,
                // Bypass is the one choice that removes every guardrail on a machine the user cannot see.
                accent = if (value == "bypassPermissions") VibeColors.Red else VibeColors.Accent,
                onClick = { onMode(value) },
            )
        }

        // --- fast mode ---
        if (detail.canFast || options?.canFast == true) {
            Spacer(Modifier.height(14.dp))
            SectionLabel("Speed")
            PickerRow(
                label = "Fast mode",
                description = if (detail.canFastNow) "Same model, faster output."
                else "Not available on the model this chat is using.",
                selected = detail.fast,
                enabled = detail.canFastNow || detail.fast,
                onClick = { onFast(!detail.fast) },
            )
        }

        // --- model ---
        if (options == null) {
            Spacer(Modifier.height(14.dp))
            Text(
                if (state.optionsBusy) "Loading the PC's model list…" else "The PC did not send a model list.",
                style = MaterialTheme.typography.bodySmall,
                color = VibeColors.Faint,
                modifier = Modifier.padding(horizontal = 4.dp, vertical = 8.dp),
            )
        } else {
            if (options.models.isNotEmpty()) {
                Spacer(Modifier.height(14.dp))
                SectionLabel("Model")
                options.models.forEach { model -> ModelRow(model, options.model, onModel) }
            }
            if (options.efforts.isNotEmpty()) {
                Spacer(Modifier.height(14.dp))
                SectionLabel("Reasoning effort")
                options.efforts.forEach { effort -> EffortRow(effort, options.effort, onEffort) }
            }
        }
    }
}

@Composable
private fun ModelRow(model: ModelOption, current: String?, onPick: (String?) -> Unit) {
    PickerRow(
        label = model.label,
        description = model.description,
        selected = model.value == current,
        onClick = { onPick(model.value) },
    )
}

@Composable
private fun EffortRow(effort: EffortOption, current: String?, onPick: (String?) -> Unit) {
    // The desktop draws effort as a filled/empty dot meter; reproducing it keeps the two surfaces legible as one app.
    val meter = if (effort.steps > 0) {
        "●".repeat(effort.rank) + "○".repeat((effort.steps - effort.rank).coerceAtLeast(0))
    } else ""
    PickerRow(
        label = effort.label,
        description = effort.description,
        trailing = meter,
        selected = effort.value == current,
        onClick = { onPick(effort.value) },
    )
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun TodosSheet(state: UiState, onDismiss: () -> Unit) {
    SheetShell(onDismiss) {
        val todos = state.detail.todos
        SheetTitle(if (todos.isEmpty()) "Task list" else "Task list · ${state.detail.todosDone}/${todos.size}")
        if (todos.isEmpty()) {
            EmptyNote("The agent's task list appears here once it starts planning.")
        } else {
            todos.forEach { todo ->
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
        SheetTitle(if (files.isEmpty()) "Files" else "Files · ${files.size}")
        if (files.isEmpty()) {
            EmptyNote("Files the agent creates or edits show up here.")
        } else {
            LazyColumn(Modifier.heightIn(max = 420.dp)) {
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
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                        )
                    }
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
        SheetTitle("Slash commands")
        if (commands.isEmpty()) {
            EmptyNote(
                if (state.optionsBusy) "Asking the PC what this chat supports…"
                else "This chat's CLI has not advertised any commands."
            )
        } else {
            LazyColumn(Modifier.heightIn(max = 440.dp)) {
                items(commands) { command ->
                    Column(
                        Modifier
                            .fillMaxWidth()
                            .clip(RoundedCornerShape(9.dp))
                            .clickable { onPick(command.name) }
                            .padding(horizontal = 8.dp, vertical = 9.dp),
                    ) {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Text("/${command.name}", style = MonoStyle, color = VibeColors.Accent)
                            if (command.hint.isNotBlank()) {
                                Spacer(Modifier.width(8.dp))
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
}

// ---------------- shared sheet furniture ----------------

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun SheetShell(onDismiss: () -> Unit, content: @Composable () -> Unit) {
    ModalBottomSheet(
        onDismissRequest = onDismiss,
        sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true),
        containerColor = VibeColors.Bg1,
        contentColor = VibeColors.Text,
        dragHandle = {
            Box(Modifier.fillMaxWidth().padding(vertical = 10.dp), contentAlignment = Alignment.Center) {
                Box(Modifier.size(width = 34.dp, height = 4.dp).clip(CircleShape).background(VibeColors.Border))
            }
        },
    ) {
        Column(
            Modifier
                .fillMaxWidth()
                .navigationBarsPadding()
                .padding(horizontal = 16.dp)
                .padding(bottom = 18.dp),
        ) { content() }
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
    accent: Color = VibeColors.Accent,
    onClick: () -> Unit,
) {
    val border = if (selected) accent else VibeColors.BorderSoft
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .padding(vertical = 3.dp)
            .clip(RoundedCornerShape(10.dp))
            .background(if (selected) accent.copy(alpha = 0.10f) else Color.Transparent)
            .border(1.dp, border, RoundedCornerShape(10.dp))
            .clickable(enabled = enabled, onClick = onClick)
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
        if (selected) {
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
            .then(if (onClick != null) Modifier.clickable(onClick = onClick) else Modifier)
            .padding(horizontal = 8.dp, vertical = 4.dp),
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
