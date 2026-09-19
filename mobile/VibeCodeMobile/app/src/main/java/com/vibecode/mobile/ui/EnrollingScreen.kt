package com.vibecode.mobile.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import com.vibecode.mobile.UiState

/**
 * What a generated APK shows while it is claiming its one-time enrolment.
 *
 * The old version of this screen was a spinner and the word "Connecting", which is precisely the wrong thing to
 * show: the reason it is not connecting is always something on the PC's side of the wire — a VPN eating LAN
 * traffic, a firewall with no rule, phone access switched off — and none of that is guessable from a spinner. So
 * this names the address being tried, keeps a visible countdown between attempts, and states the checklist that
 * actually resolves it.
 */
@Composable
fun EnrollingScreen(state: UiState, onRetry: () -> Unit) {
    val pc = state.boundTo.ifBlank { "your PC" }

    Box(
        Modifier.fillMaxSize().background(VibeColors.Bg0).padding(32.dp),
        contentAlignment = Alignment.Center,
    ) {
        Column(horizontalAlignment = Alignment.CenterHorizontally) {
            Box(
                Modifier.size(72.dp).clip(CircleShape).background(VibeColors.Bg2),
                contentAlignment = Alignment.Center,
            ) {
                if (state.enrolBusy) {
                    CircularProgressIndicator(Modifier.size(28.dp), color = VibeColors.Accent, strokeWidth = 2.dp)
                } else {
                    Text(">_", style = MaterialTheme.typography.headlineSmall, color = VibeColors.Accent)
                }
            }

            Spacer(Modifier.height(22.dp))
            Text(
                when {
                    state.enrolFatal -> "Can't set up"
                    state.enrolRound > 1 -> "Still looking for $pc"
                    else -> "Setting up"
                },
                style = MaterialTheme.typography.headlineSmall,
                color = VibeColors.Text,
            )

            Spacer(Modifier.height(8.dp))
            Text(
                if (state.enrolFatal) {
                    state.enrolError.ifBlank { "This app can no longer set up a phone." }
                } else {
                    "This app was built by $pc and only works with it, so there is nothing to type."
                },
                style = MaterialTheme.typography.bodyMedium,
                color = VibeColors.Faint,
                textAlign = TextAlign.Center,
            )

            // The live line: which address, or how long until the next go. Its whole job is to prove the app is
            // doing something, and to name the address that is failing so it can be checked against the PC.
            if (!state.enrolFatal && state.enrolProgress.isNotBlank()) {
                Spacer(Modifier.height(14.dp))
                Text(
                    state.enrolProgress,
                    style = MaterialTheme.typography.bodySmall,
                    color = VibeColors.Accent,
                    textAlign = TextAlign.Center,
                )
            }

            if (!state.enrolFatal && state.enrolError.isNotBlank()) {
                Spacer(Modifier.height(10.dp))
                Text(
                    state.enrolError,
                    style = MaterialTheme.typography.bodySmall,
                    color = VibeColors.Faint,
                    textAlign = TextAlign.Center,
                )
            }

            // Shown once the easy explanation ("it is still starting up") has clearly been exhausted.
            if (state.enrolFatal || state.enrolRound > 1) {
                Spacer(Modifier.height(20.dp))
                Column(Modifier.fillMaxWidth().clip(RoundedCornerShape(11.dp)).background(VibeColors.Bg1).padding(16.dp)) {
                    Text(
                        if (state.enrolFatal) "What to do" else "On $pc, check:",
                        style = MaterialTheme.typography.titleSmall,
                        color = VibeColors.Text,
                    )
                    Spacer(Modifier.height(8.dp))
                    val steps = if (state.enrolFatal) {
                        listOf(
                            "Open VibeCode on $pc",
                            "Go to Phone and generate the app again",
                            "Install that new file on this phone",
                        )
                    } else {
                        listOf(
                            "VibeCode is open, and Phone access says On",
                            "The \"Your phone will not be able to reach this PC\" panel is empty — it names the exact problem and has a button to fix it",
                            "A VPN on the PC is not blocking the local network",
                            "This phone is on the same Wi-Fi, not mobile data",
                        )
                    }
                    steps.forEach { step ->
                        Text(
                            "•  $step",
                            style = MaterialTheme.typography.bodySmall,
                            color = VibeColors.Faint,
                            modifier = Modifier.padding(bottom = 6.dp),
                        )
                    }
                }
            }

            Spacer(Modifier.height(24.dp))
            Text(
                if (state.enrolFatal) "Try again" else "Try now",
                style = MaterialTheme.typography.titleSmall,
                color = VibeColors.OnAccent,
                modifier = Modifier
                    .clip(RoundedCornerShape(11.dp))
                    .background(VibeColors.Accent)
                    .clickable(onClick = onRetry)
                    .padding(horizontal = 30.dp, vertical = 13.dp),
            )
        }
    }
}
