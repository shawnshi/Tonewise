/*
 * Copyright (c) 2026 Tonewise Project
 * ReplayGainAudioProcessorTest.kt is part of Tonewise.
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */
 
package com.shawnshi.tonewise.playback.replaygain

import android.content.Context
import android.net.Uri
import androidx.media3.common.C
import androidx.media3.common.audio.AudioProcessor
import androidx.media3.common.util.UnstableApi
import java.nio.ByteBuffer
import java.nio.ByteOrder
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test
import kotlin.math.abs
import kotlin.math.pow
import kotlin.math.sin
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import com.shawnshi.tonewise.playback.ActionMode
import com.shawnshi.tonewise.playback.PlaySong
import com.shawnshi.tonewise.playback.PlaybackSettings
import com.shawnshi.tonewise.playback.dsp.BiquadType
import com.shawnshi.tonewise.playback.dsp.DspChainConfig
import com.shawnshi.tonewise.playback.dsp.DspConfigSource
import com.shawnshi.tonewise.playback.dsp.DspPresetId
import com.shawnshi.tonewise.playback.dsp.TonewiseDspEngine
import com.shawnshi.tonewise.playback.state.PlaybackCommand
import com.shawnshi.tonewise.playback.state.PlaybackStateHolder
import com.shawnshi.tonewise.playback.state.PlaybackStateManager
import com.shawnshi.tonewise.playback.state.Progression
import com.shawnshi.tonewise.playback.state.RepeatMode
import org.oxycblt.musikr.Album
import org.oxycblt.musikr.Artist
import org.oxycblt.musikr.Genre
import org.oxycblt.musikr.Music
import org.oxycblt.musikr.MusicParent
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

@OptIn(UnstableApi::class)
class ReplayGainAudioProcessorTest {
    @Test
    fun configure_accepts16_24_32_andFloatPcm_andRejectsUnsupported() {
        val processor = createProcessor(trackGainDb = -6f)
        for (encoding in
            listOf(
                C.ENCODING_PCM_16BIT,
                C.ENCODING_PCM_24BIT,
                C.ENCODING_PCM_32BIT,
                C.ENCODING_PCM_FLOAT,
            )) {
            val fmt = AudioProcessor.AudioFormat(44100, 2, encoding)
            assertEquals(fmt, processor.configure(fmt))
        }

        assertThrows(AudioProcessor.UnhandledAudioFormatException::class.java) {
            processor.configure(AudioProcessor.AudioFormat(44100, 2, C.ENCODING_PCM_8BIT))
        }
    }

    @Test
    fun floatPcm_scalesAccurately_andBypassPreservesBitExactness() {
        val processor = createProcessor(trackGainDb = -6.0206f) // ~0.5x amplitude
        val fmt = AudioProcessor.AudioFormat(96000, 1, C.ENCODING_PCM_FLOAT)
        processor.configure(fmt)
        processor.flush()

        val input = ByteBuffer.allocateDirect(8).order(ByteOrder.LITTLE_ENDIAN)
        input.putFloat(0.8f)
        input.putFloat(-0.4f)
        input.flip()

        processor.queueInput(input)
        val out = processor.output.order(ByteOrder.LITTLE_ENDIAN)
        assertEquals(0.4f, out.getFloat(), 0.001f)
        assertEquals(-0.2f, out.getFloat(), 0.001f)

        // Enable bit-perfect bypass: output must match input bit-for-bit
        processor.setBypassed(true)
        val bypassIn = ByteBuffer.allocateDirect(8).order(ByteOrder.LITTLE_ENDIAN)
        bypassIn.putFloat(0.8f)
        bypassIn.putFloat(-0.4f)
        bypassIn.flip()

        processor.queueInput(bypassIn)
        val bypassOut = processor.output.order(ByteOrder.LITTLE_ENDIAN)
        assertEquals(0.8f.toRawBits(), bypassOut.getFloat().toRawBits())
        assertEquals((-0.4f).toRawBits(), bypassOut.getFloat().toRawBits())
    }

