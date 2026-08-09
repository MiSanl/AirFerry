package com.airferry.app.scan

import android.app.Activity
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.webkit.MimeTypeMap
import androidx.activity.result.contract.ActivityResultContract
import androidx.core.content.FileProvider
import java.io.File
import java.util.Locale

/**
 * Preserve a logical received filename when bytes live in a content-addressed
 * blob whose physical name is only a SHA-256 digest.
 *
 * FileProvider normally reports [File.getName] through
 * `OpenableColumns.DISPLAY_NAME`. ContentStore blobs have no extension, so
 * share targets used to see only the digest plus `application/octet-stream`
 * and commonly saved the attachment as `.bin`. The four-argument
 * [FileProvider.getUriForFile] overload carries the sanitized logical name in
 * the URI metadata without making a second copy of the file.
 */
object FileTransfer {

    fun displayName(name: String): String = FileNameUtil.sanitize(name)

    /** Best-effort MIME type derived from the logical filename extension. */
    fun mimeType(name: String): String {
        val safe = displayName(name)
        val dot = safe.lastIndexOf('.')
        if (dot <= 0 || dot == safe.lastIndex) return "application/octet-stream"
        val extension = safe.substring(dot + 1).lowercase(Locale.ROOT)
        return MimeTypeMap.getSingleton().getMimeTypeFromExtension(extension)
            ?: "application/octet-stream"
    }

    /** MIME type suitable for ACTION_SEND_MULTIPLE. */
    fun commonMimeType(names: Collection<String>): String {
        val types = names.map(::mimeType).distinct()
        if (types.size == 1) return types[0]
        val families = types.map { it.substringBefore('/') }.distinct()
        return if (families.size == 1 && types.none { it == "application/octet-stream" }) {
            "${families[0]}/*"
        } else {
            "*/*"
        }
    }

    fun shareUri(context: Context, file: File, logicalName: String): Uri =
        FileProvider.getUriForFile(
            context,
            "${context.packageName}.fileprovider",
            file,
            displayName(logicalName),
        )
}

/**
 * ACTION_CREATE_DOCUMENT contract whose MIME type is selected per logical
 * filename instead of being permanently fixed to application/octet-stream.
 */
class CreateNamedDocument : ActivityResultContract<String, Uri?>() {
    override fun createIntent(context: Context, input: String): Intent {
        val name = FileTransfer.displayName(input)
        return Intent(Intent.ACTION_CREATE_DOCUMENT)
            .addCategory(Intent.CATEGORY_OPENABLE)
            .setType(FileTransfer.mimeType(name))
            .putExtra(Intent.EXTRA_TITLE, name)
    }

    override fun parseResult(resultCode: Int, intent: Intent?): Uri? =
        if (resultCode == Activity.RESULT_OK) intent?.data else null
}
