/*
 * Copyright (c) 2026 Tonewise Project
 * WorkShuffleOrderTest.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.playback.service

import android.net.Uri
import java.util.UUID
import kotlin.random.Random
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.oxycblt.musikr.Album
import org.oxycblt.musikr.Artist
import org.oxycblt.musikr.Genre
import org.oxycblt.musikr.Music
import org.oxycblt.musikr.Song
import org.oxycblt.musikr.covers.Cover
import org.oxycblt.musikr.covers.CoverCollection
import org.oxycblt.musikr.fs.Format
import org.oxycblt.musikr.fs.Path
import org.oxycblt.musikr.tag.Date
import org.oxycblt.musikr.tag.Disc
import org.oxycblt.musikr.tag.Name
import org.oxycblt.musikr.tag.ReleaseType
import org.oxycblt.musikr.tag.ReplayGainAdjustment
import org.oxycblt.musikr.tag.interpret.Naming

class WorkShuffleOrderTest {
    @Test
    fun workShuffle_keepsMovementsContiguousAndOrdered() {
        val albumA = FakeAlbum("Beethoven 7")
        val albumB = FakeAlbum("Goldberg")
        val symMvt2 = FakeSong(albumA, "Symphony No. 7", "II. Allegretto", movementNumber = 2, track = 2)
        val symMvt1 = FakeSong(albumA, "Symphony No. 7", "I. Poco sostenuto", movementNumber = 1, track = 1)
        val symMvt3 = FakeSong(albumA, "Symphony No. 7", "III. Presto", movementNumber = 3, track = 3)
        val standalone = FakeSong(albumA, null, "Overture", movementNumber = null, track = 4)
        val goldberg1 = FakeSong(albumB, "Goldberg Variations", "Aria", movementNumber = 1, track = 1)
        val goldberg2 = FakeSong(albumB, "Goldberg Variations", "Var. 1", movementNumber = 2, track = 2)

        val songs = listOf(symMvt2, goldberg2, symMvt1, standalone, goldberg1, symMvt3)
        val order = WorkShuffleOrder.createWorkAwareOrder(songs, startIndex = 2, random = Random(42))

        assertEquals(songs.size, order.size)
        // Anchor work ("Symphony No. 7") appears first in strict movement order 1 -> 2 -> 3
        val firstThree = listOf(songs[order[0]], songs[order[1]], songs[order[2]])
        assertEquals(listOf(1, 2, 3), firstThree.map { it.movementNumber })

        // Goldberg Variations movements must also be adjacent and ordered 1 -> 2
        val goldbergPositions =
            order.toList().mapIndexedNotNull { pos, idx ->
                if (songs[idx].workName == "Goldberg Variations") pos else null
            }
        assertEquals(2, goldbergPositions.size)
        assertEquals(1, goldbergPositions[1] - goldbergPositions[0])
        assertTrue(songs[order[goldbergPositions[0]]].movementNumber == 1)
        assertTrue(songs[order[goldbergPositions[1]]].movementNumber == 2)
    }

    @Test
    fun workShuffle_isolatesConductorsOnSameAlbum_andOrdersByDiscBeforeMovement() {
        val box = FakeAlbum("Beethoven Box")
        val k1 = FakeSong(box, "Sym 9", "I", movementNumber = 1, track = 1, discNum = 1, conductorsList = listOf("Karajan"))
        val k2 = FakeSong(box, "Sym 9", "Act II", movementNumber = 1, track = 1, discNum = 2, conductorsList = listOf("Karajan"))
        val b1 = FakeSong(box, "Sym 9", "I", movementNumber = 1, track = 3, discNum = 1, conductorsList = listOf("Bernstein"))

        val songs = listOf(k2, b1, k1)
        val order = WorkShuffleOrder.createWorkAwareOrder(songs, startIndex = 0, random = Random(7))
        assertEquals(3, order.size)
        // Anchor is Karajan block (index 0 & 2), ordered Disc 1 -> Disc 2 even though both have movementNumber=1
        assertEquals(k1.uid, songs[order[0]].uid)
        assertEquals(k2.uid, songs[order[1]].uid)
        assertEquals(b1.uid, songs[order[2]].uid)
    }

    private class FakeAlbum(title: String) : Album {
        override val uid: Music.UID =
            requireNotNull(Music.UID.fromString("uaa${UUID.nameUUIDFromBytes(title.toByteArray())}"))
        override val name: Name = Naming.simple().name(title, null)
        override val songs: Collection<Song> = emptyList()
        override val dates: Date.Range? = null
        override val releaseType: ReleaseType = ReleaseType.Album(null)
        override val covers: CoverCollection = CoverCollection.from(emptyList())
        override val durationMs: Long = 0L
        override val addedMs: Long = 0L
        override val artists: List<Artist> = emptyList()
    }

    private class FakeSong(
        override val album: Album,
        override val workName: String?,
        override val movementName: String?,
        override val movementNumber: Int?,
        override val track: Int?,
        discNum: Int? = null,
        private val conductorsList: List<String> = emptyList(),
    ) : Song {
        override val uid: Music.UID =
            requireNotNull(
                Music.UID.fromString(
                    "uas${UUID.nameUUIDFromBytes("${album.uid}:$workName:$movementName:$track:$discNum:${conductorsList.joinToString()}".toByteArray())}"
                )
            )
        override val name: Name.Known =
            Naming.simple().name(movementName ?: workName ?: "Track", null)
        override val disc: Disc? = discNum?.let { Disc(it, null) }
        override val date: Date? = null
        override val uri: Uri get() = error("unused")
        override val path: Path get() = error("unused")
        override val format: Format get() = error("unused")
        override val size: Long = 0L
        override val durationMs: Long = 60_000L
        override val bitrateKbps: Int = 1000
        override val sampleRateHz: Int = 44100
        override val replayGainAdjustment = ReplayGainAdjustment(null, null)
        override val modifiedMs: Long = 0L
        override val addedMs: Long = 0L
        override val cover: Cover? = null
        override val artists: List<Artist> = emptyList()
        override val genres: List<Genre> = emptyList()
        override val composers: List<String> = emptyList()
        override val conductors: List<String> = conductorsList
    }
}
