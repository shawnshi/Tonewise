/*
 * Copyright (c) 2026 Tonewise Project
 * LocalWaveformExtractor.kt is part of Tonewise.
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

package com.shawnshi.tonewise.playback.ui

import android.content.Context
import android.media.AudioFormat
import android.media.MediaCodec
import android.media.MediaExtractor
import android.media.MediaFormat
import android.net.Uri
import android.os.Build
import java.nio.ByteBuffer
import java.nio.ByteOrder
import kotlin.math.abs
import kotlin.math.pow
import kotlin.math.roundToInt
import kotlin.math.sqrt
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import timber.log.Timber as L

/**
 * Extracts a 150-point normalized RMS waveform envelope directly on-device via Android
 * [MediaExtractor] + [MediaCodec], matching Desktop/Rust `tonewise-dsp-core` perceptual scaling
 * (`norm.pow(0.8)`).
 */
object LocalWaveformExtractor {
    const val ENVELOPE_POINTS = 150
    private const val DECODE_TIMEOUT_US = 10_000L
    private const val MAX_PACKETS_PER_SEEK_WINDOW = 4
    private const val SEQUENTIAL_THRESHOLD_US = 25_000_000L

    suspend fun extract(context: Context, uri: Uri, durationHintMs: Long): FloatArray? {
        val extractor = MediaExtractor()
        var codec: MediaCodec? = null
        try {
            extractor.setDataSource(context, uri, null)
            var audioTrackIndex = -1
            var trackFormat: MediaFormat? = null
            for (i in 0 until extractor.trackCount) {
                val fmt = extractor.getTrackFormat(i)
                val mime = fmt.getString(MediaFormat.KEY_MIME).orEmpty()
                if (mime.startsWith("audio/")) {
                    audioTrackIndex = i
                    trackFormat = fmt
                    break
                }
            }
            if (audioTrackIndex < 0 || trackFormat == null) return null

            val mime = trackFormat.getString(MediaFormat.KEY_MIME) ?: return null
            val durationUs =
                if (trackFormat.containsKey(MediaFormat.KEY_DURATION)) {
                    trackFormat.getLong(MediaFormat.KEY_DURATION)
                } else {
                    durationHintMs * 1000L
                }
            if (durationUs <= 0L) return null

            extractor.selectTrack(audioTrackIndex)
            codec = MediaCodec.createDecoderByType(mime)
            codec.configure(trackFormat, null, null, 0)
            codec.start()

            var channels =
                if (trackFormat.containsKey(MediaFormat.KEY_CHANNEL_COUNT)) {
                    trackFormat.getInteger(MediaFormat.KEY_CHANNEL_COUNT).coerceAtLeast(1)
                } else {
                    2
                }
            var pcmEncoding =
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N &&
                    trackFormat.containsKey(MediaFormat.KEY_PCM_ENCODING)
                ) {
                    trackFormat.getInteger(MediaFormat.KEY_PCM_ENCODING)
                } else {
                    AudioFormat.ENCODING_PCM_16BIT
                }

            val sumSq = DoubleArray(ENVELOPE_POINTS)
            val counts = LongArray(ENVELOPE_POINTS)
            val bufferInfo = MediaCodec.BufferInfo()

            if (durationUs <= SEQUENTIAL_THRESHOLD_US) {
                decodeSequential(
                    extractor = extractor,
                    codec = codec,
                    durationUs = durationUs,
                    initialChannels = channels,
                    initialEncoding = pcmEncoding,
                    sumSq = sumSq,
                    counts = counts,
                    bufferInfo = bufferInfo,
                )
            } else {
                val bucketDurationUs = durationUs.toDouble() / ENVELOPE_POINTS.toDouble()
                for (bucket in 0 until ENVELOPE_POINTS) {
                    currentCoroutineContext().ensureActive()
                    val targetUs = ((bucket + 0.5) * bucketDurationUs).toLong().coerceIn(0L, durationUs - 1L)
                    extractor.seekTo(targetUs, MediaExtractor.SEEK_TO_CLOSEST_SYNC)
                    codec.flush()

                    var packetsFed = 0
                    var inputDone = false
                    var outputDone = false
                    var idleIterations = 0

                    while (!outputDone && idleIterations < 24) {
                        currentCoroutineContext().ensureActive()
                        if (!inputDone && packetsFed < MAX_PACKETS_PER_SEEK_WINDOW) {
                            val inIdx = codec.dequeueInputBuffer(DECODE_TIMEOUT_US)
                            if (inIdx >= 0) {
                                val inBuf = codec.getInputBuffer(inIdx)
                                val sampleSize = if (inBuf != null) extractor.readSampleData(inBuf, 0) else -1
                                if (sampleSize < 0) {
                                    codec.queueInputBuffer(
                                        inIdx,
                                        0,
                                        0,
                                        0L,
                                        MediaCodec.BUFFER_FLAG_END_OF_STREAM,
                                    )
                                    inputDone = true
                                } else {
                                    val pts = extractor.sampleTime.coerceAtLeast(0L)
                                    codec.queueInputBuffer(inIdx, 0, sampleSize, pts, 0)
                                    extractor.advance()
                                    packetsFed++
                                    if (packetsFed >= MAX_PACKETS_PER_SEEK_WINDOW) {
                                        val eosIdx = codec.dequeueInputBuffer(DECODE_TIMEOUT_US)
                                        if (eosIdx >= 0) {
                                            codec.queueInputBuffer(
                                                eosIdx,
                                                0,
                                                0,
                                                pts,
                                                MediaCodec.BUFFER_FLAG_END_OF_STREAM,
                                            )
                                            inputDone = true
                                        }
                                    }
                                }
                            }
                        }

                        val outIdx = codec.dequeueOutputBuffer(bufferInfo, DECODE_TIMEOUT_US)
                        when {
                            outIdx >= 0 -> {
                                idleIterations = 0
                                val outBuf = codec.getOutputBuffer(outIdx)
                                if (outBuf != null && bufferInfo.size > 0) {
                                    accumulateBufferRms(
                                        buffer = outBuf,
                                        offset = bufferInfo.offset,
                                        size = bufferInfo.size,
                                        channels = channels,
                                        pcmEncoding = pcmEncoding,
                                        bucket = bucket,
                                        sumSq = sumSq,
                                        counts = counts,
                                    )
                                }
                                codec.releaseOutputBuffer(outIdx, false)
                                if ((bufferInfo.flags and MediaCodec.BUFFER_FLAG_END_OF_STREAM) != 0 ||
                                    counts[bucket] >= 2048L
                                ) {
                                    outputDone = true
                                }
                            }
                            outIdx == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED -> {
                                val outFmt = codec.outputFormat
                                if (outFmt.containsKey(MediaFormat.KEY_CHANNEL_COUNT)) {
                                    channels = outFmt.getInteger(MediaFormat.KEY_CHANNEL_COUNT).coerceAtLeast(1)
                                }
                                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N &&
                                    outFmt.containsKey(MediaFormat.KEY_PCM_ENCODING)
                                ) {
                                    pcmEncoding = outFmt.getInteger(MediaFormat.KEY_PCM_ENCODING)
                                }
                            }
                            else -> idleIterations++
                        }
                    }
                }
            }

