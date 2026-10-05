package com.vibecode.mobile.ui

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.OutlinedTextFieldDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.vibecode.mobile.PairState
import com.vibecode.mobile.PairStep

/**
 * The three-step pairing flow.
 *
 * Step two is the one that matters and the one most apps skip: before any credential is exchanged, the user is
 * shown the safety code derived from the certificate that was actually offered and asked whether it matches the
 * PC. That is what turns "trust whatever answered" into "trust the machine I am looking at".
 */
@Composable
fun PairScreen(
    state: PairState,
    onAddressChanged: (String) -> Unit,
    onDiscover: () -> Unit,
    onConfirm: () -> Unit,
    onBack: () -> Unit,
    onCodeChanged: (String) -> Unit,
    onSubmitCode: () -> Unit,
) {
    Box(Modifier.fillMaxSize().background(VibeColors.Bg0).safeDrawingPadding().imePadding(), contentAlignment = Alignment.TopCenter) {
    Column(
        modifier = Modifier
            .widthIn(max = 600.dp)
            .fillMaxWidth()
            .verticalScroll(rememberScrollState())
            .padding(horizontal = 20.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Spacer(Modifier.height(24.dp))
        BrandMark()
        Spacer(Modifier.height(18.dp))
        Text("VibeCode", style = MaterialTheme.typography.headlineSmall, color = VibeColors.Text)
        Text(
            "Your chats, on your phone",
            style = MaterialTheme.typography.bodyMedium,
            color = VibeColors.Muted,
            modifier = Modifier.padding(top = 4.dp),
        )
        Spacer(Modifier.height(38.dp))

        when (state.step) {
            PairStep.Address -> AddressStep(state, onAddressChanged, onDiscover)
            PairStep.Confirm -> ConfirmStep(state, onConfirm, onBack)
            PairStep.Code -> CodeStep(state, onCodeChanged, onSubmitCode, onBack)
        }

        AnimatedVisibility(state.error.isNotEmpty()) {
            Box(
                Modifier
                    .fillMaxWidth()
                    .padding(top = 18.dp)
                    .clip(RoundedCornerShape(10.dp))
                    .background(VibeColors.RedSoft)
                    .padding(14.dp)
            ) {
                Text(state.error, style = MaterialTheme.typography.bodySmall, color = VibeColors.Red)
            }
        }
        Spacer(Modifier.height(48.dp))
    }
    }
}

@Composable
private fun BrandMark() {
    Box(
        modifier = Modifier.size(76.dp).clip(RoundedCornerShape(22.dp)).background(VibeColors.Brand),
        contentAlignment = Alignment.Center,
    ) {
        Text(
            ">_",
            style = TextStyle(fontFamily = FontFamily.Monospace, fontSize = 28.sp, fontWeight = FontWeight.Bold),
            color = VibeColors.OnAccent,
        )
    }
}

@Composable
private fun AddressStep(state: PairState, onAddressChanged: (String) -> Unit, onDiscover: () -> Unit) {
    Card {
        StepLabel("Step 1 of 3")
        Text(
            "Open VibeCode on your PC, click the phone button in the title bar, and type the address it shows.",
            style = MaterialTheme.typography.bodyMedium,
            color = VibeColors.Muted,
            modifier = Modifier.padding(top = 8.dp, bottom = 16.dp),
        )
        OutlinedTextField(
            value = state.address,
            onValueChange = onAddressChanged,
            enabled = !state.busy,
            singleLine = true,
            label = { Text("PC address") },
            placeholder = { Text("192.168.1.20:8765", color = VibeColors.Faint) },
            textStyle = TextStyle(fontFamily = FontFamily.Monospace, fontSize = 16.sp),
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri, imeAction = ImeAction.Go),
            keyboardActions = KeyboardActions(onGo = { if (!state.busy && state.address.isNotBlank()) onDiscover() }),
            colors = fieldColors(),
            shape = RoundedCornerShape(10.dp),
            modifier = Modifier.fillMaxWidth(),
        )
        Spacer(Modifier.height(16.dp))
        PrimaryAction("Connect", state.busy, onDiscover, enabled = state.address.isNotBlank())
    }
}

