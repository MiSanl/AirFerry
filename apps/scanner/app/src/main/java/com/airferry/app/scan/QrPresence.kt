package com.airferry.app.scan

/**
 * Pure helpers for the scanner's per-QR presence indicator.
 *
 * ZXing reports bounding boxes in the unrotated CameraX analysis buffer, while
 * the user sees the rotated PreviewView. Keeping the transform here makes the
 * four status labels follow the visible 2x2 grid on every device orientation.
 */
internal object QrPresence {
    const val SLOT_CENTER = -1

    /** Map a decoded bbox to the visible 2x2 slot (or the single-code centre). */
    fun slotOf(
        bbox: IntArray,
        frameWidth: Int,
        frameHeight: Int,
        rotationDegrees: Int,
        trackedCodeCount: Int,
    ): Int {
        if (trackedCodeCount <= 1 || bbox.size < 4 || frameWidth <= 0 || frameHeight <= 0) {
            return SLOT_CENTER
        }

        val x = ((bbox[0].toDouble() + bbox[2].toDouble()) * 0.5 / frameWidth)
            .coerceIn(0.0, 1.0)
        val y = ((bbox[1].toDouble() + bbox[3].toDouble()) * 0.5 / frameHeight)
            .coerceIn(0.0, 1.0)

        // imageInfo.rotationDegrees is the clockwise rotation required to turn
        // the analysis buffer into the upright image shown by PreviewView.
        val rotation = ((rotationDegrees % 360) + 360) % 360
        val (visibleX, visibleY) = when (rotation) {
            90 -> (1.0 - y) to x
            180 -> (1.0 - x) to (1.0 - y)
            270 -> y to (1.0 - x)
            else -> x to y
        }

        val right = visibleX > 0.5
        val bottom = visibleY > 0.5
        return when {
            !right && !bottom -> 0
            right && !bottom -> 1
            !right && bottom -> 2
            else -> 3
        }
    }

    /** Build the compact single-/four-code presence string shown in the card. */
    fun statusString(
        activity: Map<Int, Long>,
        trackedCodeCount: Int,
        nowMs: Long,
        activeWindowMs: Long,
    ): String {
        if (activity.isEmpty()) return "等待扫描…"

        fun marker(slot: Int): String {
            val last = activity[slot]
            return when {
                last == null -> "·"
                nowMs - last < activeWindowMs -> "●"
                else -> "○"
            }
        }

        // A real quadrant timestamp is stronger evidence of multi-code mode than
        // a transient tracker count. Keep all four positions visible thereafter.
        val quadrantCount = activity.keys.count { it in 0..3 }
        val effectiveCount = if (quadrantCount > 0) {
            maxOf(trackedCodeCount, quadrantCount).coerceIn(2, 4)
        } else {
            trackedCodeCount
        }
        if (effectiveCount <= 1) {
            return when (val dot = marker(SLOT_CENTER)) {
                "·" -> "等待扫描…"
                "●" -> "$dot 活跃"
                else -> "$dot 暂停"
            }
        }

        val labels = arrayOf("①", "②", "③", "④")
        return List(4) { index -> "${labels[index]}${marker(index)}" }.joinToString(" ")
    }
}
