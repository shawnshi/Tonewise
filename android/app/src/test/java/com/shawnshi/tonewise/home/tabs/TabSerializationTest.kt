/*
 * Copyright (c) 2026 Tonewise Project
 * TabSerializationTest.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.home.tabs

import com.shawnshi.tonewise.music.MusicType
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test

class TabSerializationTest {
    @Test
    fun tabSerialization_migratesLegacy5TabConfigAndRoundTrips6TabsWithInvisibleFirst() {
        // Legacy 5-tab default: Songs, Albums, Artists, Genres, Playlists (all visible)
        val legacyCode = 0b1000_1001_1010_1011_1100
        val migrated = Tab.fromIntCode(legacyCode)
        assertNotNull(migrated)
        assertEquals(6, migrated!!.size)
        assertEquals(Tab.Visible(MusicType.SCENES), migrated[5])

        // Round-trip 6 tabs with Invisible(SONGS) at index 0 and reordered SCENES at index 1
        val custom =
            arrayOf<Tab>(
                Tab.Invisible(MusicType.SONGS),
                Tab.Visible(MusicType.SCENES),
                Tab.Visible(MusicType.ALBUMS),
                Tab.Visible(MusicType.ARTISTS),
                Tab.Visible(MusicType.GENRES),
                Tab.Visible(MusicType.PLAYLISTS),
            )
        val encoded = Tab.toIntCode(custom)
        val decoded = Tab.fromIntCode(encoded)
        assertNotNull(decoded)
        assertTrue(decoded!!.contentEquals(custom))
    }
}
