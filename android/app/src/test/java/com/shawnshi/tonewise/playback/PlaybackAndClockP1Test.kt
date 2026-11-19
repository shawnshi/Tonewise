/*
 * Copyright (c) 2026 Tonewise Project
 * PlaybackAndClockP1Test.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.playback

import android.net.Uri
import com.shawnshi.tonewise.playback.dsp.BiquadBand
import com.shawnshi.tonewise.playback.dsp.BiquadType
import com.shawnshi.tonewise.playback.dsp.DspChainConfig
import com.shawnshi.tonewise.playback.dsp.DspPresetId
import com.shawnshi.tonewise.playback.dsp.TonewiseDspEngine
import com.shawnshi.tonewise.playback.service.ExoPlaybackStateHolder
import com.shawnshi.tonewise.playback.service.WorkShuffleOrder
import java.util.UUID
import kotlin.math.PI
import kotlin.math.abs
import kotlin.math.pow
import kotlin.math.sin
import kotlin.random.Random
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
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

class PlaybackAndClockP1Test {
    @Test
    fun playQualification_enforcesLastFm50PercentOrFourMinutesThreshold() {
        // 3-minute track requires >= 90s (50%), NOT 30s
        assertFalse(ExoPlaybackStateHolder.isPlayQualified(durationMs = 180_000L, accumulatedPlayMs = 30_000L))
        assertFalse(ExoPlaybackStateHolder.isPlayQualified(durationMs = 180_000L, accumulatedPlayMs = 89_999L))
        assertTrue(ExoPlaybackStateHolder.isPlayQualified(durationMs = 180_000L, accumulatedPlayMs = 90_000L))

        // 10-minute track caps at 240s (4 minutes)
        assertFalse(ExoPlaybackStateHolder.isPlayQualified(durationMs = 600_000L, accumulatedPlayMs = 239_999L))
        assertTrue(ExoPlaybackStateHolder.isPlayQualified(durationMs = 600_000L, accumulatedPlayMs = 240_000L))

        // Short 20-second track requires 50% (10s)
        assertFalse(ExoPlaybackStateHolder.isPlayQualified(durationMs = 20_000L, accumulatedPlayMs = 9_999L))
        assertTrue(ExoPlaybackStateHolder.isPlayQualified(durationMs = 20_000L, accumulatedPlayMs = 10_000L))
    }

    @Test
    fun dspEngine_hotBandUpdatePreservesIirStateAndLimiterBoundsPeaks() {
        val engineWarm = TonewiseDspEngine()
        val engineBase = TonewiseDspEngine()
        val engineCold = TonewiseDspEngine()
        engineWarm.configure(48000, 2)
        engineBase.configure(48000, 2)
        engineCold.configure(48000, 2)

        val baseConfig =
            DspChainConfig(
                presetId = DspPresetId.CUSTOM,
                enabled = true,
                preampDb = -6.0,
                bands = listOf(BiquadBand(BiquadType.PEAKING, 1000.0, 3.0, 1.41, true)),
                limiterEnabled = false,
            )
        val updatedConfig =
            baseConfig.copy(
                bands = listOf(BiquadBand(BiquadType.PEAKING, 1000.0, 3.2, 1.41, true))
            )

        engineWarm.updateConfig(baseConfig)
        engineBase.updateConfig(baseConfig)
        engineCold.updateConfig(updatedConfig)

        val warmupFrames = 128
        val warmBuf = DoubleArray(warmupFrames * 2)
        val baseBuf = DoubleArray(warmupFrames * 2)
        for (f in 0 until warmupFrames) {
            val x = 0.5 * sin(2.0 * PI * 1000.0 * f / 48000.0)
            warmBuf[f * 2] = x
            warmBuf[f * 2 + 1] = x
            baseBuf[f * 2] = x
            baseBuf[f * 2 + 1] = x
        }
        engineWarm.processInterleaved(warmBuf, warmupFrames, 0.0)
        engineBase.processInterleaved(baseBuf, warmupFrames, 0.0)

        engineWarm.updateConfig(updatedConfig)
        val xNext = 0.5 * sin(2.0 * PI * 1000.0 * warmupFrames / 48000.0)
        val warmNext = doubleArrayOf(xNext, xNext)
        val baseNext = doubleArrayOf(xNext, xNext)
        val coldNext = doubleArrayOf(xNext, xNext)

        engineWarm.processInterleaved(warmNext, 1, 0.0)
        engineBase.processInterleaved(baseNext, 1, 0.0)
        engineCold.processInterleaved(coldNext, 1, 0.0)

        val warmDelta = abs(warmNext[0] - baseNext[0])
        val coldDelta = abs(coldNext[0] - baseNext[0])
        assertTrue("Expected smooth continuity on hot update, warmDelta=$warmDelta", warmDelta < 0.015)
        assertTrue(
            "Expected warmDelta ($warmDelta) << coldDelta ($coldDelta)",
            warmDelta < coldDelta * 0.25,
        )

        val limEngine = TonewiseDspEngine()
        limEngine.configure(48000, 2)
        limEngine.updateConfig(
            DspChainConfig(
                presetId = DspPresetId.CUSTOM,
                enabled = true,
                preampDb = 0.0,
                bands = emptyList(),
                limiterEnabled = true,
                limiterCeilingDb = -0.2,
            )
        )
        val frames = 256 + 64 + 256
        val samples = DoubleArray(frames * 2)
        for (f in 0 until frames) {
            val v = if (f in 256 until 320) 2.5 else 0.1
            samples[f * 2] = v
            samples[f * 2 + 1] = v
        }
        limEngine.processInterleaved(samples, frames, replayGainDb = 6.0)
        val ceiling = 10.0.pow(-0.2 / 20.0)
        for (s in samples) {
            assertTrue("Expected finite sample, got $s", s.isFinite())
            assertTrue("Sample exceeded ceiling: |${s}| > $ceiling", abs(s) <= ceiling + 1e-3)
        }

        val pending = limEngine.pendingLimiterFrames()
        assertTrue("Expected buffered lookahead limiter frames > 0", pending > 0)
        val tail = DoubleArray(pending * 2)
        val drained = limEngine.drainLimiterTail(tail)
        assertEquals(pending, drained)
        assertEquals(0, limEngine.pendingLimiterFrames())
        for (s in tail) {
            assertTrue("Drained sample exceeded ceiling: |${s}| > $ceiling", abs(s) <= ceiling + 1e-3)
        }
    }

    @Test
    fun workShuffleOrder_separatesDifferentConductorsOfSameWorkOnCompilation() {
        val compilation = FakeAlbum("Great Symphonies Anthology")
        val karajan1 =
            FakeSong(
                compilation,
                "Symphony No. 5",
                "I. Allegro con brio",
                movementNumber = 1,
                track = 1,
                conductors = listOf("Karajan"),
            )
        val karajan2 =
            FakeSong(
                compilation,
                "Symphony No. 5",
                "II. Andante con moto",
                movementNumber = 2,
                track = 2,
                conductors = listOf("Karajan"),
            )
        val kleiber1 =
            FakeSong(
                compilation,
                "Symphony No. 5",
                "I. Allegro con brio",
                movementNumber = 1,
                track = 3,
                conductors = listOf("Kleiber"),
            )
        val kleiber2 =
            FakeSong(
                compilation,
                "Symphony No. 5",
                "II. Andante con moto",
                movementNumber = 2,
                track = 4,
                conductors = listOf("Kleiber"),
            )
        val fillerA =
            FakeSong(compilation, null, "Egmont Overture", movementNumber = null, track = 5)
        val fillerB =
            FakeSong(compilation, null, "Coriolan Overture", movementNumber = null, track = 6)

        val songs = listOf(karajan2, kleiber2, fillerA, karajan1, kleiber1, fillerB)
        for (seed in 1..8) {
            val order =
                WorkShuffleOrder.createWorkAwareOrder(
                    songs,
                    startIndex = seed % songs.size,
                    random = Random(seed),
                )
            assertEquals(songs.size, order.size)

            val karajanPos =
                order.toList().mapIndexedNotNull { pos, idx ->
                    if (songs[idx].conductors.contains("Karajan")) pos else null
                }
            val kleiberPos =
                order.toList().mapIndexedNotNull { pos, idx ->
                    if (songs[idx].conductors.contains("Kleiber")) pos else null
                }

            assertEquals(2, karajanPos.size)
            assertEquals(1, karajanPos[1] - karajanPos[0])
            assertEquals(listOf(1, 2), karajanPos.map { songs[order[it]].movementNumber })

            assertEquals(2, kleiberPos.size)
            assertEquals(1, kleiberPos[1] - kleiberPos[0])
            assertEquals(listOf(1, 2), kleiberPos.map { songs[order[it]].movementNumber })
        }
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
        override val conductors: List<String> = emptyList(),
    ) : Song {
        override val uid: Music.UID =
            requireNotNull(
                Music.UID.fromString(
                    "uas${UUID.nameUUIDFromBytes("${album.uid}:$workName:$movementName:$conductors:$track".toByteArray())}"
                )
            )
        override val name: Name.Known =
            Naming.simple().name(movementName ?: workName ?: "Track", null)
        override val disc: Disc? = null
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
    }
}
