package com.airferry.app.scan

import org.junit.Assert.assertEquals
import org.junit.Test

class QrPresenceTest {
    private val topLeft = intArrayOf(100, 50, 300, 250)

    @Test
    fun `single code always maps to centre`() {
        assertEquals(
            QrPresence.SLOT_CENTER,
            QrPresence.slotOf(topLeft, 1_000, 600, 90, trackedCodeCount = 1),
        )
    }

    @Test
    fun `quadrants follow clockwise CameraX rotation`() {
        assertEquals(0, QrPresence.slotOf(topLeft, 1_000, 600, 0, 4))
        assertEquals(1, QrPresence.slotOf(topLeft, 1_000, 600, 90, 4))
        assertEquals(3, QrPresence.slotOf(topLeft, 1_000, 600, 180, 4))
        assertEquals(2, QrPresence.slotOf(topLeft, 1_000, 600, 270, 4))
    }

    @Test
    fun `presence expires even when no new frame arrives`() {
        val seen = mapOf(0 to 1_000L, 1 to 2_500L)
        assertEquals(
            "①○ ②● ③· ④·",
            QrPresence.statusString(seen, 4, nowMs = 3_000L, activeWindowMs = 1_000L),
        )
    }

    @Test
    fun `single code reports active and paused`() {
        val seen = mapOf(QrPresence.SLOT_CENTER to 1_000L)
        assertEquals("● 活跃", QrPresence.statusString(seen, 1, 1_500L, 1_000L))
        assertEquals("○ 暂停", QrPresence.statusString(seen, 1, 2_000L, 1_000L))
    }
}
