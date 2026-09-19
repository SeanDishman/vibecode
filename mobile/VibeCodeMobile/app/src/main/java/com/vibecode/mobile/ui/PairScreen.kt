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
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardOptions
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
    Column(
        modifier = Modifier
            .fillMaxSize()
            .background(VibeColors.Bg0)
            .verticalScroll(rememberScrollState())
            .imePadding()
            .padding(horizontal = 26.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Spacer(Modifier.height(64.dp))
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
            singleLine = true,
            placeholder = { Text("192.168.1.20:8765", color = VibeColors.Faint) },
            textStyle = TextStyle(fontFamily = FontFamily.Monospace, fontSize = 16.sp),
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
            colors = fieldColors(),
            shape = RoundedCornerShape(10.dp),
            modifier = Modifier.fillMaxWidth(),
        )
        Spacer(Modifier.height(16.dp))
        PrimaryAction("Connect", state.busy, onDiscover)
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
            )
        }
        Spacer(Modifier.height(16.dp))
        PrimaryAction("It matches — continue", state.busy, onConfirm)
        TextButton(onClick = onBack, modifier = Modifier.fillMaxWidth()) {
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

        // One real text field behind six drawn boxes: the keyboard, selection and IME all behave normally, and
        // the boxes are decoration rather than six fields fighting over focus.
        BasicTextField(
            value = state.code,
            onValueChange = onCodeChanged,
            singleLine = true,
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.NumberPassword),
            cursorBrush = SolidColor(VibeColors.Accent),
            textStyle = TextStyle(color = VibeColors.Text),
            modifier = Modifier.fillMaxWidth().focusRequester(focus),
            decorationBox = {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    repeat(6) { index ->
                        val char = state.code.getOrNull(index)
                        val filled = char != null
                        Box(
                            modifier = Modifier
                                .weight(1f)
                                .height(58.dp)
                                .clip(RoundedCornerShape(10.dp))
                                .background(VibeColors.CodeBg)
                                .border(
                                    width = if (filled) 1.5.dp else 1.dp,
                                    color = if (filled) VibeColors.Accent else VibeColors.Border,
                                    shape = RoundedCornerShape(10.dp),
                                ),
                            contentAlignment = Alignment.Center,
                        ) {
                            Text(
                                char?.toString() ?: "",
                                style = TextStyle(
                                    fontFamily = FontFamily.Monospace,
                                    fontSize = 22.sp,
                                    fontWeight = FontWeight.Bold,
                                    textAlign = TextAlign.Center,
                                ),
                                color = VibeColors.Text,
                            )
                        }
                    }
                }
            },
        )

        Spacer(Modifier.height(18.dp))
        PrimaryAction("Pair this phone", state.busy, onSubmit)
        TextButton(onClick = onBack, modifier = Modifier.fillMaxWidth()) {
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
private fun PrimaryAction(label: String, busy: Boolean, onClick: () -> Unit) {
    Button(
        onClick = onClick,
        enabled = !busy,
        shape = RoundedCornerShape(10.dp),
        colors = ButtonDefaults.buttonColors(
            containerColor = VibeColors.Accent,
            contentColor = VibeColors.OnAccent,
            disabledContainerColor = VibeColors.AccentDim,
            disabledContentColor = VibeColors.OnAccent,
        ),
        modifier = Modifier.fillMaxWidth().height(48.dp),
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
