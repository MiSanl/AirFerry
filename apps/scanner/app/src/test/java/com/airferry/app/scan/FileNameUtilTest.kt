package com.airferry.app.scan

import org.junit.Assert.assertEquals
import org.junit.Test

class FileNameUtilTest {
    @Test
    fun removesTraversalAndIllegalCharactersButKeepsUnicode() {
        assertEquals("报告 2026（终稿）.txt", FileNameUtil.sanitize("../../报告 2026（终稿）.txt"))
        assertEquals("a_b_c_.txt", FileNameUtil.sanitize("a:b?c*.txt"))
        assertEquals("received_file", FileNameUtil.sanitize("../.."))
    }
}
