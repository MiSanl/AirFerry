package com.airferry.app.scan

/**
 * Heuristic: which recovered files should open in the text (copy/share) UI
 * rather than the generic file detail screen.
 *
 * Used for:
 *  - bundle entries (mixed send: "添加文字" → named .txt, plus user-sent docs)
 *  - single-file transfers whose filename looks like plain text
 *  - history re-open when `.meta` has no `kind=text` but the name is text-like
 *
 * Extension-based only (no content sniffing): false positives are limited to
 * misnamed binaries; false negatives just fall back to the file screen.
 * Mirrors Windows `FileNameUtil.IsTextLikeName`.
 */
object TextLike {

    /**
     * Soft cap for opening a recovered file in the text (copy) UI.
     *
     * Larger text-like files fall back to the generic file screen: they are
     * archived as ordinary files and open in the detail page instead. The cap
     * must stay small — the text screen loads the whole file into one String
     * on the main thread and renders it in a single Compose Text, so a
     * multi-MB HTML/log (UTF-16 ~2x the UTF-8 size) reliably OOMs/ANRs the
     * app, and Android's ~1 MB Binder transaction cap would also break
     * clipboard copy. 256 KiB keeps realistic notes/source files inline while
     * routing "large text-like documents" (user-visible: larger HTML files)
     * to the file path. Mirrors Windows `FileNameUtil.MaxTextUiBytes`.
     */
    const val MAX_TEXT_UI_BYTES: Int = 256 * 1024

    // Only plain-note formats open in the text UI. Everything else — HTML,
    // JSON/YAML/XML, source code, logs — is handled as a regular file even
    // when it is technically text: rendering anything richer than a note in
    // the single-string text screen buys nothing and has been the source of
    // crashes (multi-MB HTML). Mirrors Windows FileNameUtil.TextLikeExtensions.
    private val EXTENSIONS = setOf("txt", "md")

    fun isTextLikeName(name: String): Boolean {
        val base = name.substringAfterLast('/', name).substringAfterLast('\\')
        val dot = base.lastIndexOf('.')
        if (dot <= 0 || dot >= base.length - 1) return false
        val ext = base.substring(dot + 1).lowercase()
        return ext in EXTENSIONS
    }

    /** True when [size] is small enough for the in-memory text UI. */
    fun fitsTextUi(size: Int): Boolean = size in 0..MAX_TEXT_UI_BYTES

    fun fitsTextUi(size: Long): Boolean = size in 0L..MAX_TEXT_UI_BYTES.toLong()

    /**
     * Decode [bytes] as UTF-8 only if they form a valid UTF-8 sequence.
     * [String] constructor replaces malformed input with U+FFFD; using that
     * for the copy UI would silently corrupt binary files that happen to have
     * a text-like extension. Returns null on invalid UTF-8.
     */
    fun decodeUtf8Strict(bytes: ByteArray): String? {
        if (!fitsTextUi(bytes.size)) return null
        return try {
            val cs = Charsets.UTF_8.newDecoder()
                .onMalformedInput(java.nio.charset.CodingErrorAction.REPORT)
                .onUnmappableCharacter(java.nio.charset.CodingErrorAction.REPORT)
            cs.decode(java.nio.ByteBuffer.wrap(bytes)).toString()
        } catch (_: Exception) {
            null
        }
    }
}
