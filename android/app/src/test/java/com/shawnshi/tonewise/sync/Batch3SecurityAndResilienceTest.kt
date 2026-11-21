/*
 * Copyright (c) 2026 Tonewise Project
 * Batch3SecurityAndResilienceTest.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.sync

import com.sun.net.httpserver.HttpServer
import java.io.File
import java.io.IOException
import java.net.InetSocketAddress
import java.nio.file.Files
import java.security.GeneralSecurityException
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicReference
import javax.crypto.KeyGenerator
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

class Batch3SecurityAndResilienceTest {
    @Test
    fun resolveTokenForHost_matchesNormalizedHost_andBlocksCrossHostLeakage() {
        val paired =
            PairedHostInfo(
                baseUrl = "http://192.168.1.42:45820/",
                hostName = "Studio-PC",
                deviceId = "pixel-1",
                token = "tok_secret_123",
            )

        assertEquals(
            "tok_secret_123",
            LanSyncRepository.resolveTokenForHost(paired, "http://192.168.1.42:45820"),
        )
        assertEquals(
            "tok_secret_123",
            LanSyncRepository.resolveTokenForHost(paired, "  192.168.1.42:45820/ "),
        )
        assertNull(LanSyncRepository.resolveTokenForHost(paired, "http://192.168.1.99:45820"))
        assertNull(LanSyncRepository.resolveTokenForHost(paired, "http://192.168.1.42:45821"))
        assertNull(LanSyncRepository.resolveTokenForHost(paired, ""))
        assertNull(LanSyncRepository.resolveTokenForHost(null, "http://192.168.1.42:45820"))
    }

    @Test
    fun isSafeLanOrHttpsUrl_allowsPrivateLanAndHttps_blocksPublicCleartext() {
        assertTrue(LanHttpClient.isSafeLanOrHttpsUrl("http://192.168.1.10:45820/api/v2/outbox"))
        assertTrue(LanHttpClient.isSafeLanOrHttpsUrl("http://10.0.2.2:45820/api/v2/ping"))
        assertTrue(LanHttpClient.isSafeLanOrHttpsUrl("http://172.16.5.9:45820/api/v2/ping"))
        assertTrue(LanHttpClient.isSafeLanOrHttpsUrl("http://172.31.255.1:45820/api/v2/ping"))
        assertTrue(LanHttpClient.isSafeLanOrHttpsUrl("http://127.0.0.1:45820/api/v2/ping"))
        assertTrue(LanHttpClient.isSafeLanOrHttpsUrl("http://localhost:45820/api/v2/ping"))
        assertTrue(LanHttpClient.isSafeLanOrHttpsUrl("http://studio-pc.local:45820/api/v2/ping"))
        assertTrue(LanHttpClient.isSafeLanOrHttpsUrl("http://169.254.10.20:45820/api/v2/ping"))
        assertTrue(LanHttpClient.isSafeLanOrHttpsUrl("https://ws.audioscrobbler.com/2.0/"))

        assertFalse(LanHttpClient.isSafeLanOrHttpsUrl("http://8.8.8.8:45820/api/v2/outbox"))
        assertFalse(LanHttpClient.isSafeLanOrHttpsUrl("http://example.com/api/v2/outbox"))
        assertFalse(LanHttpClient.isSafeLanOrHttpsUrl("http://0.0.0.0:45820/api/v2/outbox"))
        assertFalse(LanHttpClient.isSafeLanOrHttpsUrl("http://172.32.0.1:45820/api/v2/outbox"))
        assertFalse(LanHttpClient.isSafeLanOrHttpsUrl("ftp://192.168.1.10/file.flac"))
        assertFalse(LanHttpClient.isSafeLanOrHttpsUrl("not a url"))
    }

    @Test
    fun httpClientAndV2Client_blockRedirects_parse429_andSendAuthenticatedUnpair() {
        val redirectTargetHit = AtomicBoolean(false)
        val unpairAuthHeader = AtomicReference<String?>(null)
        val unpairMethod = AtomicReference<String?>(null)

        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        try {
            server.createContext("/redirect") { ex ->
                ex.responseHeaders.add("Location", "/redirect-target")
                ex.sendResponseHeaders(302, -1)
                ex.close()
            }
            server.createContext("/redirect-target") { ex ->
                redirectTargetHit.set(true)
                val body = "should not reach".toByteArray()
                ex.sendResponseHeaders(200, body.size.toLong())
                ex.responseBody.use { it.write(body) }
            }
            server.createContext("/api/v2/pair") { ex ->
                val payload = """{"error":"pairing_locked_out","retry_after_seconds":42}""".toByteArray()
                ex.responseHeaders.add("Content-Type", "application/json")
                ex.sendResponseHeaders(429, payload.size.toLong())
                ex.responseBody.use { it.write(payload) }
            }
            server.createContext("/api/v2/unpair") { ex ->
                unpairMethod.set(ex.requestMethod)
                unpairAuthHeader.set(ex.requestHeaders.getFirst("Authorization"))
                val payload = """{"ok":true}""".toByteArray()
                ex.responseHeaders.add("Content-Type", "application/json")
                ex.sendResponseHeaders(200, payload.size.toLong())
                ex.responseBody.use { it.write(payload) }
            }
            server.start()

            val baseUrl = "http://127.0.0.1:${server.address.port}"

            // 1. 302 redirect is blocked without following
            assertThrows(IOException::class.java) {
                LanHttpClient.getString("$baseUrl/redirect", bearerToken = "bearer-abc")
            }
            assertFalse(redirectTargetHit.get())

            // 2. 429 JSON retry_after_seconds -> LanSyncRateLimitException(42)
            val rateEx =
                assertThrows(LanSyncRateLimitException::class.java) {
                    LanSyncV2Client.pair(baseUrl, "123456", "dev-1", "Pixel")
                }
            assertEquals(42L, rateEx.retryAfterSeconds)

            // 3. Authenticated unpair sends POST with Bearer token and returns true
            assertTrue(LanSyncV2Client.unpair(baseUrl, "bearer-abc"))
            assertEquals("POST", unpairMethod.get())
            assertEquals("Bearer bearer-abc", unpairAuthHeader.get())
        } finally {
            server.stop(0)
        }
    }

    @Test
    fun secretBox_roundTripsAesGcm_passesThroughLegacyPlaintext_andRejectsWrongKey() {
        val gen = KeyGenerator.getInstance("AES").apply { init(256) }
        val key1 = gen.generateKey()
        val key2 = gen.generateKey()

        val cipher1 = SecretBox.encryptWithKey("session_secret_value", key1)
        val cipher2 = SecretBox.encryptWithKey("session_secret_value", key1)
        assertTrue(cipher1.startsWith("enc:v1:"))
        assertNotEquals(cipher1, cipher2) // random IV per encryption
        assertEquals("session_secret_value", SecretBox.decryptWithKey(cipher1, key1))
        assertEquals("legacy_plaintext", SecretBox.decryptWithKey("legacy_plaintext", key1))
        assertEquals("", SecretBox.encryptWithKey("", key1))
        assertEquals("", SecretBox.decryptWithKey("", key1))

        assertThrows(GeneralSecurityException::class.java) {
            SecretBox.decryptWithKey(cipher1, key2)
        }

        // Headless JVM fallback path
        val fallbackCipher = SecretBox.encrypt("bearer_token_jvm")
        assertTrue(SecretBox.isEncrypted(fallbackCipher))
        assertEquals("bearer_token_jvm", SecretBox.decrypt(fallbackCipher))
        assertEquals("raw_legacy_token", SecretBox.decrypt("raw_legacy_token"))
    }

    @Test
    fun pruneStalePartFiles_deletesOnlyExpiredStagingFiles() {
        val tempRoot = Files.createTempDirectory("tw_part_gc").toFile()
        try {
            val nowMs = 1_763_400_000_000L
            val maxAgeMs = 7L * 24L * 60L * 60L * 1000L

            val stalePart = File(tempRoot, "stale.part").apply {
                writeText("partial")
                setLastModified(nowMs - maxAgeMs - 60_000L)
            }
            val staleTwTmp = File(tempRoot, ".twtmp_123_song.flac").apply {
                writeText("tmp")
                setLastModified(nowMs - maxAgeMs - 1_000L)
            }
            val staleMidTwTmp = File(tempRoot, "song.flac.twtmp.999.1").apply {
                writeText("tmp2")
                setLastModified(nowMs - maxAgeMs - 1_000L)
            }
            val staleTwBak = File(tempRoot, "song.flac.twbak.999.1").apply {
                writeText("bak")
                setLastModified(nowMs - maxAgeMs - 1_000L)
            }
            val freshPart = File(tempRoot, "fresh.part").apply {
                writeText("active")
                setLastModified(nowMs - 60_000L)
            }
            val normalFlac = File(tempRoot, "track.flac").apply {
                writeText("audio")
                setLastModified(nowMs - maxAgeMs - 86_400_000L)
            }

            val removed = LanHttpClient.pruneStalePartFiles(tempRoot, maxAgeMs = maxAgeMs, nowMs = nowMs)
            assertEquals(4, removed)
            assertFalse(stalePart.exists())
            assertFalse(staleTwTmp.exists())
            assertFalse(staleMidTwTmp.exists())
            assertFalse(staleTwBak.exists())
            assertTrue(freshPart.exists())
            assertTrue(normalFlac.exists())
        } finally {
            tempRoot.deleteRecursively()
        }
    }

    @Test
    fun lastFmClassifyErrorCode_dropsPermanentPoison_andRetriesTransientOrAuth() {
        assertEquals(LastFmRepository.SubmitOutcome.DROP_PERMANENT, LastFmRepository.classifyLastFmErrorCode(6))
        assertEquals(LastFmRepository.SubmitOutcome.DROP_PERMANENT, LastFmRepository.classifyLastFmErrorCode(7))

        for (code in listOf(9, 11, 13, 16, 29, -1)) {
            assertEquals(
                LastFmRepository.SubmitOutcome.RETRY_LATER,
                LastFmRepository.classifyLastFmErrorCode(code),
            )
        }
    }

    @Test
    fun tonewiseDspEngine_concurrentConfigResetAndProcess_noException() {
        val engine = com.shawnshi.tonewise.playback.dsp.TonewiseDspEngine()
        engine.configure(48_000, 2)
        val band =
            com.shawnshi.tonewise.playback.dsp.BiquadBand(
                freqHz = 1000.0,
                gainDb = 3.0,
                q = 1.41,
                enabled = true,
            )
        val cfgA =
            com.shawnshi.tonewise.playback.dsp.DspChainConfig(
                enabled = true,
                bands = listOf(band),
                crossfeedEnabled = true,
            )
        val cfgB =
            com.shawnshi.tonewise.playback.dsp.DspChainConfig(
                enabled = true,
                bands = listOf(band, band.copy(freqHz = 4000.0, gainDb = -2.5)),
                crossfeedEnabled = false,
            )

        val running = java.util.concurrent.atomic.AtomicBoolean(true)
        val failure = java.util.concurrent.atomic.AtomicReference<Throwable?>(null)
        val audioThread =
            Thread {
                val buf = DoubleArray(256)
                try {
                    while (running.get()) {
                        for (i in buf.indices) {
                            buf[i] = if ((i and 1) == 0) 0.25 else -0.25
                        }
                        engine.processInterleaved(buf, 128, -1.0)
                    }
                } catch (t: Throwable) {
                    failure.compareAndSet(null, t)
                }
            }
        val controlThread =
            Thread {
                try {
                    var step = 0
                    while (running.get()) {
                        engine.updateConfig(if ((step and 1) == 0) cfgA else cfgB)
                        if (step % 25 == 0) {
                            engine.resetState()
                        }
                        if (step % 60 == 0) {
                            engine.configure(if ((step and 2) == 0) 48_000 else 96_000, 2)
                        }
                        step++
                    }
                } catch (t: Throwable) {
                    failure.compareAndSet(null, t)
                }
            }

        audioThread.start()
        controlThread.start()
        Thread.sleep(250)
        running.set(false)
        audioThread.join(5_000)
        controlThread.join(5_000)

        assertEquals(null, failure.get())
    }
}
