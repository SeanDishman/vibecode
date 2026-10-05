package com.vibecode.mobile.data

/** In-memory drafts belong to a chat, including while its send request is in flight. */
internal class ChatDrafts {
    private val drafts = mutableMapOf<String, String>()
    private val pending = mutableMapOf<String, String>()

    fun get(chatId: String): String = drafts[chatId].orEmpty()
    fun isSending(chatId: String): Boolean = pending.containsKey(chatId)
    fun set(chatId: String, text: String) { drafts[chatId] = text }

    fun beginSend(chatId: String): String? {
        val draft = get(chatId)
        if (draft.isBlank() || isSending(chatId)) return null
        pending[chatId] = draft
        drafts.remove(chatId)
        return draft.trim()
    }

    fun finishSend(chatId: String, failed: Boolean) {
        val sent = pending.remove(chatId) ?: return
        if (failed) {
            val newer = get(chatId)
            drafts[chatId] = if (newer.isBlank()) sent else "$sent\n\n$newer"
        }
    }

    fun clear() { drafts.clear(); pending.clear() }
}
