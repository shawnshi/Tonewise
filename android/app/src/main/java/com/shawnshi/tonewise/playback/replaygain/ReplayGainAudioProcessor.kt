/*
 * Copyright (c) 2022 Auxio Project
 * ReplayGainAudioProcessor.kt is part of Auxio.
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

import androidx.media3.common.C
import androidx.media3.common.Format
import androidx.media3.common.Player
import androidx.media3.common.audio.AudioProcessor
import androidx.media3.common.audio.BaseAudioProcessor
import androidx.media3.common.util.UnstableApi
import java.nio.ByteBuffer
import javax.inject.Inject
import kotlin.math.abs
import kotlin.math.pow
import kotlin.math.roundToInt
import kotlin.math.roundToLong
import com.shawnshi.tonewise.playback.PlaybackSettings
import com.shawnshi.tonewise.playback.dsp.DspChainConfig
import com.shawnshi.tonewise.playback.dsp.DspConfigSource
import com.shawnshi.tonewise.playback.dsp.TonewiseDspEngine
import com.shawnshi.tonewise.playback.state.PlaybackStateManager
import com.shawnshi.tonewise.playback.state.QueueChange
import org.oxycblt.musikr.Album
import org.oxycblt.musikr.MusicParent
import org.oxycblt.musikr.Song
import timber.log.Timber as L

/**
 * An [AudioProcessor] that handles ReplayGain values and their amplification of the audio stream.
 * Instead of leveraging the volume attribute like other implementations, this system manipulates
 * the bitstream itself to modify the volume, which allows the use of positive ReplayGain values.
 *
 * Note: This audio processor must be attached to a respective [Player] instance as a
 * [Player.Listener] to function properly.
 *
 * @author Alexander Capehart (OxygenCobalt)
 */