@Composable
private fun ConfirmStep(state: PairState, onConfirm: () -> Unit, onBack: () -> Unit) {
    Card {
        StepLabel("Step 2 of 3")
        Text(
            "Found ${state.pcName}",
            style = MaterialTheme.typography.titleMedium,
            color = VibeColors.Text,
            modifier = Modifier.padding(top = 8.dp),
        )
        Text(
            "Check that this safety code is the same one VibeCode is showing on the PC. If it isn't, something else on the network answered — don't continue.",
            style = MaterialTheme.typography.bodyMedium,
            color = VibeColors.Muted,
            modifier = Modifier.padding(top = 8.dp, bottom = 16.dp),
        )
        Box(
            Modifier
                .fillMaxWidth()
                .clip(RoundedCornerShape(12.dp))
                .background(VibeColors.CodeBg)
                .padding(vertical = 18.dp),
            contentAlignment = Alignment.Center,
        ) {
            Text(
                state.safetyCode,
                style = TextStyle(fontFamily = FontFamily.Monospace, fontSize = 30.sp, fontWeight = FontWeight.Bold),
                color = VibeColors.Accent,
                textAlign = TextAlign.Center,
                modifier = Modifier.padding(horizontal = 8.dp),
            )
        }
        Spacer(Modifier.height(16.dp))
        PrimaryAction("It matches — continue", state.busy, onConfirm)
        TextButton(onClick = onBack, enabled = !state.busy, modifier = Modifier.fillMaxWidth().heightIn(min = 48.dp)) {
            Text("Use a different address", color = VibeColors.Muted)
        }
    }
}

@Composable
private fun CodeStep(
    state: PairState,
    onCodeChanged: (String) -> Unit,
    onSubmit: () -> Unit,
    onBack: () -> Unit,
) {
    val focus = remember { FocusRequester() }
    LaunchedEffect(Unit) { focus.requestFocus() }

    Card {
        StepLabel("Step 3 of 3")
        Text(
            "On the PC, press \"Show pairing code\" and type the six digits here.",
            style = MaterialTheme.typography.bodyMedium,
            color = VibeColors.Muted,
            modifier = Modifier.padding(top = 8.dp, bottom = 18.dp),
        )

        // A native field keeps tapping, pasting, selection, TalkBack and IME submission working together.
        OutlinedTextField(
            value = state.code,
            onValueChange = onCodeChanged,
            enabled = !state.busy,
            singleLine = true,
            label = { Text("Six-digit pairing code") },
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.NumberPassword, imeAction = ImeAction.Done),
            keyboardActions = KeyboardActions(onDone = { if (!state.busy && state.code.length == 6) onSubmit() }),
            colors = fieldColors(),
            shape = RoundedCornerShape(10.dp),
            textStyle = MaterialTheme.typography.headlineSmall.copy(fontFamily = FontFamily.Monospace),
            modifier = Modifier.fillMaxWidth().focusRequester(focus),
        )

        Spacer(Modifier.height(18.dp))
        PrimaryAction("Pair this phone", state.busy, onSubmit, enabled = state.code.length == 6)
        TextButton(onClick = onBack, enabled = !state.busy, modifier = Modifier.fillMaxWidth().heightIn(min = 48.dp)) {
            Text("Start over", color = VibeColors.Muted)
        }
    }
}

@Composable
private fun Card(content: @Composable ColumnScope.() -> Unit) {
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(16.dp))
            .background(VibeColors.Bg1)
            .border(1.dp, VibeColors.BorderSoft, RoundedCornerShape(16.dp))
            .padding(20.dp),
        content = content,
    )
}

@Composable
private fun StepLabel(text: String) {
    Text(
        text.uppercase(),
        style = MaterialTheme.typography.labelSmall,
        color = VibeColors.Faint,
    )
}

@Composable
private fun PrimaryAction(label: String, busy: Boolean, onClick: () -> Unit, enabled: Boolean = true) {
    Button(
        onClick = onClick,
        enabled = enabled && !busy,
        shape = RoundedCornerShape(10.dp),
        colors = ButtonDefaults.buttonColors(
            containerColor = VibeColors.Accent,
            contentColor = VibeColors.OnAccent,
            disabledContainerColor = VibeColors.AccentDim,
            disabledContentColor = VibeColors.OnAccent,
        ),
        modifier = Modifier.fillMaxWidth().heightIn(min = 48.dp),
    ) {
        if (busy) {
            CircularProgressIndicator(
                modifier = Modifier.size(18.dp),
                color = VibeColors.OnAccent,
                strokeWidth = 2.dp,
            )
            Spacer(Modifier.width(10.dp))
        }
        Text(label, style = MaterialTheme.typography.titleSmall)
    }
}

@Composable
internal fun fieldColors() = OutlinedTextFieldDefaults.colors(
    focusedTextColor = VibeColors.Text,
    unfocusedTextColor = VibeColors.Text,
    focusedContainerColor = VibeColors.CodeBg,
    unfocusedContainerColor = VibeColors.CodeBg,
    focusedBorderColor = VibeColors.Accent,
    unfocusedBorderColor = VibeColors.Border,
    cursorColor = VibeColors.Accent,
)