            val rawRms = FloatArray(ENVELOPE_POINTS)
            for (i in 0 until ENVELOPE_POINTS) {
                rawRms[i] =
                    if (counts[i] > 0L) {
                        sqrt(sumSq[i] / counts[i].toDouble()).toFloat()
                    } else {
                        -1f
                    }
            }
            return normalizeRmsEnvelope(rawRms)
        } catch (t: Throwable) {
            if (t is kotlinx.coroutines.CancellationException) throw t
            L.d("LocalWaveformExtractor failed for $uri: ${t.message}")
            return null
        } finally {
            runCatching { codec?.stop() }
            runCatching { codec?.release() }
            runCatching { extractor.release() }
        }
    }

    private suspend fun decodeSequential(
        extractor: MediaExtractor,
        codec: MediaCodec,
        durationUs: Long,
        initialChannels: Int,
        initialEncoding: Int,
        sumSq: DoubleArray,
        counts: LongArray,
        bufferInfo: MediaCodec.BufferInfo,
    ) {
        var channels = initialChannels
        var pcmEncoding = initialEncoding
        var inputDone = false
        var outputDone = false
        var idleIterations = 0

        while (!outputDone && idleIterations < 100) {
            currentCoroutineContext().ensureActive()
            if (!inputDone) {
                val inIdx = codec.dequeueInputBuffer(DECODE_TIMEOUT_US)
                if (inIdx >= 0) {
                    val inBuf = codec.getInputBuffer(inIdx)
                    val sampleSize = if (inBuf != null) extractor.readSampleData(inBuf, 0) else -1
                    if (sampleSize < 0) {
                        codec.queueInputBuffer(
                            inIdx,
                            0,
                            0,
                            0L,
                            MediaCodec.BUFFER_FLAG_END_OF_STREAM,
                        )
                        inputDone = true
                    } else {
                        val pts = extractor.sampleTime.coerceAtLeast(0L)
                        codec.queueInputBuffer(inIdx, 0, sampleSize, pts, 0)
                        extractor.advance()
                    }
                }
            }

            val outIdx = codec.dequeueOutputBuffer(bufferInfo, DECODE_TIMEOUT_US)
            when {
                outIdx >= 0 -> {
                    idleIterations = 0
                    val outBuf = codec.getOutputBuffer(outIdx)
                    if (outBuf != null && bufferInfo.size > 0) {
                        val bucket =
                            ((bufferInfo.presentationTimeUs.toDouble() / durationUs.toDouble()) *
                                    ENVELOPE_POINTS)
                                .toInt()
                                .coerceIn(0, ENVELOPE_POINTS - 1)
                        accumulateBufferRms(
                            buffer = outBuf,
                            offset = bufferInfo.offset,
                            size = bufferInfo.size,
                            channels = channels,
                            pcmEncoding = pcmEncoding,
                            bucket = bucket,
                            sumSq = sumSq,
                            counts = counts,
                        )
                    }
                    codec.releaseOutputBuffer(outIdx, false)
                    if ((bufferInfo.flags and MediaCodec.BUFFER_FLAG_END_OF_STREAM) != 0) {
                        outputDone = true
                    }
                }
                outIdx == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED -> {
                    val outFmt = codec.outputFormat
                    if (outFmt.containsKey(MediaFormat.KEY_CHANNEL_COUNT)) {
                        channels = outFmt.getInteger(MediaFormat.KEY_CHANNEL_COUNT).coerceAtLeast(1)
                    }
                    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N &&
                        outFmt.containsKey(MediaFormat.KEY_PCM_ENCODING)
                    ) {
                        pcmEncoding = outFmt.getInteger(MediaFormat.KEY_PCM_ENCODING)
                    }
                }
                else -> idleIterations++
            }
        }
    }

    private fun accumulateBufferRms(
        buffer: ByteBuffer,
        offset: Int,
        size: Int,
        channels: Int,
        pcmEncoding: Int,
        bucket: Int,
        sumSq: DoubleArray,
        counts: LongArray,
    ) {
        val dup = buffer.duplicate().order(ByteOrder.LITTLE_ENDIAN)
        dup.position(offset)
        dup.limit(offset + size)

        var bucketSum = 0.0
        var frameCount = 0L

        when (pcmEncoding) {
            AudioFormat.ENCODING_PCM_FLOAT -> {
                val frameBytes = channels * 4
                val frames = size / frameBytes
                for (f in 0 until frames) {
                    var mono = 0.0
                    for (c in 0 until channels) {
                        mono += dup.getFloat().toDouble()
                    }
                    mono /= channels
                    bucketSum += mono * mono
                    frameCount++
                }
            }
            AudioFormat.ENCODING_PCM_32BIT -> {
                val frameBytes = channels * 4
                val frames = size / frameBytes
                for (f in 0 until frames) {
                    var mono = 0.0
                    for (c in 0 until channels) {
                        mono += dup.getInt().toDouble() / 2147483648.0
                    }
                    mono /= channels
                    bucketSum += mono * mono
                    frameCount++
                }
            }
            AudioFormat.ENCODING_PCM_24BIT_PACKED -> {
                val frameBytes = channels * 3
                val frames = size / frameBytes
                for (f in 0 until frames) {
                    var mono = 0.0
                    for (c in 0 until channels) {
                        val b0 = dup.get().toInt() and 0xFF
                        val b1 = dup.get().toInt() and 0xFF
                        val b2 = dup.get().toInt()
                        val sample = (b2 shl 16) or (b1 shl 8) or b0
                        mono += sample.toDouble() / 8388608.0
                    }
                    mono /= channels
                    bucketSum += mono * mono
                    frameCount++
                }
            }
            else -> {
                val frameBytes = channels * 2
                val frames = size / frameBytes
                for (f in 0 until frames) {
                    var mono = 0.0
                    for (c in 0 until channels) {
                        mono += dup.getShort().toDouble() / 32768.0
                    }
                    mono /= channels
                    bucketSum += mono * mono
                    frameCount++
                }
            }
        }

        sumSq[bucket] += bucketSum
        counts[bucket] += frameCount
    }

    /**
     * Interpolates missing buckets (`< 0f`) and applies Desktop/Rust-equivalent perceptual RMS
     * normalization (`(rms / maxRms).pow(0.8f)`). Returns `null` if all buckets are silent/empty.
     */
    fun normalizeRmsEnvelope(rawBuckets: FloatArray): FloatArray? {
        if (rawBuckets.size != ENVELOPE_POINTS) return null
        val filled = rawBuckets.copyOf()
        var hasValid = false
        for (i in filled.indices) {
            if (filled[i] >= 0f && filled[i].isFinite()) {
                hasValid = true
                break
            }
        }
        if (!hasValid) return null

        // Fill missing (-1f) buckets via nearest valid neighbor interpolation
        var lastValid = -1f
        for (i in filled.indices) {
            if (filled[i] >= 0f && filled[i].isFinite()) {
                lastValid = filled[i]
            } else if (lastValid >= 0f) {
                filled[i] = lastValid
            }
        }
        var nextValid = -1f
        for (i in filled.indices.reversed()) {
            if (filled[i] >= 0f && filled[i].isFinite()) {
                nextValid = filled[i]
            } else if (nextValid >= 0f) {
                filled[i] = nextValid
            }
        }

        var maxRms = 0f
        for (v in filled) {
            val av = abs(v)
            if (av > maxRms) maxRms = av
        }
        if (maxRms <= 1e-6f) {
            return FloatArray(ENVELOPE_POINTS) { 0.12f }
        }

        return FloatArray(ENVELOPE_POINTS) { i ->
            val norm = (abs(filled[i]) / maxRms).coerceIn(0f, 1f)
            norm.pow(0.8f).coerceIn(0f, 1f)
        }
    }

    fun quantizeToBytes(envelope: FloatArray): ByteArray =
        ByteArray(ENVELOPE_POINTS) { i ->
            val v = envelope.getOrElse(i) { 0f }.coerceIn(0f, 1f)
            (v * 255f).roundToInt().coerceIn(0, 255).toByte()
        }

    fun dequantizeFromBytes(bytes: ByteArray): FloatArray? {
        if (bytes.size != ENVELOPE_POINTS) return null
        return FloatArray(ENVELOPE_POINTS) { i ->
            ((bytes[i].toInt() and 0xFF) / 255f).coerceIn(0f, 1f)
        }
    }
}
