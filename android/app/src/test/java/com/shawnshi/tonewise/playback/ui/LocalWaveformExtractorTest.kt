package com.shawnshi.tonewise.playback.ui

import kotlin.math.pow
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Test

class LocalWaveformExtractorTest {
    @Test
    fun `normalizeRmsEnvelope interpolates gaps and matches perceptual pow 0_8 curve`() {
        val raw = FloatArray(LocalWaveformExtractor.ENVELOPE_POINTS) { -1f }
        raw[0] = 0.25f
        raw[75] = 1.0f
        raw[149] = 0.5f

        val normalized = LocalWaveformExtractor.normalizeRmsEnvelope(raw)
        assertNotNull(normalized)
        val env = normalized!!
        assertEquals(150, env.size)
        assertEquals(0.25f.pow(0.8f), env[0], 1e-4f)
        assertEquals(1.0f, env[75], 1e-4f)
        assertEquals(0.5f.pow(0.8f), env[149], 1e-4f)
    }

    @Test
    fun `normalizeRmsEnvelope returns null when all buckets are missing`() {
        val raw = FloatArray(LocalWaveformExtractor.ENVELOPE_POINTS) { -1f }
        assertNull(LocalWaveformExtractor.normalizeRmsEnvelope(raw))
    }

    @Test
    fun `quantizeToBytes and dequantizeFromBytes roundtrip within 1 LSB`() {
        val original = FloatArray(LocalWaveformExtractor.ENVELOPE_POINTS) { i -> i / 149f }
        val bytes = LocalWaveformExtractor.quantizeToBytes(original)
        assertEquals(150, bytes.size)
        val restored = LocalWaveformExtractor.dequantizeFromBytes(bytes)
        assertNotNull(restored)
        for (i in original.indices) {
            assertEquals(original[i], restored!![i], 1f / 255f + 1e-4f)
        }
    }
}
