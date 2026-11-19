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
            "Focus 深度专注",
            LanSyncRepository.sanitizePlaylistFilename("Focus · 深度专注".replace("·", "")),
        )
    }

    @Test
    fun sanitizePlaylistFilename_preservesSpacesAndSanitizesIllegalCharsOnly() {
        assertEquals("All Out 2010s", LanSyncRepository.sanitizePlaylistFilename("All Out 2010s"))
        assertEquals("All Out 2000s", LanSyncRepository.sanitizePlaylistFilename("All Out 2000s"))
        assertEquals("Focus · 深度专注", LanSyncRepository.sanitizePlaylistFilename("Focus · 深度专注"))
        assertEquals("Late Night Jazz", LanSyncRepository.sanitizePlaylistFilename("  Late   Night\tJazz  "))
        assertEquals("My_Custom_Mix", LanSyncRepository.sanitizePlaylistFilename("My_Custom_Mix"))
        assertEquals("Rock_Pop_ Best_Hits_", LanSyncRepository.sanitizePlaylistFilename("Rock/Pop: Best*Hits?"))
        assertEquals("Default", LanSyncRepository.sanitizePlaylistFilename("   "))
        assertEquals("Default", LanSyncRepository.sanitizePlaylistFilename("..."))
    }

    @Test
    fun extractPlaylistNameFromM3u8_prefersPlaylistDirectiveOverFilename() {
        val withBomAndDirective = listOf("\uFEFF#EXTM3U", "#PLAYLIST:All Out 2010s", "Artist/Track.flac")
        assertEquals(
            "All Out 2010s",
            LanSyncRepository.extractPlaylistNameFromM3u8(withBomAndDirective, "All_Out_2010s"),
        )

        val lowercaseDirective = listOf("#EXTM3U", "#playlist:  Late Night Jazz  ", "Artist/Track.flac")
        assertEquals(
            "Late Night Jazz",
            LanSyncRepository.extractPlaylistNameFromM3u8(lowercaseDirective, "fallback"),
        )

        val noDirective = listOf("#EXTM3U", "Artist/Track.flac")
        assertEquals(
            "Focus Mix",
            LanSyncRepository.extractPlaylistNameFromM3u8(noDirective, "Focus Mix"),
        )
    }

    @Test
    fun formatM3u8Entries_writesPlaylistDirectiveWhenNameProvided() {
        val entries =
            listOf(
                LanSyncRepository.M3u8Entry(
                    durationMs = 180_000L,
                    title = "Track One",
                    artists = listOf("Artist A"),
                    pathComponents = Components.parseUnix("Artist A/Album/01.flac"),
                )
            )

        val withName = LanSyncRepository.formatM3u8Entries(entries, playlistName = "All Out 2010s")
        assertTrue(withName.startsWith("#EXTM3U\n#PLAYLIST:All Out 2010s\n"))

        val withoutName = LanSyncRepository.formatM3u8Entries(entries)
        assertTrue(withoutName.startsWith("#EXTM3U\n#EXTINF:"))
        assertFalse(withoutName.contains("#PLAYLIST:"))
    }

    @Test
    fun normalizePlaylistEntryForPc_stripsMusicAndLegacyTonewisePrefixes() {
        assertEquals(
            "Artist/Album/01.flac",
            LanSyncRepository.normalizePlaylistEntryForPc("Music/Artist/Album/01.flac"),
        )
        assertEquals(
            "Artist/Album/01.flac",
            LanSyncRepository.normalizePlaylistEntryForPc("/storage/emulated/0/Music/Artist/Album/01.flac"),
        )
        assertEquals(
            "Artist/Album/01.flac",
            LanSyncRepository.normalizePlaylistEntryForPc("Music/Tonewise/Artist/Album/01.flac"),
        )
        assertEquals(
            "Artist/Album/01.flac",
            LanSyncRepository.normalizePlaylistEntryForPc("Tonewise/Artist/Album/01.flac"),
        )
        assertEquals(
            "Artist/Album/01.flac",
            LanSyncRepository.normalizePlaylistEntryForPc("Artist/Album/01.flac"),
        )
    }

    @Test
    fun deskClock_cyclesPixelShiftAcrossSixteenPointOrbitWithinTwelveDp() {
        val cycle = (0 until 16).map { computePixelShiftDp(it) }
        assertEquals(16, cycle.distinct().size)
        for ((dx, dy) in cycle) {
            assertTrue(dx.value in -12f..12f)
            assertTrue(dy.value in -12f..12f)
        }
        assertEquals(cycle[0], computePixelShiftDp(16))
        assertEquals(cycle[15], computePixelShiftDp(-1))
    }
}
