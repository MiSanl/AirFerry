package com.airferry.app.scan

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class TextParserTest {
    private val magic = "ETTEXTv1".toByteArray(Charsets.US_ASCII)

    @Test
    fun parsesStrictUtf8Text() {
        val payload = magic + "离线传输\nAirFerry".toByteArray(Charsets.UTF_8)

        assertTrue(TextParser.isText(payload))
        assertEquals("离线传输\nAirFerry", TextParser.parse(payload))
    }

    @Test
    fun rejectsInvalidUtf8AndNonTextPayloads() {
        assertNull(TextParser.parse(magic + byteArrayOf(0xC3.toByte(), 0x28)))
        assertFalse(TextParser.isText("plain".toByteArray()))
        assertNull(TextParser.parse("plain".toByteArray()))
    }
}
