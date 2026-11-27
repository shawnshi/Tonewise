/*
 * Copyright (c) 2026 Tonewise Project
 * LastFmAuthCallbackTest.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.sync

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class LastFmAuthCallbackTest {
    @Test
    fun isLastFmCallback_matchesApplicationSchemeOnly_andRejectsAudioContentUris() {
        assertTrue(
            LastFmAuthCallbackParser.isLastFmCallback(
                scheme = "com.shawnshi.tonewise.debug",
                host = "lastfm-auth",
                expectedScheme = "com.shawnshi.tonewise.debug",
            )
        )
        assertTrue(
            LastFmAuthCallbackParser.isLastFmCallback(
                scheme = "com.shawnshi.tonewise",
                host = "lastfm-auth",
                expectedScheme = "com.shawnshi.tonewise",
            )
        )
        // Negative assertions: audio URIs & cross-variant schemes must NOT match
        assertFalse(
            LastFmAuthCallbackParser.isLastFmCallback(
                scheme = "content",
                host = "media",
                expectedScheme = "com.shawnshi.tonewise",
            )
        )
        assertFalse(
            LastFmAuthCallbackParser.isLastFmCallback(
                scheme = "file",
                host = "",
                expectedScheme = "com.shawnshi.tonewise",
            )
        )
        assertFalse(
            LastFmAuthCallbackParser.isLastFmCallback(
                scheme = "com.shawnshi.tonewise",
                host = "lastfm-auth",
                expectedScheme = "com.shawnshi.tonewise.debug",
            )
        )
    }

    @Test
    fun extractCallbackToken_parsesUrlEncodedTokenAndIgnoresMissing() {
        assertEquals(
            "tok_abc123",
            LastFmAuthCallbackParser.extractCallbackToken("foo=1&token=tok_abc123&bar=2"),
        )
        assertEquals(
            "tok+123",
            LastFmAuthCallbackParser.extractCallbackToken("token=tok%2B123"),
        )
        assertNull(LastFmAuthCallbackParser.extractCallbackToken("token=&other=1"))
        assertNull(LastFmAuthCallbackParser.extractCallbackToken(null))
    }

    @Test
    fun classifyAuthSessionErrorCode_keepsPendingOnError14_andExpiresOnError4Or15() {
        assertEquals(
            LastFmAuthExchangeOutcome.PENDING_UNCONFIRMED,
            LastFmAuthCallbackParser.classifyAuthSessionErrorCode(14),
        )
        assertEquals(
            LastFmAuthExchangeOutcome.INVALID_OR_EXPIRED,
            LastFmAuthCallbackParser.classifyAuthSessionErrorCode(4),
        )
        assertEquals(
            LastFmAuthExchangeOutcome.INVALID_OR_EXPIRED,
            LastFmAuthCallbackParser.classifyAuthSessionErrorCode(15),
        )
        assertEquals(
            LastFmAuthExchangeOutcome.TRANSIENT_ERROR,
            LastFmAuthCallbackParser.classifyAuthSessionErrorCode(8),
        )
    }

    @Test
    fun lastFmState_hasValidApiSession_supportsBuiltInAndCustomCredentials() {
        val builtInConnected =
            LastFmState(
                apiKey = "0123456789abcdef0123456789abcdef",
                sharedSecret = "fedcba9876543210fedcba9876543210",
                sessionKey = "sk-xyz",
                hasBuiltInCredentials = true,
                hasCustomCredentials = false,
            )
        assertTrue(builtInConnected.hasValidApiSession)

        val missingSession = builtInConnected.copy(sessionKey = "")
        assertFalse(missingSession.hasValidApiSession)
    }
}
