/*
 * Copyright (c) 2026 Tonewise Project
 * Batch2ProtocolAndParityTest.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.sync

import android.net.Uri
import com.shawnshi.tonewise.detail.MusicBioRepositoryImpl
import com.shawnshi.tonewise.playback.dsp.BiquadBand
import com.shawnshi.tonewise.playback.dsp.DspChainConfig
import com.shawnshi.tonewise.playback.dsp.TonewiseDspEngine
import com.sun.net.httpserver.HttpServer
import java.io.File
import java.io.IOException
import java.net.InetSocketAddress
import java.nio.file.Files
import kotlin.math.abs
import kotlin.math.sin
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test
import org.oxycblt.musikr.Album
import org.oxycblt.musikr.Artist
import org.oxycblt.musikr.Genre
import org.oxycblt.musikr.Music
import org.oxycblt.musikr.Song
import org.oxycblt.musikr.core.TonewiseCore
import org.oxycblt.musikr.covers.Cover
import org.oxycblt.musikr.fs.Format
import org.oxycblt.musikr.fs.Path
import org.oxycblt.musikr.tag.Date
import org.oxycblt.musikr.tag.Disc
import org.oxycblt.musikr.tag.Name
import org.oxycblt.musikr.tag.ReplayGainAdjustment
import org.oxycblt.musikr.tag.interpret.Naming

class Batch2ProtocolAndParityTest {
    private fun findProtocolFile(relPath: String): File {
        var dir: File? = File(System.getProperty("user.dir") ?: ".").canonicalFile
        while (dir != null) {
            val candidate = File(dir, relPath)
            if (candidate.isFile) return candidate
            dir = dir.parentFile
        }
        error("Could not locate $relPath from ${System.getProperty("user.dir")}")
    }

    @Test
    fun sceneEvaluator_matchesProtocolSsot_andEmitsRatingZeroForUnratedSongs() {
        val ssotFile = findProtocolFile("protocol/dictionaries/default_scenes_5d.json")
        @Suppress("UNCHECKED_CAST")
        val ssotRoot = TonewiseCore.parseJson(ssotFile.readText(Charsets.UTF_8)) as Map<String, Any?>
        @Suppress("UNCHECKED_CAST")
        val localRoot = TonewiseCore.parseJson(SceneRuleEvaluator.DEFAULT_SCENES_JSON) as Map<String, Any?>

        val ssotScenes =
            (ssotRoot["scenes"] as List<*>).map { raw ->
                val m = raw as Map<*, *>
                m["id"].toString() to m["sort_arc"].toString()
            }
        val localScenes =
            (localRoot["scenes"] as List<*>).map { raw ->
                val m = raw as Map<*, *>
                m["id"].toString() to m["sort_arc"].toString()
            }
        assertEquals(6, ssotScenes.size)
        assertEquals(ssotScenes, localScenes)

        val unratedSong = DummySong(Music.UID.fromString("uas11111111-1111-4111-8111-111111111111")!!, "Nocturne")
        val tracksJson =
            SceneRuleEvaluator.buildTracksJson(
                songs = listOf(unratedSong),
                nowMs = 1_763_300_000_000L,
                getDjScenes = { emptyList() },
                getDjVocals = { "instrumental" },
                getDjEnergy = { "low" },
                getDjTempo = { "Slow" },
                getMoods = { emptyList() },
                getPlayStats = { Triple(0, 0L, false) },
                getRatingOverride = { null },
            )
        @Suppress("UNCHECKED_CAST")
        val tracks = TonewiseCore.parseJson(tracksJson) as List<Map<String, Any?>>
        assertEquals(1, tracks.size)
        assertEquals(0, (tracks[0]["rating"] as Number).toInt())
        assertEquals(120, (tracks[0]["unplayed_days"] as Number).toInt())
    }

    @Test
    fun lanSyncV2Client_reportSendsLwwTsMsAndParsesUnlovedUids() {
        var capturedBody = ""
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/api/v2/report") { ex ->
            capturedBody = ex.requestBody.readBytes().toString(Charsets.UTF_8)
            val resp =
                """
                {
                  "ok": true,
                  "merged_tracks": 0,
                  "effective_counts": {},
                  "loved_uids": ["${"a".repeat(64)}"],
                  "unloved_uids": ["${"b".repeat(64)}"],
                  "applied_mutations": 1,
                  "acknowledged_mutation_keys": ["k1"],
                  "unmatched_mutation_uids": [],
                  "completed_outbox_count": 0
                }
                """
                    .trimIndent()
                    .toByteArray(Charsets.UTF_8)
            ex.sendResponseHeaders(200, resp.size.toLong())
            ex.responseBody.use { it.write(resp) }
        }
        server.start()
        try {
            val resp =
                LanSyncV2Client.postReport(
                    baseUrl = "http://127.0.0.1:${server.address.port}",
                    token = "tok_b2",
                    deviceId = "pixel9",
                    playDeltas = emptyList(),
                    mutations =
                        listOf(
                            MutationPayload(
                                mutationKey = "k1",
                                trackUid = "b".repeat(64),
                                artist = "Artist",
                                title = "Track",
                                field = "tempo_bucket",
                                value = "Fast",
                                updatedAtMs = 9_876_543L,
                                deviceId = "pixel9",
                            )
                        ),
                    completedOutboxIds = emptyList(),
                )
            assertTrue(capturedBody.contains("\"lww_ts_ms\":9876543"))
            assertEquals(listOf("b".repeat(64)), resp.unlovedUids)
            assertEquals(listOf("a".repeat(64)), resp.lovedUids)
        } finally {
            server.stop(0)
        }
    }

    @Test
    fun musicBioRepository_rejectsJsonNullAndLiteralNullStrings() {
        val raw =
            """
            {
              "items": [
                {
                  "entity_type": "composer",
                  "display_name": "J.S. Bach",
                  "normalized_name": "j.s. bach",
                  "summary_zh": null,
                  "summary_en": "Baroque composer"
                },
                {
                  "entity_type": "artist",
                  "display_name": "Test Null",
                  "normalized_name": "test null",
                  "summary_zh": "null",
                  "summary_en": "NULL"
                }
              ]
            }
            """
                .trimIndent()
        val parsed = MusicBioRepositoryImpl.parseSyncedEncyclopediaEntries(raw)
        assertEquals(1, parsed.size)
        assertEquals("J.S. Bach", parsed[0].displayName)
        assertEquals(null, parsed[0].summaryZh)
        assertEquals("Baroque composer", parsed[0].summaryEn)
    }

    @Test
    fun dspEngine_apoChannelMaskIsolatesLeft_emptyRmsMatchesPreamp_andUpdatePreservesDelayLines() {
        val apo =
            """
            Preamp: -2.0 dB
            Channel: L
            Filter: ON PK Fc 1000 Hz Gain 6.0 dB Q 1.0
            Channel: R
            Filter: ON PK Fc 1000 Hz Gain -6.0 dB Q 1.0
            Channel: ALL
            Filter: ON PK Fc 200 Hz Gain 0.0 dB Q 1.0
            """.trimIndent()
        val cfg = TonewiseDspEngine.parseEqualizerApo(apo)
        assertEquals(3, cfg.bands.size)
        assertEquals(BiquadBand.CHANNEL_LEFT, cfg.bands[0].channelMask)
        assertEquals(BiquadBand.CHANNEL_RIGHT, cfg.bands[1].channelMask)
        assertEquals(BiquadBand.CHANNEL_ALL, cfg.bands[2].channelMask)

        // Left-only boost must alter L channel (relative to zero) while Right-only bypasses L filter
        val lOnlyCfg =
            DspChainConfig(
                enabled = true,
                preampDb = 0.0,
                bands =
                    listOf(
                        BiquadBand(
                            freqHz = 1000.0,
                            gainDb = 6.0,
                            q = 1.0,
                            enabled = true,
                            channelMask = BiquadBand.CHANNEL_LEFT,
                        )
                    ),
                limiterEnabled = false,
            )
        val engine = TonewiseDspEngine().apply {
            configure(48000, 2)
            updateConfig(lOnlyCfg)
        }
        val frames = 128
        val samples = DoubleArray(frames * 2)
        val origRight = DoubleArray(frames)
        val stage1Atten = TonewiseDspEngine.computeEffectivePreampDb(lOnlyCfg, 0.0)
        assertEquals(-6.0, stage1Atten, 1e-6)
        val stage1Linear = engine.computeStage1LinearGain(0.0)
        for (i in 0 until frames) {
            val v = 0.25 * sin(2.0 * Math.PI * 1000.0 * i / 48000.0)
            samples[i * 2] = v
            samples[i * 2 + 1] = v
            origRight[i] = v * stage1Linear // R should only see Stage 1 linear gain, no L peaking filter
        }
        engine.processInterleaved(samples, frames, 0.0)
        var maxRightDiff = 0.0
        var maxLeftVsRightDiff = 0.0
        for (i in 0 until frames) {
            maxRightDiff = maxOf(maxRightDiff, abs(samples[i * 2 + 1] - origRight[i]))
            maxLeftVsRightDiff = maxOf(maxLeftVsRightDiff, abs(samples[i * 2] - samples[i * 2 + 1]))
        }
        assertTrue("Right channel should remain flat within 1e-9, got $maxRightDiff", maxRightDiff < 1e-9)
        assertTrue("Left channel should diverge from Right via +6 dB PK, got $maxLeftVsRightDiff", maxLeftVsRightDiff > 0.05)

        // Empty-filter estimateRmsLoudnessShiftDb uses computeEffectivePreampDb(config, 0.0)
        val flatWithBoostAndPreamp =
            DspChainConfig(
                enabled = true,
                preampDb = -4.0,
                bands = listOf(BiquadBand(freqHz = 100.0, gainDb = 0.0, enabled = true)),
            )
        assertEquals(-4.0, TonewiseDspEngine.estimateRmsLoudnessShiftDb(flatWithBoostAndPreamp, 48000), 1e-6)

        // Parameter change (cutoff/balance) preserving delay geometry keeps limiter/crossfeed buffers hot
        val crossCfg =
            DspChainConfig(
                enabled = true,
                crossfeedEnabled = true,
                crossfeedCutoffHz = 700.0,
                crossfeedFeedDb = 4.5,
                crossfeedDelayMs = 0.28,
                limiterEnabled = true,
            )
        val contEngine = TonewiseDspEngine().apply {
            configure(48000, 2)
            updateConfig(crossCfg)
        }
        val warmFrames = 256
        val warmBuf = DoubleArray(warmFrames * 2) { i -> 0.5 * sin(2.0 * Math.PI * 330.0 * (i / 2) / 48000.0) }
        contEngine.processInterleaved(warmBuf, warmFrames, 0.0)

        contEngine.updateConfig(crossCfg.copy(crossfeedCutoffHz = 550.0, balanceDb = 1.5))
        val nextFrames = 16
        val nextBuf = DoubleArray(nextFrames * 2) { i -> 0.5 * sin(2.0 * Math.PI * 330.0 * (warmFrames + i / 2) / 48000.0) }
        contEngine.processInterleaved(nextBuf, nextFrames, 0.0)
        for (f in 0 until nextFrames) {
            assertTrue("Frame $f dropped to zero after live DSP update", abs(nextBuf[f * 2]) > 1e-4)
        }
    }

    @Test
    fun lanHttpClient_downloadFileToTempAndReplace_isAtomicOnHttp200AndSafeOnHttp500() {
        val tempDir = Files.createTempDirectory("tw_b2_dl_").toFile()
        var failRequest = false
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/api/v1/waveforms") { ex ->
            if (failRequest) {
                ex.sendResponseHeaders(500, -1)
                ex.close()
            } else {
                val payload = "NEW_WAVEFORM_PAYLOAD_V2".toByteArray(Charsets.UTF_8)
                ex.sendResponseHeaders(200, payload.size.toLong())
                ex.responseBody.use { it.write(payload) }
            }
        }
        server.start()
        try {
            val target = File(tempDir, ".tonewise/waveforms.bin")
            target.parentFile.mkdirs()
            target.writeText("ORIGINAL_WAVEFORM_DATA", Charsets.UTF_8)

            // 1. Failed HTTP 500 must leave original file untouched and no lingering .tmp
            failRequest = true
            assertThrows(IOException::class.java) {
                LanHttpClient.downloadFileToTempAndReplace(
                    url = "http://127.0.0.1:${server.address.port}/api/v1/waveforms",
                    bearerToken = "tok",
                    targetFile = target,
                )
            }
            assertEquals("ORIGINAL_WAVEFORM_DATA", target.readText(Charsets.UTF_8))
            assertFalse(File(target.parentFile, "${target.name}.tmp").exists())

            // 2. Successful HTTP 200 replaces target atomically and cleans up .tmp
            failRequest = false
            val bytes =
                LanHttpClient.downloadFileToTempAndReplace(
                    url = "http://127.0.0.1:${server.address.port}/api/v1/waveforms",
                    bearerToken = "tok",
                    targetFile = target,
                )
            assertEquals("NEW_WAVEFORM_PAYLOAD_V2".length.toLong(), bytes)
            assertEquals("NEW_WAVEFORM_PAYLOAD_V2", target.readText(Charsets.UTF_8))
            assertFalse(File(target.parentFile, "${target.name}.tmp").exists())
        } finally {
            server.stop(0)
            tempDir.deleteRecursively()
        }
    }

    private class DummySong(override val uid: Music.UID, title: String) : Song {
        override val name: Name.Known = Naming.simple().name(title, null)
        override val track: Int? = null
        override val disc: Disc? = null
        override val date: Date? = null
        override val uri: Uri
            get() = error("unused")
        override val path: Path
            get() = error("unused")
        override val format: Format
            get() = error("unused")
        override val size: Long = 0L
        override val durationMs: Long = 180_000L
        override val bitrateKbps: Int = 900
        override val sampleRateHz: Int = 44100
        override val replayGainAdjustment: ReplayGainAdjustment = ReplayGainAdjustment(null, null)
        override val modifiedMs: Long = 0L
        override val addedMs: Long = 0L
        override val cover: Cover? = null
        override val album: Album
            get() = error("unused")
        override val artists: List<Artist> = emptyList()
        override val genres: List<Genre> = emptyList()
        override val composers: List<String> = emptyList()
        override val conductors: List<String> = emptyList()
    }
}
