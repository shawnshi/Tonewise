/*
 * Copyright (c) 2026 Tonewise Project
 * LastFmScrobblerTest.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.sync

import java.security.MessageDigest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class LastFmScrobblerTest {
    @Test
    fun buildApiSignature_sortsUtf8KeysExcludesFormatAndComputesMd5() {
        val params =
            mapOf(
                "sk" to "session123",
                "api_key" to "mykey",
                "format" to "json",
                "callback" to "ignored",
                "track" to "II. Allegretto",
                "artist" to "Ludwig van Beethoven",
                "method" to "track.scrobble",
            )
        val secret = "mysecret"

        val actual = LastFmRepository.buildApiSignature(params, secret)

        // Expected canonical ordering: api_key, artist, method, sk, track (alphabetical) + secret
        val expectedRaw =
            "api_keymykey" +
                "artistLudwig van Beethoven" +
                "methodtrack.scrobble" +
                "sksession123" +
                "trackII. Allegretto" +
                "mysecret"
        val expectedMd5 =
            MessageDigest.getInstance("MD5")
                .digest(expectedRaw.toByteArray(Charsets.UTF_8))
                .joinToString("") { "%02x".format(it) }

        assertEquals(32, actual.length)
        assertEquals(expectedMd5, actual)
    }

    @Test
    fun upsertPendingLoveOp_deduplicatesByNormalizedTrackAndKeepsLatestState() {
        val ops = mutableListOf<PendingLoveOp>()
        val first =
            PendingLoveOp(
                songUid = "uid-1",
                artist = "Glenn Gould",
                track = "Aria",
                loved = true,
                updatedAtMs = 1_000L,
            )
        val second =
            PendingLoveOp(
                songUid = "uid-1",
                artist = "  glenn   gould ",
                track = "aria ",
                loved = false,
                updatedAtMs = 2_000L,
            )

        LastFmRepository.upsertPendingLoveOp(ops, first)
        LastFmRepository.upsertPendingLoveOp(ops, second)

        assertEquals(1, ops.size)
        assertFalse(ops[0].loved)
        assertEquals(2_000L, ops[0].updatedAtMs)
    }

    @Test
    fun pendingLoveOps_roundTripJsonSerialization() {
        val original =
            listOf(
                PendingLoveOp(
                    songUid = "uid-1",
                    artist = "Johann Sebastian Bach",
                    track = "Partita No. 2 \"Chaconne\" \\ BWV 1004",
                    loved = true,
                    updatedAtMs = 1_763_300_000_000L,
                ),
                PendingLoveOp(
                    songUid = "uid-2",
                    artist = "Bill Evans Trio",
                    track = "Waltz for Debby",
                    loved = false,
                    updatedAtMs = 1_763_300_005_000L,
                ),
            )

        val encoded = LastFmRepository.serializePendingLoveOps(original)
        val decoded = LastFmRepository.deserializePendingLoveOps(encoded)

        assertEquals(original, decoded)
        assertTrue(LastFmRepository.deserializePendingLoveOps(null).isEmpty())
    }

    @Test
    fun buildLoveRequestParams_signsTrackLoveAndTrackUnlove() {
        val loveParams =
            LastFmRepository.buildLoveRequestParams(
                apiKey = "key123",
                sharedSecret = "sec456",
                sessionKey = "sk789",
                artist = "Miles Davis",
                track = "Blue in Green",
                loved = true,
            )
        assertEquals("track.love", loveParams["method"])
        assertEquals("json", loveParams["format"])
        assertEquals(
            LastFmRepository.buildApiSignature(loveParams - "api_sig", "sec456"),
            loveParams["api_sig"],
        )

        val unloveParams =
            LastFmRepository.buildLoveRequestParams(
                apiKey = "key123",
                sharedSecret = "sec456",
                sessionKey = "sk789",
                artist = "Miles Davis",
                track = "Blue in Green",
                loved = false,
            )
        assertEquals("track.unlove", unloveParams["method"])
        assertEquals("json", unloveParams["format"])
        assertEquals(
            LastFmRepository.buildApiSignature(unloveParams - "api_sig", "sec456"),
            unloveParams["api_sig"],
        )
    }

    @Test
    fun resolveAutoEnabledApiFlag_enablesWhenValidSessionFirstConfiguredAndRespectsExplicitDisable() {
        assertTrue(
            LastFmRepository.resolveAutoEnabledApiFlag(
                requestedApiEnabled = false,
                hadValidSession = false,
                hasValidSession = true,
            )
        )
        assertFalse(
            LastFmRepository.resolveAutoEnabledApiFlag(
                requestedApiEnabled = false,
                hadValidSession = true,
                hasValidSession = true,
            )
        )
        assertTrue(
            LastFmRepository.resolveAutoEnabledApiFlag(
                requestedApiEnabled = true,
                hadValidSession = true,
                hasValidSession = true,
            )
        )
    }

    @Test
    fun shouldScrobbleTrack_enforces30SecondMinimum() {
        assertFalse(LastFmRepository.shouldScrobbleTrack(29_999L))
        assertTrue(LastFmRepository.shouldScrobbleTrack(30_000L))
        assertTrue(LastFmRepository.shouldScrobbleTrack(240_000L))
    }

    @Test
    fun parseTotalPages_extractsPositiveIntegerOrDefaultsToOne() {
        assertEquals(
            7,
            LastFmRepository.parseTotalPages(
                """{"recenttracks":{"@attr":{"totalPages":"7"}}}""",
                "recenttracks",
            ),
        )
        assertEquals(
            12,
            LastFmRepository.parseTotalPages(
                """{"lovedtracks":{"@attr":{"totalPages":12}}}""",
                "lovedtracks",
            ),
        )
        assertEquals(
            1,
            LastFmRepository.parseTotalPages("""{"recenttracks":{"@attr":{"totalPages":"0"}}}""", "recenttracks"),
        )
        assertEquals(1, LastFmRepository.parseTotalPages("not-json", "recenttracks"))
    }
}
