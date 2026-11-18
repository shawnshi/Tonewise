package com.shawnshi.tonewise.home.tabs

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Test
import com.shawnshi.tonewise.music.MusicType

class TabSerializationTest {
    @Test
    fun legacy5TabSequenceMigratesTo8Tabs() {
        // Legacy V1 default: Songs, Albums, Artists, Genres, Playlists (all visible)
        val legacy5 = 0b1000_1001_1010_1011_1100
        val migrated = Tab.fromIntCode(legacy5)

        assertNotNull(migrated)
        assertEquals(8, migrated!!.size)
        assertEquals(Tab.Visible(MusicType.SONGS), migrated[0])
        assertEquals(Tab.Visible(MusicType.ALBUMS), migrated[1])
        assertEquals(Tab.Visible(MusicType.ARTISTS), migrated[2])
        assertEquals(Tab.Visible(MusicType.GENRES), migrated[3])
        assertEquals(Tab.Visible(MusicType.PLAYLISTS), migrated[4])
        assertEquals(Tab.Visible(MusicType.SCENES), migrated[5])
        assertEquals(Tab.Visible(MusicType.COMPOSERS), migrated[6])
        assertEquals(Tab.Visible(MusicType.CONDUCTORS), migrated[7])
    }

    @Test
    fun legacy6TabV2SequenceMigratesTo8TabsPreservingOrderAndVisibility() {
        // Legacy V2: SEQ_FORMAT_V2 (1 shl 24) + Invisible(SONGS), Visible(SCENES), ALBUMS, ARTISTS, GENRES, PLAYLISTS
        val legacy6V2 = (1 shl 24) or 0b0000_1101_1001_1010_1011_1100
        val migrated = Tab.fromIntCode(legacy6V2)

        assertNotNull(migrated)
        assertEquals(8, migrated!!.size)
        assertEquals(Tab.Invisible(MusicType.SONGS), migrated[0])
        assertEquals(Tab.Visible(MusicType.SCENES), migrated[1])
        assertEquals(Tab.Visible(MusicType.ALBUMS), migrated[2])
        assertEquals(Tab.Visible(MusicType.ARTISTS), migrated[3])
        assertEquals(Tab.Visible(MusicType.GENRES), migrated[4])
        assertEquals(Tab.Visible(MusicType.PLAYLISTS), migrated[5])
        assertEquals(Tab.Visible(MusicType.COMPOSERS), migrated[6])
        assertEquals(Tab.Visible(MusicType.CONDUCTORS), migrated[7])
    }

    @Test
    fun v3SequenceRoundTripsCustomOrderAndBit24Boundary() {
        val defaultTabs = Tab.fromIntCode(Tab.SEQUENCE_DEFAULT)!!
        assertEquals(8, defaultTabs.size)
        assertEquals(MusicType.COMPOSERS, defaultTabs[6].type)
        assertEquals(MusicType.CONDUCTORS, defaultTabs[7].type)

        // Slots 0 & 1 Invisible(SONGS), Invisible(ALBUMS) produce high bits 0x01 (same byte as V2 marker);
        // verify V3 decoder wins unambiguously over legacy V2 decoding.
        val customTabs =
            arrayOf(
                Tab.Invisible(MusicType.SONGS),
                Tab.Invisible(MusicType.ALBUMS),
                Tab.Visible(MusicType.COMPOSERS),
                Tab.Visible(MusicType.CONDUCTORS),
                Tab.Visible(MusicType.SCENES),
                Tab.Visible(MusicType.ARTISTS),
                Tab.Visible(MusicType.GENRES),
                Tab.Visible(MusicType.PLAYLISTS),
            )
        val encoded = Tab.toIntCode(customTabs)
        val decoded = Tab.fromIntCode(encoded)

        assertNotNull(decoded)
        assertEquals(customTabs.toList(), decoded!!.toList())
    }
}
