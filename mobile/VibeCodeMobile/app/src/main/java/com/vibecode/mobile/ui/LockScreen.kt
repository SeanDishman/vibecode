package com.vibecode.mobile.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp

/**
 * What the app shows while locked. Deliberately says nothing about the paired PC or any chat — this is also the
 * screen behind the recents thumbnail on devices that ignore FLAG_SECURE.
 */
@Composable
fun LockScreen(onUnlock: () -> Unit) {
    Box(
        Modifier.fillMaxSize().background(VibeColors.Bg0).padding(32.dp),
        contentAlignment = Alignment.Center,
    ) {
        Column(horizontalAlignment = Alignment.CenterHorizontally) {
            Box(
                Modifier.size(64.dp).clip(CircleShape).background(VibeColors.Bg2),
                contentAlignment = Alignment.Center,
            ) {
                Icon(Icons.Default.Lock, contentDescription = null, tint = VibeColors.Accent)
            }
            Spacer(Modifier.height(20.dp))
            Text("VibeCode is locked", style = MaterialTheme.typography.titleMedium, color = VibeColors.Text)
            Spacer(Modifier.height(8.dp))
            Text(
                "This phone can send commands to your PC, so it asks who you are first.",
                style = MaterialTheme.typography.bodyMedium,
                color = VibeColors.Faint,
                textAlign = TextAlign.Center,
            )
            Spacer(Modifier.height(24.dp))
            Text(
                "Unlock",
                style = MaterialTheme.typography.titleSmall,
                color = VibeColors.OnAccent,
                modifier = Modifier
                    .clip(RoundedCornerShape(11.dp))
                    .background(VibeColors.Accent)
                    .clickable(onClick = onUnlock)
                    .padding(horizontal = 30.dp, vertical = 13.dp),
            )
        }
    }
}
