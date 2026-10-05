package com.vibecode.mobile.data

import org.junit.Assert.*
import org.junit.Test

class TranscriptUpdateTest {
    private val first = Message("u", "first")
    private val live = Message("a", "partial", live = true)
    private val complete = Message("a", "complete")
    private fun update(base: Int, total: Int, items: List<Message>, unchanged: Boolean = false) =
        TranscriptUpdate(2, unchanged, total, base, items, null, null)

    @Test fun replacesStreamingTailAndSupportsRemovalAndDetailOnlyChanges() {
        assertEquals(listOf(first, complete), update(1, 2, listOf(complete)).applyTo(listOf(first, live)))
        assertEquals(listOf(first), update(1, 1, emptyList()).applyTo(listOf(first, live)))
        assertEquals(listOf(first, live), update(2, 2, emptyList()).applyTo(listOf(first, live)))
        assertEquals(emptyList<Message>(), update(0, 0, emptyList()).applyTo(listOf(first)))
    }

    @Test fun missingPrefixOrMalformedUpdateRequiresFullRefresh() {
        listOf(update(2, 3, listOf(complete)), update(-1, 0, emptyList()),
            update(1, 0, emptyList()), update(0, 2, listOf(first))).forEach {
            assertThrows(TranscriptOutOfSyncException::class.java) { it.applyTo(listOf(first)) }
        }
    }

    @Test fun unchangedResponseKeepsExistingTranscript() {
        val existing = listOf(first, complete)
        assertSame(existing, update(0, 0, emptyList(), unchanged = true).applyTo(existing))
    }
}
