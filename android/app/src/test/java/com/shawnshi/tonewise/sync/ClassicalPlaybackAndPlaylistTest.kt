/*
 * Copyright (c) 2026 Tonewise Project
 * ClassicalPlaybackAndPlaylistTest.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.sync

import android.net.Uri
import com.shawnshi.tonewise.detail.WorkRecordingsDialog
import com.shawnshi.tonewise.music.ClassicalWorkKey
import com.shawnshi.tonewise.playback.replaygain.ReplayGainAudioProcessor
import com.shawnshi.tonewise.playback.service.BetterShuffleOrder
import com.shawnshi.tonewise.playback.sleep.SleepTimerController
import com.shawnshi.tonewise.playback.sleep.SleepTimerMode
import java.io.File
import java.util.UUID
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Assume.assumeTrue
import org.junit.Before
import org.junit.Test
import org.oxycblt.musikr.Album
import org.oxycblt.musikr.Artist
import org.oxycblt.musikr.Genre
import org.oxycblt.musikr.Music
import org.oxycblt.musikr.Song
import org.oxycblt.musikr.core.TonewiseCore
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

class ClassicalPlaybackAndPlaylistTest {
    @Before
    fun loadNativeLibrary() {
        var dir: File? = File(System.getProperty("user.dir") ?: ".").canonicalFile
        var nativeLib: File? = null
        while (dir != null) {
            val dll = File(dir, "core/target/release/tonewise_core.dll")
            val so = File(dir, "core/target/release/libtonewise_core.so")
            nativeLib =
                when {
                    dll.isFile -> dll
                    so.isFile -> so
                    else -> null
                }
            if (nativeLib != null) break
            dir = dir.parentFile
        }
        assumeTrue("Desktop tonewise_core native library must be built", nativeLib != null)
        TonewiseCore.loadNativeFromPath(nativeLib!!.absolutePath)
    }

    @Test
    fun sceneRuleEvaluator_emitsClassicalMetadata_andKeepsWorksAtomicAcrossArcs() {
        val symAlbum = FakeAlbum("Beethoven 5")
        val soloAlbum = FakeAlbum("Romantic Piano")

        val symM2 =
            FakeSong(
                idSeed = "sym_m2",
                album = symAlbum,
                title = "II. Andante con moto",
                workName = "Symphony No. 5",
                movementName = "II. Andante con moto",
                movementNumber = 2,
                composers = listOf("Ludwig van Beethoven"),
                conductors = listOf("Herbert von Karajan"),
                durationMs = 600_000L,
            )
        val soloMid =
            FakeSong(
                idSeed = "chopin_mid",
                album = soloAlbum,
                title = "Ballade No. 1",
                workName = null,
                movementName = null,
                movementNumber = null,
                composers = listOf("Frederic Chopin"),
                conductors = emptyList(),
                durationMs = 540_000L,
            )
        val symM1 =
            FakeSong(
                idSeed = "sym_m1",
                album = symAlbum,
                title = "I. Allegro con brio",
                workName = "Symphony No. 5",
                movementName = "I. Allegro con brio",
                movementNumber = 1,
                composers = listOf("Ludwig van Beethoven"),
                conductors = listOf("Herbert von Karajan"),
                durationMs = 420_000L,
            )

        val songs = listOf(symM2, soloMid, symM1)
        val tracksJson =
            SceneRuleEvaluator.buildTracksJson(
                songs = songs,
                nowMs = 1_763_300_000_000L,
                getDjScenes = { emptyList() },
                getDjVocals = { "instrumental" },
                getDjEnergy = { s ->
                    when (s.uid) {
                        symM1.uid -> "high"
                        symM2.uid -> "low"
                        else -> "medium"
                    }
                },
                getDjTempo = { null },
                getMoods = { emptyList() },
                getPlayStats = { s ->
                    val unplayed = if (s.uid == symM1.uid) 0 else 1
                    Triple(unplayed, if (unplayed > 0) 1_763_200_000_000L else 0L, false)
                },
                getRatingOverride = { null },
            )

        @Suppress("UNCHECKED_CAST")
        val serialized = TonewiseCore.parseJson(tracksJson) as List<Map<String, Any?>>
        assertEquals(3, serialized.size)
        assertEquals("${symAlbum.uid}|herbert von karajan::symphony no. 5", serialized[0]["work_key"])
        assertEquals(2, (serialized[0]["movement_order"] as Number).toInt())
        assertEquals(listOf("Ludwig van Beethoven"), serialized[0]["composers"])
        assertEquals(listOf("Herbert von Karajan"), serialized[0]["conductors"])
        assertEquals("Symphony No. 5", serialized[0]["work_name"])

        val energyAscScene =
            TonewiseCore.stringifyJson(
                mapOf(
                    "id" to "asc_test",
                    "title" to "Energy Asc",
                    "updated_at_ms" to 0,
                    "sort_arc" to "energy_asc",
                    "tempo_null_passthrough" to true,
                    "rule" to
                        mapOf(
                            "predicate" to
                                mapOf(
                                    "field" to "composer",
                                    "op" to "in",
                                    "values" to listOf("Beethoven", "Chopin"),
                                )
                        ),
                )
            )
        val orderedUids = TonewiseCore.evaluateScene(energyAscScene, tracksJson)
        assertEquals(3, orderedUids.size)
        val m1Pos = orderedUids.indexOf(symM1.uid.toString())
        assertTrue(m1Pos >= 0)
        assertEquals(symM2.uid.toString(), orderedUids[m1Pos + 1])

        // Filter by conductor keeps only Karajan movements in movement order 1 -> 2
        val conductorScene =
            TonewiseCore.stringifyJson(
                mapOf(
                    "id" to "cond_test",
                    "title" to "Conductor Filter",
                    "updated_at_ms" to 0,
                    "sort_arc" to "rediscovery_90d",
                    "tempo_null_passthrough" to true,
                    "rule" to
                        mapOf(
                            "predicate" to
                                mapOf(
                                    "field" to "conductor",
                                    "op" to "in",
                                    "values" to listOf("Karajan"),
                                )
                        ),
                )
            )
        assertEquals(
            listOf(symM1.uid.toString(), symM2.uid.toString()),
            TonewiseCore.evaluateScene(conductorScene, tracksJson),
        )
    }

    @Test
    fun sceneRuleEvaluator_extractSortArc_parsesWorkShuffleWithoutAndroidOrgJson() {
        val raw = """{"id":"s1","sort_arc":"work_shuffle"}"""
        assertEquals("work_shuffle", SceneRuleEvaluator.extractSortArc(raw))
        assertEquals("flat", SceneRuleEvaluator.extractSortArc("""{"id":"s2"}"""))
    }

    @Test
    fun replayGainDynamic_usesAlbumGainForContiguousWorksEvenUnderShuffleOrStandaloneWorkDialog() {
        val symAlbum = FakeAlbum("Beethoven 5 (Karajan)")
        val popAlbum = FakeAlbum("Pop Hits")
        val m1 = FakeSong("m1", symAlbum, "Mvt 1", "Symphony No. 5", "Mvt 1", 1, durationMs = 400_000L)
        val m2 = FakeSong("m2", symAlbum, "Mvt 2", "Symphony No. 5", "Mvt 2", 2, durationMs = 500_000L)
        val pop1 = FakeSong("p1", popAlbum, "Pop 1", null, null, null, durationMs = 180_000L)
        val pop2 = FakeSong("p2", popAlbum, "Pop 2", null, null, null, durationMs = 190_000L)

        val workShuffleQueue = listOf(pop1, m1, m2, pop2)
        // Playing movement inside WorkShuffle (isShuffled = true) -> Album Gain
        assertTrue(
            ReplayGainAudioProcessor.shouldUseAlbumGainDynamic(
                queue = workShuffleQueue,
                currentIndex = 1,
                currentSong = m1,
                isSingleAlbumParent = false,
                isShuffled = true,
            )
        )
        // Playing pop song inside shuffled queue -> Track Gain
        assertFalse(
            ReplayGainAudioProcessor.shouldUseAlbumGainDynamic(
                queue = workShuffleQueue,
                currentIndex = 0,
                currentSong = pop1,
                isSingleAlbumParent = false,
                isShuffled = true,
            )
        )
        // Standalone work launched from WorkRecordingsDialog (parent = null) -> Album Gain
        assertTrue(
            ReplayGainAudioProcessor.shouldUseAlbumGainDynamic(
                queue = listOf(m1, m2),
                currentIndex = 0,
                currentSong = m1,
                isSingleAlbumParent = false,
                isShuffled = false,
            )
        )
        // Shuffled single pop album -> Track Gain
        assertFalse(
            ReplayGainAudioProcessor.shouldUseAlbumGainDynamic(
                queue = listOf(pop1, pop2),
                currentIndex = 0,
                currentSong = pop1,
                isSingleAlbumParent = true,
                isShuffled = true,
            )
        )
    }

    @Test
    fun workRecordingsDialog_preservesProportionalSeekAndOuterQueueOnAbSwitch() {
        assertEquals(
            150_000L,
            WorkRecordingsDialog.computeProportionalSeekMs(
                currentPosMs = 100_000L,
                currentDurationMs = 400_000L,
                targetDurationMs = 600_000L,
            ),
        )
        assertEquals(
            0L,
            WorkRecordingsDialog.computeProportionalSeekMs(
                currentPosMs = 50_000L,
                currentDurationMs = 0L,
                targetDurationMs = 600_000L,
            ),
        )

        val popAlb = FakeAlbum("Pop Mix")
        val karAlb = FakeAlbum("Karajan 1963")
        val bernAlb = FakeAlbum("Bernstein 1980")
        val p0 = FakeSong("p0", popAlb, "Intro", null, null, null, durationMs = 180_000L)
        val k1 = FakeSong("k1", karAlb, "I", "Sym 5", "I", 1, durationMs = 400_000L)
        val k2 = FakeSong("k2", karAlb, "II", "Sym 5", "II", 2, durationMs = 500_000L)
        val p1 = FakeSong("p1", popAlb, "Outro", null, null, null, durationMs = 180_000L)
        val b1 = FakeSong("b1", bernAlb, "I", "Sym 5", "I", 1, durationMs = 440_000L)
        val b2 = FakeSong("b2", bernAlb, "II", "Sym 5", "II", 2, durationMs = 520_000L)

        val (updatedQueue, newIdx) =
            WorkRecordingsDialog.replaceWorkBlockInQueue(
                currentQueue = listOf(p0, k1, k2, p1),
                currentIndex = 2,
                targetMovements = listOf(b1, b2),
                targetOffsetInMovements = 1,
            )
        assertEquals(listOf(p0.uid, b1.uid, b2.uid, p1.uid), updatedQueue.map { it.uid })
        assertEquals(2, newIdx)
    }

    @Test
    fun classicalWorkKey_isolatesConductorsOnCompilationAlbum_andSleepTimerHandlesWrapAround() {
        val compAlb = FakeAlbum("Great Symphonies Box")
        val kar1 = FakeSong("kar1", compAlb, "I", "Symphony No. 5", "I", 1, 400_000L, conductors = listOf("Herbert von Karajan"))
        val bern1 = FakeSong("bern1", compAlb, "I", "Symphony No. 5", "I", 1, 420_000L, conductors = listOf("Leonard Bernstein"))
        assertTrue(ClassicalWorkKey.recordingScope(kar1) != ClassicalWorkKey.recordingScope(bern1))
        assertTrue(ClassicalWorkKey.workBlockKey(kar1) != ClassicalWorkKey.workBlockKey(bern1))

        val timer = SleepTimerController()
        timer.setEndOfWork(kar1)
        assertFalse(timer.willStopAtTransition(kar1, kar1, isQueueWrapAround = false))
        assertTrue(timer.willStopAtTransition(kar1, kar1, isQueueWrapAround = true))
        assertTrue(timer.willStopAtTransition(kar1, bern1, isQueueWrapAround = false))
        assertTrue(timer.onAutoTrackTransition(kar1, kar1, isQueueWrapAround = true))
        assertEquals(SleepTimerMode.OFF, timer.state.value.mode)

        timer.setEndOfWork(kar1)
        assertTrue(timer.consumeEndOfItemStop())
        assertEquals(SleepTimerMode.OFF, timer.state.value.mode)
    }

    @Test
    fun betterShuffleOrder_insertsNextAfterWorkBlockContiguously() {
        // Shuffled play sequence is [2, 3, 0, 1] where (2, 3) is one work and (0, 1) are remaining tracks.
        // Inserting 2 tracks at anchor index 0 ("next after work") must produce [4, 5, 0, 1, 2, 3].
        val order = BetterShuffleOrder(intArrayOf(2, 3, 0, 1))
        val updated = order.cloneAndInsert(insertionIndex = 0, insertionCount = 2)
        val seq = ArrayList<Int>(updated.length)
        var cursor = updated.firstIndex
        while (cursor != -1) {
            seq.add(cursor)
            cursor = updated.getNextIndex(cursor)
        }
        assertEquals(listOf(4, 5, 0, 1, 2, 3), seq)
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
    ) : Song {
        override val uid: Music.UID =
            requireNotNull(Music.UID.fromString("uas${UUID.nameUUIDFromBytes(idSeed.toByteArray())}"))
        override val name: Name.Known = Naming.simple().name(title, null)
        override val track: Int? = movementNumber
        override val disc: Disc? = null
        override val date: Date? = null
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
