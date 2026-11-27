/*
 * Copyright (c) 2026 Tonewise Project
 * ClassicalHierarchyAndQueueTest.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.home.classical

import android.net.Uri
import com.shawnshi.tonewise.home.collection.CollectionPivot
import com.shawnshi.tonewise.music.ClassicalWork
import com.shawnshi.tonewise.music.WorkRecording
import com.shawnshi.tonewise.playback.state.ClassicalSessionShuffleLock
import java.util.UUID
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
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

class ClassicalHierarchyAndQueueTest {
    @Test
    fun groupByComposer_populatesEraStatsLowercaseAndFormFiltersAccurately() {
        val albKarajan = FakeAlbum("Beethoven: Symphony No. 9 (Karajan)")
        val albFurtwangler = FakeAlbum("Beethoven: Symphony No. 9 (Furtwangler)")
        val albBach = FakeAlbum("Goldberg Variations")
        val symMvt1 = FakeSong("k1", albKarajan, "Symphony No. 9 in D minor, Op. 125: I. Allegro ma non troppo", null, null, null, 900_000L, composers = listOf("Ludwig van Beethoven"), conductors = listOf("Herbert von Karajan"), year = 1963)
        val symRec1 = WorkRecording(albKarajan, "Herbert von Karajan", "Berlin Philharmonic", 1963, listOf(symMvt1), "k")
        val symRec2 = WorkRecording(albFurtwangler, "Wilhelm Furtwangler", "Bayreuth Festival Orchestra", 1951, listOf(symMvt1), "f")
        val bachMvt1 = FakeSong("b1", albBach, "Aria", "Goldberg Variations, BWV 988", "Aria", 1, 180_000L, composers = listOf("Johann Sebastian Bach"), year = 1981)
        val bachRec = WorkRecording(albBach, "", "Glenn Gould", 1981, listOf(bachMvt1), "g")

        val works =
            listOf(
                ClassicalWork(
                    key = "beethoven::sym9",
                    workTitle = "Symphony No. 9 in D minor, Op. 125",
                    composerName = "Ludwig van Beethoven",
                    recordings = listOf(symRec1, symRec2),
                    form = "symphonies",
                    era = "romantic",
                ),
                ClassicalWork(
                    key = "bach::bwv988",
                    workTitle = "Goldberg Variations, BWV 988",
                    composerName = "Johann Sebastian Bach",
                    recordings = listOf(bachRec),
                    form = "solo",
                    era = "baroque",
                ),
            )

        val groups = ClassicalHierarchyLogic.groupByComposer(works)
        assertEquals(listOf("johann sebastian bach", "ludwig van beethoven"), groups.map { it.displayLowercase })
        assertEquals("baroque · 1 works · 1 recordings", ClassicalHierarchyLogic.formatComposerStatsLine(groups[0]))
        assertEquals("romantic · 1 works · 2 recordings", ClassicalHierarchyLogic.formatComposerStatsLine(groups[1]))
        assertNull(groups[0].localPortraitPath)

        val romanticOnly = ClassicalHierarchyLogic.filterComposersByEra(groups, "romantic")
        assertEquals(listOf("Ludwig van Beethoven"), romanticOnly.map { it.composerName })

        val symOnly = ClassicalHierarchyLogic.filterWorksByForm(works, "symphonies")
        assertEquals(listOf("beethoven::sym9"), symOnly.map { it.key })

        assertEquals("herbert von karajan · 1963", ClassicalHierarchyLogic.formatRecordingBadge(symRec1))
        val (roman, cleanTitle) =
            ClassicalHierarchyLogic.formatMovementDisplay(
                rawTitle = "Symphony No. 9 in D minor, Op. 125: II. Molto vivace",
                explicitWork = null,
                explicitMovement = null,
                explicitMovementNumber = null,
                index = 0,
            )
        assertEquals("II.", roman)
        assertEquals("Molto vivace", cleanTitle)
    }

    @Test
    fun classicalSessionShuffleLock_suppressesShuffleForClassicalAndRestoresImplicitGlobalShuffle() {
        val lock = ClassicalSessionShuffleLock()
        assertFalse(lock.isLocked)

        // User starts with global shuffle ON -> plays classical work -> shuffle locked to false
        val firstPlay =
            lock.resolveForNewPlayback(
                currentShuffleEnabled = true,
                commandShuffled = false,
                suppressGlobalShuffle = true,
                restoreGlobalShuffleOnUnlock = false,
            )
        assertFalse(firstPlay)
        assertTrue(lock.isLocked)

        // Playing another classical movement while locked must keep the original saved ON state
        val secondMovement =
            lock.resolveForNewPlayback(
                currentShuffleEnabled = false,
                commandShuffled = false,
                suppressGlobalShuffle = true,
                restoreGlobalShuffleOnUnlock = false,
            )
        assertFalse(secondMovement)
        assertTrue(lock.isLocked)

        // Switching to a non-classical playlist with ShuffleMode.IMPLICIT restores global shuffle ON
        val nonClassical =
            lock.resolveForNewPlayback(
                currentShuffleEnabled = false,
                commandShuffled = false,
                suppressGlobalShuffle = false,
                restoreGlobalShuffleOnUnlock = true,
            )
        assertTrue(nonClassical)
        assertFalse(lock.isLocked)
    }

    @Test
    fun collectionPivot_omitsStandaloneComposersFromDefaultPivots() {
        assertFalse(CollectionPivot.DEFAULT_PIVOTS.contains(CollectionPivot.COMPOSERS))
        assertTrue(CollectionPivot.DEFAULT_PIVOTS.contains(CollectionPivot.CLASSICAL))
        assertTrue(CollectionPivot.DEFAULT_PIVOTS.contains(CollectionPivot.CONDUCTORS))
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
        idSeed: String,
        override val album: Album,
        title: String,
        override val workName: String?,
        override val movementName: String?,
        override val movementNumber: Int?,
        override val durationMs: Long,
        override val composers: List<String> = emptyList(),
        override val conductors: List<String> = emptyList(),
        year: Int? = null,
    ) : Song {
        override val uid: Music.UID =
            requireNotNull(Music.UID.fromString("uas${UUID.nameUUIDFromBytes(idSeed.toByteArray())}"))
        override val name: Name.Known = Naming.simple().name(title, null)
        override val track: Int? = movementNumber
        override val disc: Disc? = null
        override val date: Date? = year?.let(Date::from)
        override val uri: Uri get() = error("unused")
        override val path: Path get() = error("unused")
        override val format: Format get() = error("unused")
        override val size: Long = 0L
        override val bitrateKbps: Int = 1000
        override val sampleRateHz: Int = 44100
        override val replayGainAdjustment = ReplayGainAdjustment(null, null)
        override val modifiedMs: Long = 0L
        override val addedMs: Long = 0L
        override val cover: Cover? = null
        override val artists: List<Artist> = emptyList()
        override val genres: List<Genre> = emptyList()
    }
}
