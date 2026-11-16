/*
 * Copyright (c) 2026 Tonewise Project
 * LastFmScrobblerTest.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.sync

import java.security.MessageDigest
import org.junit.Assert.assertEquals
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
}