@OptIn(markerClass = [UnstableApi::class])
class ReplayGainAudioProcessor
@Inject
constructor(
    private val playbackManager: PlaybackStateManager,
    private val playbackSettings: PlaybackSettings,
    private val dspConfigSource: DspConfigSource? = null,
) : BaseAudioProcessor(), PlaybackStateManager.Listener, PlaybackSettings.Listener {
    private val dspEngine = TonewiseDspEngine()
    private var workBuffer = DoubleArray(0)
    private var volume = 1f
        set(value) {
            field = value
            // Processed bytes are no longer valid, flush the stream
            flush()
        }

    @Volatile
    var isBypassed: Boolean = false
        private set

    @Volatile
    var currentAdjustmentDb: Float = 0f
        private set

    @Volatile
    var configuredEncoding: Int = C.ENCODING_INVALID
        private set

    fun setBypassed(bypassed: Boolean) {
        if (isBypassed != bypassed) {
            isBypassed = bypassed
            flush()
        }
    }

    fun attach() {
        playbackManager.addListener(this)
        playbackSettings.registerListener(this)
    }

    /** Remove this instance from the components required for it to function correctly. */
    fun release() {
        playbackManager.removeListener(this)
        playbackSettings.unregisterListener(this)
    }

    // --- OVERRIDES ---

    override fun onIndexMoved(index: Int) {
        L.d("Index moved, updating current song")
        applyReplayGain(playbackManager.currentSong)
    }

    override fun onQueueChanged(queue: List<Song>, index: Int, change: QueueChange) {
        // Other types of queue changes preserve the current song.
        if (change.type == QueueChange.Type.SONG) {
            applyReplayGain(playbackManager.currentSong)
        }
    }

    override fun onNewPlayback(
        parent: MusicParent?,
        queue: List<Song>,
        index: Int,
        isShuffled: Boolean,
    ) {
        L.d("New playback started, updating playback information")
        applyReplayGain(playbackManager.currentSong)
    }

    override fun onReplayGainSettingsChanged() {
        // ReplayGain config changed, we need to set it up again.
        applyReplayGain(playbackManager.currentSong)
    }

    // --- REPLAYGAIN PARSING ---

    /**
     * Updates the volume adjustment based on the given [Format].
     *
     * @param song The [Format] of the currently playing track, or null if nothing is playing.
     */
    private fun applyReplayGain(song: Song?) {
        if (song == null) {
            L.d("Nothing playing, disabling adjustment")
            currentAdjustmentDb = 0f
            volume = 1f
            return
        }

        L.d("Applying ReplayGain adjustment for $song")

        val gain = song.replayGainAdjustment
        val preAmp = playbackSettings.replayGainPreAmp

        // ReplayGain is configurable, so determine what to do based off of the mode.
        val resolvedAdjustment =
            when (playbackSettings.replayGainMode) {
                // User wants no adjustment.
                ReplayGainMode.OFF -> {
                    L.d("ReplayGain is off")
                    null
                }
                // User wants track gain to be preferred. Default to album gain only if
                // there is no track gain.
                ReplayGainMode.TRACK -> {
                    L.d("Using track strategy")
                    gain.track ?: gain.album
                }
                // User wants album gain to be preferred. Default to track gain only if
                // here is no album gain.
                ReplayGainMode.ALBUM -> {
                    L.d("Using album strategy")
                    gain.album ?: gain.track
                }
                // User wants album gain to be used when in an album, track gain otherwise.
                ReplayGainMode.DYNAMIC -> {
                    L.d("Using dynamic strategy")
                    gain.album?.takeIf {
                        playbackManager.parent is Album &&
                            playbackManager.currentSong?.album == playbackManager.parent
                    } ?: gain.track
                }
            }

        val amplifiedAdjustment =
            if (resolvedAdjustment != null) {
                // Successfully resolved an adjustment, apply the corresponding pre-amp
                L.d("Applying with pre-amp")
                resolvedAdjustment + preAmp.with
            } else {
                // No adjustment found, use the corresponding user-defined pre-amp
                L.d("Applying without pre-amp")
                preAmp.without
            }

        L.d("Applying ReplayGain adjustment ${amplifiedAdjustment}db")

        currentAdjustmentDb = amplifiedAdjustment
        // Final adjustment along the volume curve.
        volume = 10f.pow(amplifiedAdjustment / 20f)
    }

    // --- AUDIO PROCESSOR IMPLEMENTATION ---

    override fun onConfigure(
        inputAudioFormat: AudioProcessor.AudioFormat
    ): AudioProcessor.AudioFormat {
        return when (inputAudioFormat.encoding) {
            C.ENCODING_PCM_16BIT,
            C.ENCODING_PCM_24BIT,
            C.ENCODING_PCM_32BIT,
            C.ENCODING_PCM_FLOAT -> {
                configuredEncoding = inputAudioFormat.encoding
                dspEngine.configure(inputAudioFormat.sampleRate, inputAudioFormat.channelCount)
                inputAudioFormat
            }
            else -> throw AudioProcessor.UnhandledAudioFormatException(inputAudioFormat)
        }
    }

    override fun onFlush() {
        dspEngine.resetState()
    }

    override fun onReset() {
        dspEngine.resetState()
        workBuffer = DoubleArray(0)
    }

    override fun queueInput(inputBuffer: ByteBuffer) {
        val pos = inputBuffer.position()
        val limit = inputBuffer.limit()
        val size = limit - pos
        val buffer = replaceOutputBuffer(size)

        val dspCfg = dspConfigSource?.config?.value ?: DspChainConfig()
        dspEngine.updateConfig(dspCfg)
        val nonIdentityDsp = dspCfg.hasNonIdentityProcessing()

        if (isBypassed || (!nonIdentityDsp && volume == 1f)) {
            buffer.put(inputBuffer.slice())
        } else if (!nonIdentityDsp) {
            val scale = volume.toDouble()
            when (inputAudioFormat.encoding) {
                C.ENCODING_PCM_16BIT -> {
                    for (i in pos until limit step 2) {
                        val sample =
                            (inputBuffer.getLeShort(i) * scale)
                                .toInt()
                                .coerceIn(Short.MIN_VALUE.toInt(), Short.MAX_VALUE.toInt())
                                .toShort()
                        buffer.putLeShort(sample)
                    }
                }
                C.ENCODING_PCM_24BIT -> {
                    for (i in pos until limit step 3) {
                        val sample =
                            (inputBuffer.getLeInt24(i) * scale)
                                .toInt()
                                .coerceIn(MIN_INT24, MAX_INT24)
                        buffer.putLeInt24(sample)
                    }
                }
                C.ENCODING_PCM_32BIT -> {
                    for (i in pos until limit step 4) {
                        val sample =
                            (inputBuffer.getLeInt32(i) * scale)
                                .coerceIn(Int.MIN_VALUE.toDouble(), Int.MAX_VALUE.toDouble())
                                .toInt()
                        buffer.putLeInt32(sample)
                    }
                }
                C.ENCODING_PCM_FLOAT -> {
                    for (i in pos until limit step 4) {
                        val bits = inputBuffer.getLeInt32(i)
                        val sample = Float.fromBits(bits)
                        val scaled = (sample * scale).toFloat()
                        buffer.putLeInt32(scaled.toRawBits())
                    }
                }
                else -> buffer.put(inputBuffer.slice())
            }
        } else {
            val bytesPerSample =
                when (inputAudioFormat.encoding) {
                    C.ENCODING_PCM_16BIT -> 2
                    C.ENCODING_PCM_24BIT -> 3
                    C.ENCODING_PCM_32BIT,
                    C.ENCODING_PCM_FLOAT -> 4
                    else -> 0
                }
            val ch = inputAudioFormat.channelCount.coerceAtLeast(1)
            val totalSamples = if (bytesPerSample > 0) size / bytesPerSample else 0
            val frameCount = totalSamples / ch
            if (frameCount <= 0) {
                buffer.put(inputBuffer.slice())
            } else {
                val usedSamples = frameCount * ch
                if (workBuffer.size < usedSamples) {
                    workBuffer = DoubleArray(usedSamples)
                }
                when (inputAudioFormat.encoding) {
                    C.ENCODING_PCM_16BIT -> {
                        var offset = pos
                        for (s in 0 until usedSamples) {
                            workBuffer[s] = inputBuffer.getLeShort(offset) / 32768.0
                            offset += 2
                        }
                    }
                    C.ENCODING_PCM_24BIT -> {
                        var offset = pos
                        for (s in 0 until usedSamples) {
                            workBuffer[s] = inputBuffer.getLeInt24(offset) / 8388608.0
                            offset += 3
                        }
                    }
                    C.ENCODING_PCM_32BIT -> {
                        var offset = pos
                        for (s in 0 until usedSamples) {
                            workBuffer[s] = inputBuffer.getLeInt32(offset) / 2147483648.0
                            offset += 4
                        }
                    }
                    C.ENCODING_PCM_FLOAT -> {
                        var offset = pos
                        for (s in 0 until usedSamples) {
                            workBuffer[s] = Float.fromBits(inputBuffer.getLeInt32(offset)).toDouble()
                            offset += 4
                        }
                    }
                }

                dspEngine.processInterleaved(
                    samples = workBuffer,
                    frameCount = frameCount,
                    replayGainDb = currentAdjustmentDb.toDouble(),
                )

                when (inputAudioFormat.encoding) {
                    C.ENCODING_PCM_16BIT -> {
                        for (s in 0 until usedSamples) {
                            val v =
                                (workBuffer[s] * 32768.0)
                                    .roundToInt()
                                    .coerceIn(Short.MIN_VALUE.toInt(), Short.MAX_VALUE.toInt())
                                    .toShort()
                            buffer.putLeShort(v)
                        }
                    }
                    C.ENCODING_PCM_24BIT -> {
                        for (s in 0 until usedSamples) {
                            val v = (workBuffer[s] * 8388608.0).roundToInt().coerceIn(MIN_INT24, MAX_INT24)
                            buffer.putLeInt24(v)
                        }
                    }
                    C.ENCODING_PCM_32BIT -> {
                        for (s in 0 until usedSamples) {
                            val v =
                                (workBuffer[s] * 2147483648.0)
                                    .roundToLong()
                                    .coerceIn(Int.MIN_VALUE.toLong(), Int.MAX_VALUE.toLong())
                                    .toInt()
                            buffer.putLeInt32(v)
                        }
                    }
                    C.ENCODING_PCM_FLOAT -> {
                        for (s in 0 until usedSamples) {
                            buffer.putLeInt32(workBuffer[s].toFloat().toRawBits())
                        }
                    }
                }
            }
        }

        inputBuffer.position(limit)
        buffer.flip()
    }

    private fun ByteBuffer.getLeShort(at: Int) =
        get(at + 1).toInt().shl(8).or(get(at).toInt().and(0xFF)).toShort()

    private fun ByteBuffer.putLeShort(short: Short) {
        put(short.toByte())
        put(short.toInt().shr(8).toByte())
    }

    private fun ByteBuffer.getLeInt24(at: Int): Int {
        val b0 = get(at).toInt() and 0xFF
        val b1 = get(at + 1).toInt() and 0xFF
        val b2 = get(at + 2).toInt() // keep sign bit
        return (b2 shl 16) or (b1 shl 8) or b0
    }

    private fun ByteBuffer.putLeInt24(value: Int) {
        put(value.toByte())
        put((value shr 8).toByte())
        put((value shr 16).toByte())
    }

    private fun ByteBuffer.getLeInt32(at: Int): Int {
        val b0 = get(at).toInt() and 0xFF
        val b1 = get(at + 1).toInt() and 0xFF
        val b2 = get(at + 2).toInt() and 0xFF
        val b3 = get(at + 3).toInt() and 0xFF
        return (b3 shl 24) or (b2 shl 16) or (b1 shl 8) or b0
    }

    private fun ByteBuffer.putLeInt32(value: Int) {
        put(value.toByte())
        put((value shr 8).toByte())
        put((value shr 16).toByte())
        put((value shr 24).toByte())
    }

    private companion object {
        const val MIN_INT24 = -8_388_608
        const val MAX_INT24 = 8_388_607
    }
}