    @Test
    fun pcm24Bit_scalesAndClampsSigned24BitSamples() {
        val processor = createProcessor(trackGainDb = -6.0206f) // ~0.5x amplitude
        val fmt = AudioProcessor.AudioFormat(48000, 1, C.ENCODING_PCM_24BIT)
        processor.configure(fmt)
        processor.flush()

        val samplePos = 4_000_000
        val sampleNeg = -4_000_000
        val input = ByteBuffer.allocateDirect(6).order(ByteOrder.LITTLE_ENDIAN)
        putInt24(input, samplePos)
        putInt24(input, sampleNeg)
        input.flip()

        processor.queueInput(input)
        val out = processor.output.order(ByteOrder.LITTLE_ENDIAN)
        val outPos = getInt24(out)
        val outNeg = getInt24(out)
        assertEquals(2_000_000.toDouble(), outPos.toDouble(), 5_000.0)
        assertEquals((-2_000_000).toDouble(), outNeg.toDouble(), 5_000.0)
    }

    @Test
    fun dspEngine_parsesEqualizerApo_guardsClipping_andCrossfeeds() {
        val apo =
            """
            Preamp: -4.5 dB
            Filter: ON LSC Fc 85 Hz Gain 4.5 dB Q 0.71
            Filter: ON HSC Fc 6000 Hz Gain -2.5 dB Q 0.71
            """.trimIndent()
        val parsed = TonewiseDspEngine.parseEqualizerApo(apo)
        assertEquals(-4.5, parsed.preampDb, 1e-6)
        assertEquals(2, parsed.bands.size)
        assertEquals(BiquadType.LOW_SHELF, parsed.bands[0].type)
        assertEquals(-7.5, TonewiseDspEngine.computeEffectivePreampDb(parsed, -3.0), 1e-5)

        // Stereo crossfeed + lookahead limiter ceiling (-0.2 dBFS ~ 0.977237)
        val crossConfig =
            DspChainConfig.preset(DspPresetId.CLASSICAL_CROSSFEED).copy(
                crossfeedEnabled = true,
                limiterEnabled = true,
                limiterCeilingDb = -0.2,
            )
        val dspSource = FakeDspConfigSource(crossConfig)
        val processor =
            ReplayGainAudioProcessor(
                FakePlaybackStateManager(FakeSong(ReplayGainAdjustment(track = 0f, album = null))),
                FakePlaybackSettings(ReplayGainMode.OFF),
                dspSource,
            ).apply { onIndexMoved(0) }

        val fmt = AudioProcessor.AudioFormat(48000, 2, C.ENCODING_PCM_FLOAT)
        processor.configure(fmt)
        processor.flush()

        val frames = 512
        val input = ByteBuffer.allocateDirect(frames * 8).order(ByteOrder.LITTLE_ENDIAN)
        for (i in 0 until frames) {
            // Hot left-only sine wave exceeding 0 dBFS to test both crossfeed & limiter ceiling
            val left = (1.3 * sin(2.0 * Math.PI * 220.0 * i / 48000.0)).toFloat()
            input.putFloat(left)
            input.putFloat(0f)
        }
        input.flip()

        processor.queueInput(input)
        val out = processor.output.order(ByteOrder.LITTLE_ENDIAN)
        val ceilingLinear = 10.0.pow(-0.2 / 20.0).toFloat()
        var rightEnergy = 0.0
        for (i in 0 until frames) {
            val l = out.getFloat()
            val r = out.getFloat()
            assertTrue("Limiter exceeded ceiling: |$l| > $ceilingLinear", abs(l) <= ceilingLinear + 1e-4f)
            assertTrue("Limiter exceeded ceiling: |$r| > $ceilingLinear", abs(r) <= ceilingLinear + 1e-4f)
            if (i > 160) {
                rightEnergy += r * r
            }
        }
        assertTrue("Expected crossfeed bleed into silent right channel", rightEnergy > 0.01)

        // BIT_PERFECT bypass must pass samples bit-for-bit untouched even with DSP enabled
        processor.setBypassed(true)
        val bypassIn = ByteBuffer.allocateDirect(8).order(ByteOrder.LITTLE_ENDIAN)
        bypassIn.putFloat(0.75f)
        bypassIn.putFloat(-0.25f)
        bypassIn.flip()
        processor.queueInput(bypassIn)
        val bypassOut = processor.output.order(ByteOrder.LITTLE_ENDIAN)
        assertEquals(0.75f.toRawBits(), bypassOut.getFloat().toRawBits())
        assertEquals((-0.25f).toRawBits(), bypassOut.getFloat().toRawBits())
    }

