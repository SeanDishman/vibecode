package com.vibecode.mobile.data

import org.junit.Assert.*
import org.junit.Test

class ChatDraftsTest {
    @Test fun delayedFailurePreservesBothChatsAndNewTyping() {
        val drafts = ChatDrafts()
        drafts.set("first", " original prompt ")
        assertEquals("original prompt", drafts.beginSend("first"))
        assertNull(drafts.beginSend("first"))
        drafts.set("second", "another conversation")
        drafts.set("first", "new typing")
        drafts.finishSend("first", failed = true)
        assertEquals(" original prompt \n\nnew typing", drafts.get("first"))
        assertEquals("another conversation", drafts.get("second"))
        assertFalse(drafts.isSending("first"))
    }

    @Test fun successKeepsNewDraftAndRevocationDiscardsPendingText() {
        val drafts = ChatDrafts()
        drafts.set("chat", "sent")
        drafts.beginSend("chat")
        drafts.set("chat", "next")
        drafts.finishSend("chat", failed = false)
        assertEquals("next", drafts.get("chat"))
        drafts.beginSend("chat")
        drafts.clear()
        drafts.finishSend("chat", failed = true)
        assertEquals("", drafts.get("chat"))
        assertFalse(drafts.isSending("chat"))
    }
}
