package com.vibecode.mobile.ui

import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.dp

/**
 * Just enough Markdown to make agent output readable on a phone.
 *
 * Agents write fenced code, `inline code`, **bold** and bullet lists constantly, and rendering those as literal
 * asterisks and backticks is the difference between a transcript you can read on a bus and a wall of noise. A
 * full CommonMark renderer would be a dependency and a scrolling-performance problem for a screen whose longest
 * message is usually a shell transcript, so this handles the four things that actually show up and leaves
 * everything else as plain text.
 */
@Composable
fun RichText(
    text: String,
    modifier: Modifier = Modifier,
    style: TextStyle = androidx.compose.material3.MaterialTheme.typography.bodyMedium,
    color: Color = VibeColors.Text,
) {
    val blocks = remember(text) { splitBlocks(text) }
    Column(modifier = modifier) {
        blocks.forEach { block ->
            when (block) {
                is Block.Code -> CodeBlock(block.language, block.code)
                is Block.Prose -> Text(
                    text = remember(block.text) { inline(block.text) },
                    style = style,
                    color = color,
                    modifier = Modifier.fillMaxWidth(),
                )
            }
        }
    }
}

@Composable
private fun CodeBlock(language: String, code: String) {
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .padding(vertical = 6.dp)
            .clip(RoundedCornerShape(8.dp))
            .background(VibeColors.CodeBg)
            .padding(PaddingValues(horizontal = 12.dp, vertical = 10.dp)),
    ) {
        if (language.isNotBlank()) {
            Text(language, style = MonoStyle, color = VibeColors.Faint, modifier = Modifier.padding(bottom = 6.dp))
        }
        // Code does not wrap well at phone widths; scrolling sideways preserves the alignment that makes a diff
        // or a stack trace legible in the first place.
        Text(
            text = code,
            style = MonoStyle,
            color = VibeColors.Text,
            softWrap = false,
            modifier = Modifier.horizontalScroll(rememberScrollState()),
        )
    }
}

private sealed interface Block {
    data class Prose(val text: String) : Block
    data class Code(val language: String, val code: String) : Block
}

private fun splitBlocks(text: String): List<Block> {
    val blocks = mutableListOf<Block>()
    val prose = StringBuilder()
    val code = StringBuilder()
    var inFence = false
    var language = ""

    fun flushProse() {
        val body = prose.toString().trim('\n')
        if (body.isNotEmpty()) blocks += Block.Prose(body)
        prose.setLength(0)
    }

    text.split('\n').forEach { line ->
        val trimmed = line.trimStart()
        if (trimmed.startsWith("```")) {
            if (inFence) {
                blocks += Block.Code(language, code.toString().trimEnd('\n'))
                code.setLength(0)
                inFence = false
            } else {
                flushProse()
                language = trimmed.removePrefix("```").trim()
                inFence = true
            }
            return@forEach
        }
        if (inFence) code.append(line).append('\n') else prose.append(line).append('\n')
    }

    // An unterminated fence is normal: it is what a code block looks like while it is still streaming.
    if (inFence && code.isNotEmpty()) blocks += Block.Code(language, code.toString().trimEnd('\n'))
    if (!inFence) flushProse()
    return blocks
}

/** Handles `code`, **bold**, *italic*, and turns "- " / "* " list markers into real bullets. */
private fun inline(source: String): AnnotatedString = buildAnnotatedString {
    val text = source.lines().joinToString("\n") { line ->
        val trimmed = line.trimStart()
        when {
            trimmed.startsWith("- ") || trimmed.startsWith("* ") ->
                line.substring(0, line.length - trimmed.length) + "•  " + trimmed.substring(2)
            else -> line
        }
    }

    var i = 0
    while (i < text.length) {
        when {
            text.startsWith("```", i) -> { append("```"); i += 3 }

            text[i] == '`' -> {
                val end = text.indexOf('`', i + 1)
                if (end < 0) { append(text[i]); i++ } else {
                    withStyle(SpanStyle(fontFamily = FontFamily.Monospace, color = VibeColors.Blue)) {
                        append(text.substring(i + 1, end))
                    }
                    i = end + 1
                }
            }

            text.startsWith("**", i) -> {
                val end = text.indexOf("**", i + 2)
                if (end < 0) { append("**"); i += 2 } else {
                    withStyle(SpanStyle(fontWeight = FontWeight.SemiBold)) { append(text.substring(i + 2, end)) }
                    i = end + 2
                }
            }

            text[i] == '*' -> {
                val end = text.indexOf('*', i + 1)
                // A lone asterisk mid-sentence is not emphasis; only close pairs on the same line count.
                if (end < 0 || text.substring(i + 1, end).contains('\n')) { append(text[i]); i++ } else {
                    withStyle(SpanStyle(fontStyle = FontStyle.Italic)) { append(text.substring(i + 1, end)) }
                    i = end + 1
                }
            }

            else -> { append(text[i]); i++ }
        }
    }
}