    @Test
    fun setBypassed_resetsStateOnAudioThread_andTogglesSafelyConcurrently() {
        val crossConfig =
            DspChainConfig.preset(DspPresetId.CLASSICAL_CROSSFEED).copy(
                crossfeedEnabled = true,
                limiterEnabled = true,
                limiterCeilingDb = -0.2,
            )
        val processor =
            ReplayGainAudioProcessor(
                FakePlaybackStateManager(FakeSong(ReplayGainAdjustment(track = 0f, album = null))),
                FakePlaybackSettings(ReplayGainMode.OFF),
                FakeDspConfigSource(crossConfig),
            ).apply { onIndexMoved(0) }

        val fmt = AudioProcessor.AudioFormat(48000, 2, C.ENCODING_PCM_FLOAT)
        processor.configure(fmt)
        processor.flush()

        val frames = 128
        val blockBytes = frames * 8
        val warmIn = ByteBuffer.allocateDirect(blockBytes).order(ByteOrder.LITTLE_ENDIAN)
        for (i in 0 until frames) {
            val s = (0.85 * sin(2.0 * Math.PI * 330.0 * i / 48000.0)).toFloat()
            warmIn.putFloat(s)
            warmIn.putFloat(-s * 0.5f)
        }
        warmIn.flip()
        processor.queueInput(warmIn)
        val warmOut = processor.output.order(ByteOrder.LITTLE_ENDIAN)
        while (warmOut.remaining() >= 4) {
            assertTrue(warmOut.getFloat().isFinite())
        }

        // Toggle bypass on/off without external flush() and verify clean passthrough & DSP recovery.
        processor.setBypassed(true)
        val passthroughIn = ByteBuffer.allocateDirect(8).order(ByteOrder.LITTLE_ENDIAN)
        passthroughIn.putFloat(0.625f)
        passthroughIn.putFloat(-0.375f)
        passthroughIn.flip()
        processor.queueInput(passthroughIn)
        val passthroughOut = processor.output.order(ByteOrder.LITTLE_ENDIAN)
        assertEquals(0.625f.toRawBits(), passthroughOut.getFloat().toRawBits())
        assertEquals((-0.375f).toRawBits(), passthroughOut.getFloat().toRawBits())

        processor.setBypassed(false)
        warmIn.rewind()
        processor.queueInput(warmIn)
        val resumedOut = processor.output.order(ByteOrder.LITTLE_ENDIAN)
        while (resumedOut.remaining() >= 4) {
            assertTrue(resumedOut.getFloat().isFinite())
        }

        // Concurrent UI toggle vs audio-thread queueInput stress (200 blocks).
        val error = java.util.concurrent.atomic.AtomicReference<Throwable?>(null)
        val startGate = java.util.concurrent.CountDownLatch(1)
        val stopFlag = java.util.concurrent.atomic.AtomicBoolean(false)
        val toggleThread =
            java.lang.Thread {
                try {
                    startGate.await()
                    var flag = false
                    while (!stopFlag.get()) {
                        flag = !flag
                        processor.setBypassed(flag)
                    }
                } catch (t: Throwable) {
                    error.compareAndSet(null, t)
                }
            }
        toggleThread.start()
        startGate.countDown()

        val loopBuf = ByteBuffer.allocateDirect(blockBytes).order(ByteOrder.LITTLE_ENDIAN)
        try {
            for (block in 0 until 200) {
                loopBuf.clear()
                for (i in 0 until frames) {
                    val sample = (0.6 * sin(2.0 * Math.PI * 440.0 * (block * frames + i) / 48000.0)).toFloat()
                    loopBuf.putFloat(sample)
                    loopBuf.putFloat(sample * 0.5f)
                }
                loopBuf.flip()
                processor.queueInput(loopBuf)
                val out = processor.output.order(ByteOrder.LITTLE_ENDIAN)
                while (out.remaining() >= 4) {
                    assertTrue(out.getFloat().isFinite())
                }
            }
        } finally {
            stopFlag.set(true)
            toggleThread.join(5000)
        }
        assertEquals(null, error.get())
    }

    @Test
    fun replayGainOff_ignoresNonZeroPreAmpWithout_andPassesThroughBitExact() {
        val song = FakeSong(ReplayGainAdjustment(track = -5f, album = -7f))
        val settings =
            FakePlaybackSettings(
                replayGainMode = ReplayGainMode.OFF,
                replayGainPreAmp = ReplayGainPreAmp(with = -2f, without = -6f),
            )
        val processor =
            ReplayGainAudioProcessor(FakePlaybackStateManager(song), settings).apply {
                onIndexMoved(0)
            }
        assertEquals(0f, processor.currentAdjustmentDb, 1e-6f)

        processor.configure(AudioProcessor.AudioFormat(48000, 1, C.ENCODING_PCM_FLOAT))
        processor.flush()
        val input = ByteBuffer.allocateDirect(4).order(ByteOrder.LITTLE_ENDIAN)
        input.putFloat(0.65f)
        input.flip()

        processor.queueInput(input)
        val out = processor.output.order(ByteOrder.LITTLE_ENDIAN)
        assertEquals(0.65f.toRawBits(), out.getFloat().toRawBits())
    }

