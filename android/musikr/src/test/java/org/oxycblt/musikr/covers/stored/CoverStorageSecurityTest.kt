package org.oxycblt.musikr.covers.stored

import java.io.File
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class CoverStorageSecurityTest {
    @Test
    fun fsCoverStorage_rejectsPathTraversalAndSlashNames_whileAllowingSafeIds() = runBlocking {
        val tempRoot = File(System.getProperty("java.io.tmpdir"), "tw_cover_sec_${System.nanoTime()}")
        val coversDir = File(tempRoot, "covers").apply { mkdirs() }
        val secretFile = File(tempRoot, "secret.db").apply { writeText("sensitive_db_payload") }

        try {
            val storage = CoverStorage.at(coversDir)

            assertNull(storage.find("../secret.db"))
            assertNull(storage.find("..\\secret.db"))
            assertNull(storage.find("sub/cover.jpg"))
            assertNull(storage.find("mcs\u0000evil"))

            var threw = false
            try {
                storage.write("../escaped.jpg") { it.write(byteArrayOf(1, 2, 3)) }
            } catch (_: IllegalArgumentException) {
                threw = true
            }
            assertTrue("Expected unsafe filename write to throw IllegalArgumentException", threw)

            storage.write("safe_cover_1.jpg") { it.write(byteArrayOf(7, 8, 9)) }
            val hit = storage.find("safe_cover_1.jpg")
            assertNotNull(hit)
            assertEquals("safe_cover_1.jpg", hit!!.id)
            assertTrue(secretFile.exists())
        } finally {
            tempRoot.deleteRecursively()
        }
    }
}
