/*
 * Copyright (c) 2026 Tonewise Project
 * ClassicalHierarchyAndQueueTest.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.home.classical

import android.net.Uri
import com.shawnshi.tonewise.home.collection.CollectionPivot
import com.shawnshi.tonewise.music.ClassicalWork
import com.shawnshi.tonewise.music.WorkRecording
import com.shawnshi.tonewise.music.buildClassicalWorkIndex
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

    @Test
    fun buildClassicalWorkIndex_mergesSameWorkAcrossDistinctMovementMbids() {
        val albRostropovich = FakeAlbum("Rostropovich!")
        val albAlternate = FakeAlbum("Vivaldi Cello Concertos (2004)")
        val workTitle = "Cello Concerto in G major, RV 413"

        val m1 =
            FakeSong(
                idSeed = "rv413_1",
                album = albRostropovich,
                title = "Konzert für Violoncello und Streicher G-dur, RV 413: 1. Allegro",
                workName = workTitle,
                movementName = null,
                movementNumber = null,
                durationMs = 210_000L,
                composers = listOf("Antonio Vivaldi"),
                conductors = listOf("Paul Sacher"),
                track = 10,
                disc = Disc(3, null),
                musicBrainzWorkId = "85ba7816-8093-3771-92bb-7138ca2c370c",
            )
        val m2 =
            FakeSong(
                idSeed = "rv413_2",
                album = albRostropovich,
                title = "Konzert für Violoncello und Streicher G-dur, RV 413: 2. Adagio",
                workName = workTitle,
                movementName = null,
                movementNumber = null,
                durationMs = 195_000L,
                composers = listOf("Antonio Vivaldi"),
                conductors = listOf("Paul Sacher"),
                track = 11,
                disc = Disc(3, null),
                musicBrainzWorkId = "86f556d9-7448-36a9-9764-334bb96e8990",
            )
        val m3 =
            FakeSong(
                idSeed = "rv413_3",
                album = albRostropovich,
                title = "Konzert für Violoncello und Streicher G-dur, RV 413: 3. Allegro",
                workName = workTitle,
                movementName = null,
                movementNumber = null,
                durationMs = 180_000L,
                composers = listOf("Antonio Vivaldi"),
                conductors = listOf("Paul Sacher"),
                track = 12,
                disc = Disc(3, null),
                musicBrainzWorkId = "a0b92596-1ac3-397f-8582-c8268646f49a",
            )
        val altRec =
            FakeSong(
                idSeed = "rv413_alt",
                album = albAlternate,
                title = "Cello Concerto in G major, RV 413: I. Allegro",
                workName = workTitle,
                movementName = null,
                movementNumber = 1,
                durationMs = 200_000L,
                composers = listOf("Antonio Vivaldi"),
                conductors = listOf("Andrea Marcon"),
                track = 1,
                disc = Disc(1, null),
                musicBrainzWorkId = null,
            )

        val (byUid, works) = buildClassicalWorkIndex(listOf(m3, m1, altRec, m2))
        assertEquals(1, works.size)
        val rv413 = works.single()
        assertEquals("antonio vivaldi::cello concerto in g major, rv 413", rv413.key)
        assertEquals("Antonio Vivaldi", rv413.composerName)
        assertEquals(2, rv413.recordings.size)
        val rostropovichRec = rv413.recordings.first { it.album == albRostropovich }
        assertEquals(listOf(10, 11, 12), rostropovichRec.movements.map { it.track })
        assertEquals(rv413, byUid[m1.uid])
        assertEquals(rv413, byUid[m2.uid])
        assertEquals(rv413, byUid[m3.uid])
        assertEquals(rv413, byUid[altRec.uid])
    }

    @Test
    fun buildClassicalWorkIndex_and_groupByComposer_mergeComposerCaseAndAliasVariants() {
        val alb1 = FakeAlbum("Vivaldi Concertos A")
        val alb2 = FakeAlbum("Vivaldi Concertos B")
        val s1 =
            FakeSong(
                idSeed = "viv_alias_1",
                album = alb1,
                title = "Cello Concerto in G major, RV 413: I. Allegro",
                workName = "Cello Concerto in G major, RV 413",
                movementName = "Allegro",
                movementNumber = 1,
                durationMs = 200_000L,
                composers = listOf("Vivaldi"),
            )
        val s2 =
            FakeSong(
                idSeed = "viv_alias_2",
                album = alb2,
                title = "Cello Concerto in G major, RV 413: II. Adagio",
                workName = "Cello Concerto in G major, RV 413",
                movementName = "Adagio",
                movementNumber = 2,
                durationMs = 190_000L,
                composers = listOf("ANTONIO VIVALDI"),
            )
        val s3 =
            FakeSong(
                idSeed = "viv_alias_3",
                album = alb2,
                title = "Cello Concerto in G major, RV 413: III. Allegro",
                workName = "Cello Concerto in G major, RV 413",
                movementName = "Allegro",
                movementNumber = 3,
                durationMs = 185_000L,
                composers = listOf("Antonio Vivaldi"),
            )

        val (_, works) = buildClassicalWorkIndex(listOf(s1, s2, s3))
        assertEquals(1, works.size)
        assertEquals("Antonio Vivaldi", works.single().composerName)

        val groups =
            ClassicalHierarchyLogic.groupByComposer(
                listOf(
                    ClassicalWork("k1", "Work A", "vivaldi", emptyList()),
                    ClassicalWork("k2", "Work B", "ANTONIO VIVALDI", emptyList()),
                    ClassicalWork("k3", "Work C", "Antonio Vivaldi", emptyList()),
                )
            )
        assertEquals(1, groups.size)
        assertEquals("Antonio Vivaldi", groups.single().composerName)
        assertEquals(3, groups.single().workCount)
    }

    @Test
    fun buildClassicalWorkIndex_prefersTrackArtistOverAlbumArtistWhenComposerMissing() {
        val variousAlbum = FakeAlbum("DG Baroque Compilation", artists = listOf(FakeArtist("Various Artists")))
        val s =
            FakeSong(
                idSeed = "missing_comp_1",
                album = variousAlbum,
                title = "Cello Concerto in G major, RV 413: I. Allegro",
                workName = "Cello Concerto in G major, RV 413",
                movementName = "Allegro",
                movementNumber = 1,
                durationMs = 200_000L,
                composers = emptyList(),
                artists = listOf(FakeArtist("Antonio Vivaldi")),
            )

        val (_, works) = buildClassicalWorkIndex(listOf(s))
        assertEquals(1, works.size)
        assertEquals("Antonio Vivaldi", works.single().composerName)
        assertEquals("antonio vivaldi::cello concerto in g major, rv 413", works.single().key)
    }

    @Test
    fun buildClassicalWorkIndex_stripsMovementSuffixFromDirtyWorkTags() {
        val alb = FakeAlbum("Rostropovich!")
        val m1 =
            FakeSong(
                idSeed = "dirty_work_1",
                album = alb,
                title = "Konzert für Violoncello und Streicher G-dur, RV 413: 1. Allegro",
                workName = "Concerto for Cello in G major, RV 413: I. Allegro",
                movementName = null,
                movementNumber = null,
                durationMs = 210_000L,
                composers = listOf("Antonio Vivaldi"),
                track = 10,
            )
        val m2 =
            FakeSong(
                idSeed = "dirty_work_2",
                album = alb,
                title = "Konzert für Violoncello und Streicher G-dur, RV 413: 2. Adagio",
                workName = "Concerto for Cello in G major, RV 413: II. Largo",
                movementName = null,
                movementNumber = null,
                durationMs = 195_000L,
                composers = listOf("Antonio Vivaldi"),
                track = 11,
            )
        val m3 =
            FakeSong(
                idSeed = "dirty_work_3",
                album = alb,
                title = "Konzert für Violoncello und Streicher G-dur, RV 413: 3. Allegro",
                workName = "Concerto for Cello in G major, RV 413: III. Allegro",
                movementName = null,
                movementNumber = null,
                durationMs = 180_000L,
                composers = listOf("Antonio Vivaldi"),
                track = 12,
            )

        val (_, works) = buildClassicalWorkIndex(listOf(m3, m1, m2))
        assertEquals(1, works.size)
        val work = works.single()
        assertEquals("Concerto for Cello in G major, RV 413", work.workTitle)
        assertEquals(1, work.recordings.size)
        assertEquals(listOf(10, 11, 12), work.recordings.single().movements.map { it.track })
    }

    private class FakeArtist(name: String) : Artist {
        override val uid: Music.UID =
            requireNotNull(Music.UID.fromString("uar${UUID.nameUUIDFromBytes(name.toByteArray())}"))
        override val name: Name = Naming.simple().name(name, null)
        override val songs: Collection<Song> = emptyList()
        override val explicitAlbums: Collection<Album> = emptyList()
        override val implicitAlbums: Collection<Album> = emptyList()
        override val durationMs: Long? = null
        override val covers: CoverCollection = CoverCollection.from(emptyList())
        override val genres: List<Genre> = emptyList()
    }

    private class FakeAlbum(
        title: String,
        override val artists: List<Artist> = emptyList(),
    ) : Album {
        override val uid: Music.UID =
            requireNotNull(Music.UID.fromString("uaa${UUID.nameUUIDFromBytes(title.toByteArray())}"))
        override val name: Name = Naming.simple().name(title, null)
        override val songs: Collection<Song> = emptyList()
        override val dates: Date.Range? = null
        override val releaseType: ReleaseType = ReleaseType.Album(null)
        override val covers: CoverCollection = CoverCollection.from(emptyList())
        override val durationMs: Long = 0L
        override val addedMs: Long = 0L
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
        override val track: Int? = movementNumber,
        override val disc: Disc? = null,
        override val artists: List<Artist> = emptyList(),
        override val musicBrainzWorkId: String? = null,
    ) : Song {
        override val uid: Music.UID =
            requireNotNull(Music.UID.fromString("uas${UUID.nameUUIDFromBytes(idSeed.toByteArray())}"))
        override val name: Name.Known = Naming.simple().name(title, null)
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
        override val genres: List<Genre> = emptyList()
    }
}
