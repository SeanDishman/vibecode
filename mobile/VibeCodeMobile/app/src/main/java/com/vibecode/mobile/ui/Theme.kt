package com.vibecode.mobile.ui

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.sp

/**
 * The desktop's palette, byte for byte (VibeCode.Desktop/Themes/Dark.xaml). The point of the phone app is that it
 * feels like the same program in your hand, and colour is most of that — so these are not "close enough" values,
 * they are the same ones.
 *
 * There is no light scheme. VibeCode has never had one, and inventing one here would make the phone the odd one
 * out on every screenshot.
 */
object VibeColors {
    val Bg0 = Color(0xFF1A1A1D)
    val Bg1 = Color(0xFF212226)
    val Bg2 = Color(0xFF27282C)
    val Bg3 = Color(0xFF2F3035)
    val CodeBg = Color(0xFF141518)
    val Border = Color(0xFF3A3B42)
    val BorderSoft = Color(0xFF2C2D32)
    val Text = Color(0xFFECEDF1)
    val Muted = Color(0xFF9EA0A8)
    val Faint = Color(0xFF6C6E77)
    val Accent = Color(0xFF4C8DF5)
    val AccentHover = Color(0xFF6BA0F7)
    val AccentSoft = Color(0x244C8DF5)
    val AccentDim = Color(0x664C8DF5)
    val OnAccent = Color(0xFFFFFFFF)
    val Green = Color(0xFF7ED0A6)
    val GreenSoft = Color(0x1E7ED0A6)
    val Red = Color(0xFFE86A78)
    val RedSoft = Color(0x22E86A78)
    val Amber = Color(0xFFE0B24D)
    val AmberSoft = Color(0x1FE0B24D)
    val Blue = Color(0xFF8FAEE6)
    val Violet = Color(0xFF8B7CF6)

    val Brand = Brush.linearGradient(listOf(Accent, Violet))

    /** One colour per CLI, so a glance at the list says which agent a chat belongs to. */
    fun provider(provider: String): Color = when (provider) {
        "codex" -> Green
        "kimi" -> Violet
        "grok" -> Amber
        else -> Accent
    }
}

private val Scheme = darkColorScheme(
    primary = VibeColors.Accent,
    onPrimary = VibeColors.OnAccent,
    primaryContainer = VibeColors.AccentSoft,
    onPrimaryContainer = VibeColors.Text,
    secondary = VibeColors.Blue,
    background = VibeColors.Bg0,
    onBackground = VibeColors.Text,
    surface = VibeColors.Bg1,
    onSurface = VibeColors.Text,
    surfaceVariant = VibeColors.Bg2,
    onSurfaceVariant = VibeColors.Muted,
    outline = VibeColors.Border,
    outlineVariant = VibeColors.BorderSoft,
    error = VibeColors.Red,
    onError = VibeColors.OnAccent,
)

private val VibeTypography = Typography(
    headlineSmall = TextStyle(fontSize = 22.sp, fontWeight = FontWeight.SemiBold, letterSpacing = (-0.2).sp),
    titleMedium = TextStyle(fontSize = 15.sp, fontWeight = FontWeight.SemiBold),
    titleSmall = TextStyle(fontSize = 13.sp, fontWeight = FontWeight.Medium),
    bodyLarge = TextStyle(fontSize = 15.sp, lineHeight = 22.sp),
    bodyMedium = TextStyle(fontSize = 13.5.sp, lineHeight = 20.sp),
    bodySmall = TextStyle(fontSize = 12.sp, lineHeight = 17.sp),
    labelSmall = TextStyle(fontSize = 10.5.sp, fontWeight = FontWeight.Medium, letterSpacing = 0.4.sp),
)

val MonoStyle = TextStyle(fontFamily = FontFamily.Monospace, fontSize = 12.sp, lineHeight = 18.sp)

@Composable
fun VibeCodeTheme(content: @Composable () -> Unit) {
    @Suppress("UNUSED_EXPRESSION")
    isSystemInDarkTheme()   // read so the app is honest about ignoring it: VibeCode is dark, always.
    MaterialTheme(colorScheme = Scheme, typography = VibeTypography, content = content)
}