    @Test
    fun dynamicMode_respectsAlbumParentAndShuffle_andFallsBackWhenTrackMissing() {
        val albumA = FakeAlbum()
        val albumB = FakeAlbum()
        val song = FakeSong(ReplayGainAdjustment(track = -2.0f, album = -8.0f), songAlbum = albumA)
        val manager = FakePlaybackStateManager(song, parent = albumA, isShuffled = false)
        val processor =
            ReplayGainAudioProcessor(manager, FakePlaybackSettings(ReplayGainMode.DYNAMIC)).apply {
                onNewPlayback(albumA, listOf(song), 0, isShuffled = false)
            }
        assertEquals(-8.0f, processor.currentAdjustmentDb, 1e-6f)

        // Enabling shuffle in Dynamic mode switches from album gain to track gain
        manager.isShuffled = true
        processor.onQueueReordered(listOf(song), 0, isShuffled = true)
        assertEquals(-2.0f, processor.currentAdjustmentDb, 1e-6f)

        // Playing from another parent uses track gain
        manager.isShuffled = false
        manager.parent = albumB
        processor.onNewPlayback(albumB, listOf(song), 0, isShuffled = false)
        assertEquals(-2.0f, processor.currentAdjustmentDb, 1e-6f)

        // If track gain is null outside album context, falls back to album gain
        val albumOnlySong = FakeSong(ReplayGainAdjustment(track = null, album = -6.5f), songAlbum = albumA)
        manager.currentSong = albumOnlySong
        processor.onIndexMoved(0)
        assertEquals(-6.5f, processor.currentAdjustmentDb, 1e-6f)
    }

    @Test
    fun positiveReplayGainFastPath_appliesZeroLatencySoftLimiterWhenEnabled() {
        val song = FakeSong(ReplayGainAdjustment(track = 6.0f, album = null))
        val cfg = DspChainConfig(enabled = false, limiterEnabled = true, limiterCeilingDb = -0.2)
        val processor =
            ReplayGainAudioProcessor(
                FakePlaybackStateManager(song),
                FakePlaybackSettings(ReplayGainMode.TRACK),
                FakeDspConfigSource(cfg),
            ).apply { onIndexMoved(0) }

        processor.configure(AudioProcessor.AudioFormat(48000, 1, C.ENCODING_PCM_FLOAT))
        processor.flush()

        val input = ByteBuffer.allocateDirect(4).order(ByteOrder.LITTLE_ENDIAN)
        input.putFloat(0.9f) // 0.9 * ~2.0x boost = ~1.8 -> must be soft-limited <= ceiling (< 1.0)
        input.flip()

        processor.queueInput(input)
        val out = processor.output.order(ByteOrder.LITTLE_ENDIAN)
        val limited = out.getFloat()
        val ceiling = 10.0.pow(-0.2 / 20.0).toFloat()
        assertTrue("Expected positive sample after boost: $limited", limited > 0.7f)
        assertTrue("Expected soft-limited below ceiling ($ceiling), got $limited", limited <= ceiling + 1e-5f)
    }

    private class FakeDspConfigSource(initial: DspChainConfig) : DspConfigSource {
        override val config: StateFlow<DspChainConfig> = MutableStateFlow(initial)
    }

    private fun putInt24(buf: ByteBuffer, value: Int) {
        buf.put(value.toByte())
        buf.put((value shr 8).toByte())
        buf.put((value shr 16).toByte())
    }

    private fun getInt24(buf: ByteBuffer): Int {
        val b0 = buf.get().toInt() and 0xFF
        val b1 = buf.get().toInt() and 0xFF
        val b2 = buf.get().toInt()
        return (b2 shl 16) or (b1 shl 8) or b0
    }

