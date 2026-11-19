/*
 * Copyright (c) 2026 Tonewise Project
 * LanSyncV2Test.kt is part of Tonewise.
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

package com.shawnshi.tonewise.sync

import com.sun.net.httpserver.HttpServer
import java.io.File
import java.net.InetSocketAddress
import java.nio.file.Files
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicReference
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

class LanSyncV2Test {
    private fun withServer(configure: (HttpServer) -> Unit, block: (String) -> Unit) {
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        configure(server)
        server.start()
        try {
            val baseUrl = "http://127.0.0.1:${server.address.port}"
            block(baseUrl)
        } finally {
            server.stop(0)
        }
    }

    @Test
    fun pair_succeedsWithValidCode_andThrowsAuthExceptionOn401() {
        withServer(
            configure = { server ->
                server.createContext("/api/v2/pair") { ex ->
                    val req = ex.requestBody.readBytes().toString(Charsets.UTF_8)
                    if (req.contains("\"482913\"")) {
                        val resp =
                            """{"token":"tok_abc123","host_name":"Shawn-PC","device_id":"pixel9","device_name":"Pixel 9","paired_at_ms":1763251200000}"""
                                .toByteArray(Charsets.UTF_8)
                        ex.sendResponseHeaders(200, resp.size.toLong())
                        ex.responseBody.use { it.write(resp) }
                    } else {
                        val resp = """{"error":"invalid_pairing_code"}""".toByteArray(Charsets.UTF_8)
                        ex.sendResponseHeaders(401, resp.size.toLong())
                        ex.responseBody.use { it.write(resp) }
                    }
                }
            }
        ) { baseUrl ->
            assertThrows(LanSyncAuthException::class.java) {
                LanSyncV2Client.pair(baseUrl, "000000", "pixel9", "Pixel 9")
            }

            val pair = LanSyncV2Client.pair(baseUrl, "482913", "pixel9", "Pixel 9")
            assertEquals("tok_abc123", pair.token)
            assertEquals("Shawn-PC", pair.hostName)
            assertEquals("pixel9", pair.deviceId)
        }
    }

    @Test
    fun fetchOutbox_parsesAll5GranularitiesAndPlaylists() {
        val outboxJson =
            """
            {
              "host_name": "Shawn-PC",
              "generated_at_ms": 1763251200000,
              "total_bytes": 12000,
              "items": [
                {"id":"ob_1","device_id":"*","kind":"playlist","target_key":"Favs","display_name":"歌单：Favs","mode":"subscribe","track_uids":[],"created_at_ms":100},
                {"id":"ob_2","device_id":"*","kind":"genre","target_key":"Jazz","display_name":"流派：Jazz","mode":"subscribe","track_uids":[],"created_at_ms":101},
                {"id":"ob_3","device_id":"*","kind":"artist","target_key":"Bill Evans","display_name":"艺人：Bill Evans","mode":"subscribe","track_uids":[],"created_at_ms":102},
                {"id":"ob_4","device_id":"*","kind":"tracks","target_key":"u1","display_name":"Peace Piece","mode":"copy","track_uids":["u1"],"created_at_ms":103},
                {"id":"ob_5","device_id":"*","kind":"tracks","target_key":"batch:2","display_name":"2 selected","mode":"copy","track_uids":["u1","u2"],"created_at_ms":104}
              ],
              "tracks": [
                {"track_uid":"u1","rel_path":"Bill Evans/01.flac","target_rel_path":"Bill Evans/01.flac","title":"Peace Piece","artist":"Bill Evans","album":"Everybody Digs","album_artist":"Bill Evans","genre":"Jazz","duration_ms":380000,"file_size":8000,"mtime_ms":500,"has_cover":true,"source_outbox_ids":["ob_1","ob_2","ob_3","ob_4","ob_5"]},
                {"track_uid":"u2","rel_path":"Miles/02.flac","target_rel_path":"Miles/02.flac","title":"Blue in Green","artist":"Miles Davis","album":"Kind of Blue","album_artist":"Miles Davis","genre":"Jazz","duration_ms":327000,"file_size":4000,"mtime_ms":501,"has_cover":false,"source_outbox_ids":["ob_1","ob_2","ob_5"]}
              ],
              "playlists": [
                {"outbox_id":"ob_1","name":"Favs","rel_paths":["Bill Evans/01.flac","Miles/02.flac"],"track_uids":["u1","u2"]}
              ]
            }
            """
                .trimIndent()

        withServer(
            configure = { server ->
                server.createContext("/api/v2/outbox") { ex ->
                    val auth = ex.requestHeaders.getFirst("Authorization")
                    if (auth != "Bearer tok_ok") {
                        ex.sendResponseHeaders(401, -1)
                        ex.close()
                        return@createContext
                    }
                    val bytes = outboxJson.toByteArray(Charsets.UTF_8)
                    ex.sendResponseHeaders(200, bytes.size.toLong())
                    ex.responseBody.use { it.write(bytes) }
                }
            }
        ) { baseUrl ->
            assertThrows(LanSyncAuthException::class.java) {
                LanSyncV2Client.fetchOutbox(baseUrl, "wrong_tok")
            }

            val parsed = LanSyncV2Client.fetchOutbox(baseUrl, "tok_ok")
            assertEquals("Shawn-PC", parsed.hostName)
            assertEquals(12000L, parsed.totalBytes)
            assertEquals(5, parsed.items.size)
            assertEquals("subscribe", parsed.items[0].mode)
            assertEquals("copy", parsed.items[3].mode)
            assertEquals(2, parsed.tracks.size)
            assertEquals(5, parsed.tracks[0].sourceOutboxIds.size)
            assertTrue(parsed.tracks[0].hasCover)
            assertEquals(1, parsed.playlists.size)
            assertEquals(listOf("Bill Evans/01.flac", "Miles/02.flac"), parsed.playlists[0].relPaths)
        }
    }

    @Test
    fun downloadFileResumable_resumesFromPartialFileViaHttpRange206() {
        val original = ByteArray(65_536) { idx -> (idx % 251).toByte() }
        val firstCut = 24_576
        val attemptCounter = AtomicInteger(0)
        val observedRangeHeader = AtomicReference<String?>(null)
        val tempDir = Files.createTempDirectory("tw_range_test_").toFile()

        try {
            withServer(
                configure = { server ->
                    server.createContext("/api/v2/tracks/u1/file") { ex ->
                        val attempt = attemptCounter.incrementAndGet()
                        val range = ex.requestHeaders.getFirst("Range")
                        if (attempt == 1) {
                            // First attempt: send partial body chunked and close prematurely
                            ex.sendResponseHeaders(200, 0)
                            ex.responseBody.write(original, 0, firstCut)
                            ex.responseBody.flush()
                            ex.close()
                        } else {
                            observedRangeHeader.set(range)
                            val start =
                                range?.removePrefix("bytes=")?.substringBefore('-')?.toIntOrNull()
                                    ?: 0
                            val remaining = original.size - start
                            ex.responseHeaders.add(
                                "Content-Range",
                                "bytes $start-${original.size - 1}/${original.size}",
                            )
                            ex.sendResponseHeaders(206, remaining.toLong())
                            ex.responseBody.use { it.write(original, start, remaining) }
                        }
                    }
                }
            ) { baseUrl ->
                val partFile = File(tempDir, "u1.part")
                val fileUrl = LanSyncV2Client.buildTrackFileUrl(baseUrl, "u1")

                // Attempt 1 throws because transfer ended early, but .part preserves received bytes
                var failedAsExpected = false
                try {
                    LanSyncV2Client.downloadFileResumable(
                        url = fileUrl,
                        token = "tok_ok",
                        partFile = partFile,
                        expectedSize = original.size.toLong(),
                    )
                } catch (_: Exception) {
                    failedAsExpected = true
                }
                assertTrue(failedAsExpected)
                assertTrue(partFile.isFile)
                assertEquals(firstCut.toLong(), partFile.length())

                // Attempt 2 resumes from byte 24576 via HTTP 206 Partial Content
                val downloaded =
                    LanSyncV2Client.downloadFileResumable(
                        url = fileUrl,
                        token = "tok_ok",
                        partFile = partFile,
                        expectedSize = original.size.toLong(),
                    )
                assertEquals("bytes=$firstCut-", observedRangeHeader.get())
                assertEquals(original.size.toLong(), downloaded)
                assertArrayEquals(original, partFile.readBytes())
            }
        } finally {
            tempDir.deleteRecursively()
        }
    }

    @Test
    fun postReport_parsesEffectiveCountsAndFiltersOnlyAcknowledgedMutations() {
        val mOk = TagMutationRecord("local_1", "rating", "5", 5000L, "pixel")
        val mUnmatched = TagMutationRecord("local_missing", "dj_energy", "high", 5001L, "pixel")
        val batch = listOf(mOk, mUnmatched)
        val keys =
            mapOf(
                mOk to "local_1|rating|5000",
                mUnmatched to "local_missing|dj_energy|5001",
            )

        withServer(
            configure = { server ->
                server.createContext("/api/v2/report") { ex ->
                    val resp =
                        """
                        {
                          "ok": true,
                          "merged_tracks": 1,
                          "effective_counts": {"b3_uid_1": 19},
                          "loved_uids": ["b3_uid_1"],
                          "applied_mutations": 1,
                          "acknowledged_mutation_keys": ["local_1|rating|5000"],
                          "unmatched_mutation_uids": ["b3_missing"],
                          "completed_outbox_count": 1
                        }
                        """
                            .trimIndent()
                            .toByteArray(Charsets.UTF_8)
                    ex.sendResponseHeaders(200, resp.size.toLong())
                    ex.responseBody.use { it.write(resp) }
                }
            }
        ) { baseUrl ->
            val resp =
                LanSyncV2Client.postReport(
                    baseUrl = baseUrl,
                    token = "tok_ok",
                    deviceId = "pixel",
                    playDeltas =
                        listOf(
                            PlayDeltaPayload(
                                eventId = "ev1",
                                trackUid = "b3_uid_1",
                                artist = "Glenn Gould",
                                title = "Aria",
                                album = "Goldberg",
                                playCountDelta = 2,
                                lastPlayedTsMs = 1760000000000L,
                                deviceId = "pixel",
                            )
                        ),
                    mutations =
                        listOf(
                            MutationPayload("local_1|rating|5000", "b3_uid_1", "Glenn Gould", "Aria", "rating", "5", 5000L, "pixel"),
                            MutationPayload("local_missing|dj_energy|5001", "b3_missing", "", "", "dj_energy", "high", 5001L, "pixel"),
                        ),
                    completedOutboxIds = listOf("ob_4"),
                )

            assertTrue(resp.ok)
            assertEquals(19, resp.effectiveCounts["b3_uid_1"])
            assertEquals(listOf("b3_uid_1"), resp.lovedUids)

            val acked =
                LanSyncV2Client.filterAcknowledgedMutations(
                    batch = batch,
                    keyByRecord = keys,
                    ackKeys = resp.acknowledgedMutationKeys,
                )
            assertEquals(listOf(mOk), acked)
        }
    }

    @Test
    fun sanitizeRelativePathSegments_blocksTraversalSegments() {
        assertEquals(
            listOf("Artist", "Album", "01_Track.flac"),
            LanSyncV2Client.sanitizeRelativePathSegments("..\\..\\Artist/Album/01:Track.flac"),
        )
        assertEquals(
            listOf("secret.txt"),
            LanSyncV2Client.sanitizeRelativePathSegments("../../../secret.txt"),
        )
    }

    @Test
    fun parseOutboxResponse_extractsHashesAndDuplicateCount_andDeduplicatesTracks() {
        val json =
            """
            {
              "host_name": "DESKTOP-TW",
              "generated_at_ms": 1760000001000,
              "duplicate_track_count": 2,
              "items": [
                {"id": "ob_pl1", "device_id": "*", "kind": "playlist", "target_key": "P1", "display_name": "歌单：P1", "mode": "subscribe", "track_uids": [], "created_at_ms": 10},
                {"id": "ob_pl2", "device_id": "*", "kind": "playlist", "target_key": "P2", "display_name": "歌单：P2", "mode": "subscribe", "track_uids": [], "created_at_ms": 20}
              ],
              "tracks": [
                {
                  "track_uid": "uid_1",
                  "rel_path": "Artist/Album/01.flac",
                  "target_rel_path": "Artist/Album/01.flac",
                  "title": "Track 1",
                  "artist": "Artist",
                  "album": "Album",
                  "album_artist": "Artist",
                  "genre": "Jazz",
                  "duration_ms": 180000,
                  "file_size": 1000,
                  "mtime_ms": 5000,
                  "has_cover": true,
                  "source_outbox_ids": ["ob_pl1"],
                  "header_blake3": "hdr_a",
                  "audio_frame_blake3": "aud_a"
                },
                {
                  "track_uid": "UID_1",
                  "rel_path": "Artist/Album/01.flac",
                  "target_rel_path": "Artist/Album/01.flac",
                  "title": "Track 1",
                  "artist": "Artist",
                  "album": "Album",
                  "album_artist": "Artist",
                  "genre": "Jazz",
                  "duration_ms": 180000,
                  "file_size": 1000,
                  "mtime_ms": 5000,
                  "has_cover": true,
                  "source_outbox_ids": ["ob_pl2"],
                  "header_blake3": "hdr_a",
                  "audio_frame_blake3": "aud_a"
                },
                {
                  "track_uid": "uid_path_alias",
                  "rel_path": "Artist/Album/01.flac",
                  "target_rel_path": "Artist/Album/01.flac",
                  "title": "Track 1",
                  "artist": "Artist",
                  "album": "Album",
                  "album_artist": "Artist",
                  "genre": "Jazz",
                  "duration_ms": 180000,
                  "file_size": 1000,
                  "mtime_ms": 8000,
                  "has_cover": true,
                  "source_outbox_ids": ["ob_extra"],
                  "header_blake3": "hdr_a_v2",
                  "audio_frame_blake3": "aud_a"
                }
              ],
              "playlists": [],
              "total_bytes": 1000
            }
            """.trimIndent()

        val resp = LanSyncV2Client.parseOutboxResponse(json)
        assertEquals(2, resp.duplicateTrackCount)
        assertEquals("hdr_a", resp.tracks[0].headerBlake3)
        assertEquals("aud_a", resp.tracks[0].audioFrameBlake3)

        val (deduped, clientDups) = LanSyncV2Client.deduplicateOutboxTracks(resp.tracks)
        assertEquals(1, deduped.size)
        assertEquals(2, clientDups)
        assertEquals(listOf("ob_pl1", "ob_pl2", "ob_extra"), deduped[0].sourceOutboxIds)
        assertEquals("hdr_a_v2", deduped[0].headerBlake3)
    }

    @Test
    fun evaluateLocalTrackFreshness_detectsMissingUpToDateAndOutdatedVersions() {
        val remote =
            OutboxTrackDto(
                trackUid = "uid_fresh",
                relPath = "Bach/Aria.flac",
                targetRelPath = "Bach/Aria.flac",
                title = "Aria",
                artist = "Glenn Gould",
                album = "Goldberg",
                albumArtist = "Glenn Gould",
                genre = "Classical",
                durationMs = 180000,
                fileSize = 20480L,
                mtimeMs = 1760000010000L,
                hasCover = true,
                sourceOutboxIds = listOf("ob_1"),
                headerBlake3 = "hdr_v2",
                audioFrameBlake3 = "aud_same",
            )

        assertEquals(
            LocalSyncState.MISSING,
            LanSyncV2Client.evaluateLocalTrackFreshness(remote, exists = false, localFileSize = 0L, localMtimeMs = 0L),
        )

        assertEquals(
            LocalSyncState.UP_TO_DATE,
            LanSyncV2Client.evaluateLocalTrackFreshness(
                remote = remote,
                exists = true,
                localFileSize = 20480L,
                localMtimeMs = 1760000000000L,
                localHeaderBlake3 = "HDR_V2",
                localAudioFrameBlake3 = "AUD_SAME",
            ),
        )

        // Tag edit inside FLAC padding: fileSize identical, audio_frame identical, header_blake3 changed -> OUTDATED
        assertEquals(
            LocalSyncState.OUTDATED,
            LanSyncV2Client.evaluateLocalTrackFreshness(
                remote = remote,
                exists = true,
                localFileSize = 20480L,
                localMtimeMs = 1760000010000L,
                localHeaderBlake3 = "hdr_v1",
                localAudioFrameBlake3 = "aud_same",
            ),
        )

        // File size changed -> OUTDATED
        assertEquals(
            LocalSyncState.OUTDATED,
            LanSyncV2Client.evaluateLocalTrackFreshness(
                remote = remote,
                exists = true,
                localFileSize = 19999L,
                localMtimeMs = 1760000010000L,
                localHeaderBlake3 = "hdr_v2",
                localAudioFrameBlake3 = "aud_same",
            ),
        )

        // Without hashes, newer remote mtime (> 2s) -> OUTDATED; equal/older -> UP_TO_DATE
        val remoteNoHash = remote.copy(headerBlake3 = "", audioFrameBlake3 = "")
        assertEquals(
            LocalSyncState.OUTDATED,
            LanSyncV2Client.evaluateLocalTrackFreshness(
                remote = remoteNoHash,
                exists = true,
                localFileSize = 20480L,
                localMtimeMs = 1760000005000L,
            ),
        )
        assertEquals(
            LocalSyncState.UP_TO_DATE,
            LanSyncV2Client.evaluateLocalTrackFreshness(
                remote = remoteNoHash,
                exists = true,
                localFileSize = 20480L,
                localMtimeMs = 1760000009000L,
            ),
        )
    }

    @Test
    fun managedSyncStorage_defaultsAudioToMusicDirAndPrefersMusicSafLocation() {
        assertEquals(
            "Music/Bach/Goldberg/",
            ManagedSyncStorage.buildMediaStoreMusicRelativeDir(listOf("Bach", "Goldberg")),
        )
        assertEquals(
            "Music/",
            ManagedSyncStorage.buildMediaStoreMusicRelativeDir(emptyList()),
        )
        assertEquals(
            "Music/Tonewise/Bach/Goldberg/",
            ManagedSyncStorage.buildLegacyMediaStoreTonewiseRelativeDir(listOf("Bach", "Goldberg")),
        )
        assertEquals(1, ManagedSyncStorage.selectPreferredMusicSafIndex(listOf("Tonewise", "Music")))
        assertEquals(1, ManagedSyncStorage.selectPreferredMusicSafIndex(listOf("Tonewise", "Lossless")))
        assertEquals(0, ManagedSyncStorage.selectPreferredMusicSafIndex(listOf("Tonewise")))
        assertEquals(-1, ManagedSyncStorage.selectPreferredMusicSafIndex(emptyList()))
    }

    @Test
    fun cancelToken_abortsStalledDownloadQuickly_andPreservesPartFile() {
        val firstChunk = ByteArray(8_192) { idx -> (idx % 127).toByte() }
        val expectedTotal = 65_536L
        val releaseServer = java.util.concurrent.CountDownLatch(1)
        val tempDir = Files.createTempDirectory("tw_cancel_test_").toFile()

        try {
            withServer(
                configure = { server ->
                    server.createContext("/api/v2/tracks/u_stall/file") { ex ->
                        ex.sendResponseHeaders(200, expectedTotal)
                        runCatching {
                            ex.responseBody.write(firstChunk)
                            ex.responseBody.flush()
                            releaseServer.await(10, java.util.concurrent.TimeUnit.SECONDS)
                        }
                        runCatching { ex.close() }
                    }
                }
            ) { baseUrl ->
                val partFile = File(tempDir, "u_stall.part")
                val cancelToken = LanHttpClient.CancelToken()
                val canceller =
                    java.lang.Thread {
                        java.lang.Thread.sleep(150L)
                        cancelToken.cancel()
                    }
                canceller.start()

                val startNs = System.nanoTime()
                var caughtCancellation = false
                try {
                    LanSyncV2Client.downloadFileResumable(
                        url = LanSyncV2Client.buildTrackFileUrl(baseUrl, "u_stall"),
                        token = "tok_ok",
                        partFile = partFile,
                        expectedSize = expectedTotal,
                        cancelToken = cancelToken,
                    )
                } catch (_: kotlinx.coroutines.CancellationException) {
                    caughtCancellation = true
                } finally {
                    releaseServer.countDown()
                    canceller.join(2_000)
                }

                val elapsedMs = (System.nanoTime() - startNs) / 1_000_000L
                assertTrue("Expected CancellationException on cancel", caughtCancellation)
                assertTrue("Expected fast cancellation (<2000ms), took ${elapsedMs}ms", elapsedMs < 2_000L)
                assertTrue(partFile.isFile)
                assertEquals(firstChunk.size.toLong(), partFile.length())
            }
        } finally {
            releaseServer.countDown()
            tempDir.deleteRecursively()
        }
    }
}
