/*
 * Copyright (c) 2026 Tonewise Project
 * PlaybackAndClockP0Test.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.playback

import androidx.media3.common.C
import com.shawnshi.tonewise.playback.audio.AudioOutputManager
import com.shawnshi.tonewise.playback.audio.AudioOutputManager.BitPerfectCandidate
import com.shawnshi.tonewise.playback.dsp.softLimitSample
import kotlin.math.abs
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class PlaybackAndClockP0Test {
    @Test
    fun softLimitSample_isContinuousStrictlyMonotonicAndBoundedWithinCeiling() {
        val ceiling = 10.0.let { 0.977237 } // -0.2 dBFS
        val knee = ceiling * 0.85

        // Unity gain below knee
        assertEquals(0.5, softLimitSample(0.5, ceiling), 1e-12)
        assertEquals(-knee, softLimitSample(-knee, ceiling), 1e-12)

        // No 24% discontinuity crossing +/-ceiling (previous bug jumped from 0.977 to 0.744)
        val belowCeiling = softLimitSample(ceiling - 1e-4, ceiling)
        val atCeiling = softLimitSample(ceiling, ceiling)
        val aboveCeiling = softLimitSample(ceiling + 1e-4, ceiling)
        assertTrue(abs(atCeiling - belowCeiling) < 0.001)
        assertTrue(abs(aboveCeiling - atCeiling) < 0.001)
        assertTrue(belowCeiling < atCeiling && atCeiling < aboveCeiling)

        // Strictly monotonic in active soft-knee region and monotonically non-decreasing up to +/-ceiling
        var prev = Double.NEGATIVE_INFINITY
        for (i in -5000..5000) {
            val x = i / 1000.0
            val y = softLimitSample(x, ceiling)
            if (abs(x) <= 2.0) {
                assertTrue("Expected strictly monotonic at x=$x ($prev < $y)", y > prev)
            } else {
                assertTrue("Expected non-decreasing at x=$x ($prev <= $y)", y >= prev)
            }
            assertTrue("Expected |y| <= ceiling at x=$x (got $y)", abs(y) <= ceiling + 1e-12)
            prev = y
        }
    }

    @Test
    fun audioOutputManager_isExactBitPerfectMatch_rejectsEncodingAndRateMismatches() {
        val usb24_96 = BitPerfectCandidate(96_000, AudioOutputManager.PCM_ENCODING_24BIT_PACKED)
        val usb16_44 = BitPerfectCandidate(44_100, AudioOutputManager.PCM_ENCODING_16BIT)
        val usbFloat_48 = BitPerfectCandidate(48_000, AudioOutputManager.PCM_ENCODING_FLOAT)

        // When DefaultAudioSink opens AudioTrack in 16-bit PCM, a 24-bit mixer attribute is NOT bit-perfect
        assertFalse(
            AudioOutputManager.isExactBitPerfectMatch(
                candidate = usb24_96,
                targetSampleRateHz = 96_000,
                actualTrackMedia3Encoding = C.ENCODING_PCM_16BIT,
            )
        )

        // Exact rate + exact encoding matches cleanly
        assertTrue(
            AudioOutputManager.isExactBitPerfectMatch(
                candidate = usb16_44,
                targetSampleRateHz = 44_100,
                actualTrackMedia3Encoding = C.ENCODING_PCM_16BIT,
            )
        )
        assertTrue(
            AudioOutputManager.isExactBitPerfectMatch(
                candidate = usbFloat_48,
                targetSampleRateHz = 48_000,
                actualTrackMedia3Encoding = C.ENCODING_PCM_FLOAT,
            )
        )

        // Sample rate mismatch is rejected
        assertFalse(
            AudioOutputManager.isExactBitPerfectMatch(
                candidate = usb16_44,
                targetSampleRateHz = 48_000,
                actualTrackMedia3Encoding = C.ENCODING_PCM_16BIT,
            )
        )
    }
}