    private fun createProcessor(trackGainDb: Float): ReplayGainAudioProcessor {
        val song = FakeSong(ReplayGainAdjustment(track = trackGainDb, album = null))
        val manager = FakePlaybackStateManager(song)
        val settings = FakePlaybackSettings(ReplayGainMode.TRACK)
        return ReplayGainAudioProcessor(manager, settings).apply { onIndexMoved(0) }
    }

    private class FakePlaybackSettings(
        override val replayGainMode: ReplayGainMode,
        override var replayGainPreAmp: ReplayGainPreAmp = ReplayGainPreAmp(0f, 0f),
    ) : PlaybackSettings {
        override val barAction = ActionMode.NEXT
        override val headsetAutoplay = false
        override val playInListWith = PlaySong.FromAll
        override val inParentPlaybackMode: PlaySong? = null
        override val keepShuffle = true
        override val rewindWithPrev = true
        override val pauseOnRepeat = false
        override val rememberPause = false
        override val exitOnTaskRemoval = false
        override val floatOutput = true
        override val usbBitPerfect = true

        override fun migrate() {}

        override fun registerListener(listener: PlaybackSettings.Listener) {}

        override fun unregisterListener(listener: PlaybackSettings.Listener) {}
    }

    private class FakePlaybackStateManager(
        override var currentSong: Song?,
        override var parent: MusicParent? = null,
        override var isShuffled: Boolean = false,
    ) : PlaybackStateManager {
        override val progression: Progression
            get() = error("unused")
        override val repeatMode = RepeatMode.NONE
        override val queue: List<Song>
            get() = listOfNotNull(currentSong)
        override val index = 0
        override val currentAudioSessionId: Int? = null

        override fun addListener(listener: PlaybackStateManager.Listener) {}

        override fun removeListener(listener: PlaybackStateManager.Listener) {}

        override fun registerStateHolder(stateHolder: PlaybackStateHolder) {}

        override fun unregisterStateHolder(stateHolder: PlaybackStateHolder) {}

        override fun play(command: PlaybackCommand) {}

        override fun next() {}

        override fun prev() {}

        override fun goto(index: Int) {}

        override fun playNext(songs: List<Song>) {}

        override fun addToQueue(songs: List<Song>) {}

        override fun moveQueueItem(src: Int, dst: Int) {}

        override fun removeQueueItem(at: Int) {}

        override fun playing(playing: Boolean) {}

        override fun seekTo(positionMs: Long) {}

        override fun repeatMode(repeatMode: RepeatMode) {}

        override fun shuffled(shuffled: Boolean) {}

        override fun playDeferred(action: com.shawnshi.tonewise.playback.state.DeferredPlayback) {}

        override fun requestAction(stateHolder: PlaybackStateHolder) {}

        override fun ack(stateHolder: PlaybackStateHolder, ack: com.shawnshi.tonewise.playback.state.StateAck) {}

        override fun endSession() {}

        override fun toSavedState(): PlaybackStateManager.SavedState? = null

        override fun applySavedState(
            savedState: PlaybackStateManager.SavedState,
            destructive: Boolean,
        ) {}
    }

    private class FakeSong(
        override val replayGainAdjustment: ReplayGainAdjustment,
        private val songAlbum: Album? = null,
    ) : Song {
        override val uid: Music.UID
            get() = error("unused")

        override val name: Name.Known
            get() = error("unused")

        override val track: Int? = null
        override val disc: Disc? = null
        override val date: Date? = null
        override val uri: Uri
            get() = error("unused")

        override val path: Path
            get() = error("unused")

        override val format: Format
            get() = error("unused")

        override val size: Long = 0L
        override val durationMs: Long = 10_000L
        override val bitrateKbps: Int = 1000
        override val sampleRateHz: Int = 96000
        override val modifiedMs: Long = 0L
        override val addedMs: Long = 0L
        override val cover: Cover? = null
        override val album: Album
            get() = songAlbum ?: error("unused")

        override val artists: List<Artist> = emptyList()
        override val genres: List<Genre> = emptyList()
        override val composers: List<String> = emptyList()
        override val conductors: List<String> = emptyList()
    }

    private class FakeAlbum : Album {
        override val uid: Music.UID
            get() = error("unused")
        override val name: Name
            get() = error("unused")
        override val songs: Collection<Song> = emptyList()
        override val dates: Date.Range? = null
        override val releaseType: ReleaseType
            get() = error("unused")
        override val covers: CoverCollection
            get() = error("unused")
        override val durationMs: Long = 0L
        override val addedMs: Long = 0L
        override val artists: List<Artist> = emptyList()
    }
}
