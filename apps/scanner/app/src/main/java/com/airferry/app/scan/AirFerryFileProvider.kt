package com.airferry.app.scan

import android.net.Uri
import androidx.core.content.FileProvider

/**
 * FileProvider that reports the MIME type of the logical filename carried by
 * AndroidX's four-argument `getUriForFile(..., displayName)` API.
 *
 * The default provider derives MIME from the physical file. ContentStore files
 * are extensionless SHA-256 blobs, so that default is always
 * `application/octet-stream` even when DISPLAY_NAME is `report.pdf`.
 */
class AirFerryFileProvider : FileProvider() {

    override fun getType(uri: Uri): String =
        logicalType(uri) ?: super.getType(uri) ?: "application/octet-stream"

    override fun getTypeAnonymous(uri: Uri): String =
        logicalType(uri) ?: super.getTypeAnonymous(uri) ?: "application/octet-stream"

    private fun logicalType(uri: Uri): String? =
        uri.getQueryParameter(DISPLAY_NAME_PARAM)
            ?.takeIf { it.isNotBlank() }
            ?.let(FileTransfer::mimeType)

    private companion object {
        // AndroidX FileProvider's public four-argument helper uses this query key.
        const val DISPLAY_NAME_PARAM = "displayName"
    }
}
