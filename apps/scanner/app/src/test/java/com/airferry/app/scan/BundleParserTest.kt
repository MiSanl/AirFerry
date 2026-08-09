package com.airferry.app.scan

import java.io.ByteArrayOutputStream
import java.io.DataOutputStream
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class BundleParserTest {
    @Test
    fun parsesBigEndianBundle() {
        val payload = bundle(
            "报告 2026.txt" to "hello".toByteArray(),
            "空.dat" to byteArrayOf()
        )

        assertTrue(BundleParser.isBundle(payload))
        val parsed = requireNotNull(BundleParser.parse(payload))
        assertEquals(listOf("报告 2026.txt", "空.dat"), parsed.files.map { it.name })
        assertArrayEquals("hello".toByteArray(), parsed.files[0].data)
        assertArrayEquals(byteArrayOf(), parsed.files[1].data)
    }

    @Test
    fun rejectsTruncatedOrEmptyBundle() {
        val valid = bundle("a.bin" to byteArrayOf(1, 2, 3))
        assertNull(BundleParser.parse(valid.copyOf(valid.size - 1)))

        val empty = ByteArrayOutputStream().also { output ->
            DataOutputStream(output).use { data ->
                data.write("ETBUNDL1".toByteArray(Charsets.US_ASCII))
                data.writeShort(1)
                data.writeShort(0)
            }
        }.toByteArray()
        assertNull(BundleParser.parse(empty))
    }

    private fun bundle(vararg files: Pair<String, ByteArray>): ByteArray =
        ByteArrayOutputStream().also { output ->
            DataOutputStream(output).use { data ->
                data.write("ETBUNDL1".toByteArray(Charsets.US_ASCII))
                data.writeShort(1)
                data.writeShort(files.size)
                for ((name, bytes) in files) {
                    val encodedName = name.toByteArray(Charsets.UTF_8)
                    data.writeShort(encodedName.size)
                    data.write(encodedName)
                    data.writeLong(bytes.size.toLong())
                    data.write(bytes)
                }
            }
        }.toByteArray()
}
