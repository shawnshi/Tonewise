/*
 * Copyright (c) 2026 Tonewise Project
 * QuickplaySnapshotCodecTest.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.home.quickplay

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class QuickplaySnapshotCodecTest {
    @Test
    fun serializeAndDeserialize_roundtripsHistoryNewestRediscovery() {
        val sections =
            QuickplaySections(
                history =
                    listOf(
                        QuickplayAlbumSummary(
                            albumUid = "alb-hist-1",
                            representativeSongUid = "song-1",
                            albumTitle = "Beethoven: \"Symphony No. 9\"",
                            artist = "Karajan \\ Berlin · 1963",
                            timestampMs = 1_732_500_000_000L,
                            playCount = 12,
                            coverId = "mcs:abcdef123456.jpg",
                        )
                    ),
                newlyAdded =
                    listOf(
                        QuickplayAlbumSummary(
                            albumUid = "alb-new-1",
                            representativeSongUid = "song-2",
                            albumTitle = "巴赫无伴奏小提琴组曲\nBWV 1004",
                            artist = "Nathan Milstein",
                            timestampMs = 1_732_600_000_000L,
                            playCount = 0,
                            coverId = "mcf:content://media/external/images/media/42",
                        )
                    ),
                rediscovery =
                    listOf(
                        QuickplayAlbumSummary(
                            albumUid = "alb-old-1",
                            representativeSongUid = "song-3",
                            albumTitle = "Kind of Blue",
                            artist = "Miles Davis",
                            timestampMs = 1_700_000_000_000L,
                            playCount = 7,
                        )
                    ),
            )

        val encoded = QuickplaySectionsCodec.serializeSections(sections)
        val decoded = QuickplaySectionsCodec.deserializeSections(encoded)

        assertFalse(decoded.isEmpty)
        assertEquals(sections, decoded)
    }

    @Test
    fun deserialize_handlesLegacyPayloadWithoutCoverId() {
        val legacyJson =
            """{"v":1,"history":[{"albumUid":"alb-1","representativeSongUid":"s-1","albumTitle":"Goldberg","artist":"Glenn Gould","timestampMs":1000,"playCount":3}],"newlyAdded":[],"rediscovery":[]}"""
        val decoded = QuickplaySectionsCodec.deserializeSections(legacyJson)
        assertEquals(1, decoded.history.size)
        assertEquals("", decoded.history.first().coverId)

        val legacyPinsJson =
            """[{"kind":"album","id":"alb-1","title":"Goldberg","subtitle":"Glenn Gould","pinnedAtMs":2000}]"""
        val decodedPins = QuickplayPinCodec.deserializePins(legacyPinsJson)
        assertEquals(1, decodedPins.size)
        assertEquals("", decodedPins.first().coverId)
    }

    @Test
    fun pinCodec_roundtripsCoverId() {
        val pins =
            listOf(
                QuickplayPinRecord(
                    kind = QuickplayPinKind.ALBUM,
                    id = "alb-1",
                    title = "Symphony No. 9",
                    subtitle = "Karajan",
                    pinnedAtMs = 12345L,
                    coverId = "mcs:deadbeef.webp",
                ),
                QuickplayPinRecord(
                    kind = QuickplayPinKind.WORK,
                    id = "work-1",
                    title = "BWV 1004",
                    subtitle = "Bach",
                    pinnedAtMs = 67890L,
                    coverId = "mcf:content://media/external/file/99",
                ),
            )
        val encoded = QuickplayPinCodec.serializePins(pins)
        val decoded = QuickplayPinCodec.deserializePins(encoded)
        assertEquals(pins, decoded)
    }

    @Test
    fun parseCachedCoverSpec_validatesStoredAndContentUrisSafely() {
        assertEquals(
            CachedCoverSpec.Stored("mcs:cover_01.jpg", "cover_01.jpg"),
            parseCachedCoverSpec("  mcs:cover_01.jpg  "),
        )
        assertEquals(
            CachedCoverSpec.ContentUri(
                "mcf:content://media/external/images/media/101",
                "content://media/external/images/media/101",
            ),
            parseCachedCoverSpec("mcf:content://media/external/images/media/101"),
        )
        assertNull(parseCachedCoverSpec(null))
        assertNull(parseCachedCoverSpec("   "))
        assertNull(parseCachedCoverSpec("mcs:"))
        assertNull(parseCachedCoverSpec("mcs:."))
        assertNull(parseCachedCoverSpec("mcs:.."))
        assertNull(parseCachedCoverSpec("mcs:../evil.jpg"))
        assertNull(parseCachedCoverSpec("mcs:sub/dir.jpg"))
        assertNull(parseCachedCoverSpec("mcs:sub\\dir.jpg"))
        assertNull(parseCachedCoverSpec("mcs:bad\u0000.jpg"))
        assertNull(parseCachedCoverSpec("mcf:file:///etc/passwd"))
        assertNull(parseCachedCoverSpec("mcf:content://"))
        assertNull(parseCachedCoverSpec("unknown:cover.jpg"))
    }

    @Test
    fun deserialize_handlesMalformedOrEmptyPayloadGracefully() {
        assertTrue(QuickplaySectionsCodec.deserializeSections(null).isEmpty)
        assertTrue(QuickplaySectionsCodec.deserializeSections("").isEmpty)
        assertTrue(QuickplaySectionsCodec.deserializeSections("[]").isEmpty)
        assertTrue(QuickplaySectionsCodec.deserializeSections("{\"history\":[{\"albumUid\":").isEmpty)
    }
}
