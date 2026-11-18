/*
 * Copyright (c) 2026 Tonewise Project
 * TempoTagParsingTest.kt is part of Tonewise.
 */

package org.oxycblt.musikr.tag.parse

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test
import org.junit.runner.RunWith
import org.oxycblt.musikr.bpmToTempoBucket
import org.oxycblt.musikr.metadata.Metadata
import org.oxycblt.musikr.metadata.Properties
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [30])
class TempoTagParsingTest {
    private val tagParser = TagParser.new()

    @Test
    fun bpmToTempoBucket_matchesCanonicalThresholds() {
        assertNull(bpmToTempoBucket(null))
        assertNull(bpmToTempoBucket(0f))
        assertNull(bpmToTempoBucket(-10f))
        assertNull(bpmToTempoBucket(Float.NaN))
        assertEquals("Very Slow", bpmToTempoBucket(64.9f))
        assertEquals("Slow", bpmToTempoBucket(65.0f))
        assertEquals("Slow", bpmToTempoBucket(89.9f))
        assertEquals("Moderate", bpmToTempoBucket(90.0f))
        assertEquals("Moderate", bpmToTempoBucket(119.9f))
        assertEquals("Fast", bpmToTempoBucket(120.0f))
        assertEquals("Fast", bpmToTempoBucket(149.9f))
        assertEquals("Very Fast", bpmToTempoBucket(150.0f))
    }

    @Test
    fun tagParser_parsesNumericAndTextTempoTags() {
        val xiphMeta =
            Metadata(
                properties = Properties("audio/flac", 240_000L, 900, 44100),
                xiph = mapOf("BPM" to listOf("128.5 BPM")),
                id3v2 = emptyMap(),
                mp4 = emptyMap(),
                cover = null,
            )
        val xiphTags = tagParser.parse(xiphMeta)
        assertEquals(128.5f, xiphTags.tempoBpm)
        assertEquals("Fast", bpmToTempoBucket(xiphTags.tempoBpm))

        val id3Meta =
            Metadata(
                properties = Properties("audio/mpeg", 180_000L, 320, 44100),
                xiph = emptyMap(),
                id3v2 = mapOf("TBPM" to listOf("Moderate")),
                mp4 = emptyMap(),
                cover = null,
            )
        val id3Tags = tagParser.parse(id3Meta)
        assertEquals("Moderate", bpmToTempoBucket(id3Tags.tempoBpm))
    }
}
