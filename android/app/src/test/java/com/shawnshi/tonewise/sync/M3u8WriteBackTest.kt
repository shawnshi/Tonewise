/*
 * Copyright (c) 2026 Tonewise Project
 * M3u8WriteBackTest.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.sync

import androidx.compose.ui.unit.dp
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import com.shawnshi.tonewise.playback.ui.clock.computePixelShiftDp
import org.oxycblt.musikr.fs.Components

class M3u8WriteBackTest {
    @Test
    fun formatM3u8Entries_usesUtf8WithoutBomAndPosixPaths() {
        val entries =
            listOf(
                LanSyncRepository.M3u8Entry(
                    durationMs = 524_000L,
                    title = "II. Allegretto",
                    artists = listOf("Carlos Kleiber"),
                    pathComponents =
                        Components.parseUnix("Music/Classical/Beethoven7/02_Allegretto.flac"),
                ),
                LanSyncRepository.M3u8Entry(
                    durationMs = 301_000L,
                    title = "I. Poco sostenuto",
                    artists = listOf("Carlos Kleiber", "Wiener Philharmoniker"),
                    pathComponents =
                        Components.parseWindows("Music\\Classical\\Beethoven7\\01_Sostenuto.flac"),
                ),
            )

        val content = LanSyncRepository.formatM3u8Entries(entries)
        val bytes = content.toByteArray(Charsets.UTF_8)

        // Ensure no UTF-8 BOM (EF BB BF)
        assertFalse(
            bytes.size >= 3 &&
                bytes[0] == 0xEF.toByte() &&
                bytes[1] == 0xBB.toByte() &&
                bytes[2] == 0xBF.toByte()
        )
        assertTrue(content.startsWith("#EXTM3U\n"))
        assertTrue(content.contains("#EXTINF:524,Carlos Kleiber - II. Allegretto\n"))
        assertTrue(content.contains("Music/Classical/Beethoven7/02_Allegretto.flac\n"))
        assertTrue(
            content.contains(
                "#EXTINF:301,Carlos Kleiber, Wiener Philharmoniker - I. Poco sostenuto\n"
            )
        )
        assertTrue(content.contains("Music/Classical/Beethoven7/01_Sostenuto.flac\n"))
        assertFalse(content.contains("\\"))
        assertEquals(
            "Focus_深度专注",
            LanSyncRepository.sanitizePlaylistFilename("Focus · 深度专注".replace("·", "")),
        )
    }

    @Test
    fun deskClock_cyclesPixelShiftAcrossFourCorners() {
        val cycle = (0..4).map { computePixelShiftDp(it) }
        assertEquals((-2).dp to (-2).dp, cycle[0])
        assertEquals(2.dp to (-2).dp, cycle[1])
        assertEquals(2.dp to 2.dp, cycle[2])
        assertEquals((-2).dp to 2.dp, cycle[3])
        assertEquals(cycle[0], cycle[4])
    }
}
