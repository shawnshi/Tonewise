/*
 * Copyright (c) 2026 Tonewise Project
 * UsbSyncAndPruneTest.kt is part of Tonewise.
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
import java.io.IOException
import java.net.InetSocketAddress
import java.nio.file.Files
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicReference
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

class UsbSyncAndPruneTest {
    private fun withServer(configure: (HttpServer) -> Unit, block: (String) -> Unit) {
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        configure(server)
        server.start()
        try {
            block("http://127.0.0.1:${server.address.port}")
        } finally {
            server.stop(0)
        }
    }

    @Test
    fun isLoopbackUrl_identifiesLoopbackAddresses_andRejectsLanOrPublic() {
        assertTrue(LanHttpClient.isLoopbackUrl("http://127.0.0.1:45820/api/v2/ping"))
        assertTrue(LanHttpClient.isLoopbackUrl("http://localhost:45820/api/v2/outbox"))
        assertTrue(LanHttpClient.isLoopbackUrl("http://[::1]:45820/api/v2/ping"))
        assertTrue(LanHttpClient.isLoopbackUrl("http://127.0.1.1:45820"))

        assertFalse(LanHttpClient.isLoopbackUrl("http://192.168.1.10:45820/api/v2/ping"))
        assertFalse(LanHttpClient.isLoopbackUrl("https://example.com/api/v2/ping"))
        assertFalse(LanHttpClient.isLoopbackUrl("not a url"))
    }

    @Test
    fun resolveTokenForHost_supportsCrossTransportViaHostId_andBlocksMismatch() {
        val pairedWithId =
            PairedHostInfo(
                baseUrl = "http://192.168.1.42:45820",
                hostName = "Studio-PC",
                deviceId = "pixel9",
                token = "tok_shared_123",
                hostId = "uuid-host-1",
            )
        val pairedWithoutId = pairedWithId.copy(hostId = "")

        // Exact URL match still works with 2-arg call
        assertEquals(
            "tok_shared_123",
            LanSyncRepository.resolveTokenForHost(pairedWithId, "http://192.168.1.42:45820"),
        )
        // Cross-transport USB loopback matches when verifiedHostId equals paired.hostId
        assertEquals(
            "tok_shared_123",
            LanSyncRepository.resolveTokenForHost(
                pairedWithId,
                "http://127.0.0.1:45820",
                verifiedHostId = "UUID-HOST-1",
            ),
        )
        // Mismatched hostId, missing hostId, or unsafe public URL are rejected
        assertNull(
            LanSyncRepository.resolveTokenForHost(
                pairedWithId,
                "http://127.0.0.1:45820",
                verifiedHostId = "uuid-host-other",
            )
        )
        assertNull(
            LanSyncRepository.resolveTokenForHost(
                pairedWithoutId,
                "http://127.0.0.1:45820",
                verifiedHostId = "uuid-host-1",
            )
        )
        assertNull(
            LanSyncRepository.resolveTokenForHost(
                pairedWithId,
                "http://8.8.8.8:45820",
                verifiedHostId = "uuid-host-1",
            )
        )
    }

    @Test
    fun v2Client_pingInfo_pairViaUsb_parseOutboxPolicy_andPostSyncProgress() {
        val observedProgressAuth = AtomicReference<String?>(null)
        val observedProgressBody = AtomicReference<String?>(null)

        assertThrows(IOException::class.java) {
            LanSyncV2Client.pairViaUsb("http://192.168.1.10:45820", "n1", "dev1", "Pixel")
        }

        withServer(
            configure = { server ->
                server.createContext("/api/v2/ping") { ex ->
                    val resp =
                        """{"schema_version":2,"host_name":"Studio-PC","host_id":"host-uuid-99","port":45820,"requires_pairing":true,"transport_hint":"usb_loopback"}"""
                            .toByteArray(Charsets.UTF_8)
                    ex.sendResponseHeaders(200, resp.size.toLong())
                    ex.responseBody.use { it.write(resp) }
                }
                server.createContext("/api/v2/usb/pair") { ex ->
                    val req = ex.requestBody.readBytes().toString(Charsets.UTF_8)
                    if (req.contains("\"usb-nonce-ok\"")) {
                        val resp =
                            """{"token":"tok_usb_777","host_name":"Studio-PC","host_id":"host-uuid-99","device_id":"pixel9","device_name":"Pixel 9","paired_at_ms":1763251200000}"""
                                .toByteArray(Charsets.UTF_8)
                        ex.sendResponseHeaders(200, resp.size.toLong())
                        ex.responseBody.use { it.write(resp) }
                    } else {
                        ex.sendResponseHeaders(401, -1)
                        ex.close()
                    }
                }
                server.createContext("/api/v2/sync-session/progress") { ex ->
                    observedProgressAuth.set(ex.requestHeaders.getFirst("Authorization"))
                    observedProgressBody.set(ex.requestBody.readBytes().toString(Charsets.UTF_8))
                    val resp =
                        """{"ok":true,"cancel_requested":true,"prune_confirmed":true}"""
                            .toByteArray(Charsets.UTF_8)
                    ex.sendResponseHeaders(200, resp.size.toLong())
                    ex.responseBody.use { it.write(resp) }
                }
            }
        ) { baseUrl ->
            val ping = LanSyncV2Client.pingInfo(baseUrl)
            assertTrue(ping.ok)
            assertEquals("Studio-PC", ping.hostName)
            assertEquals("host-uuid-99", ping.hostId)
            assertEquals("usb_loopback", ping.transportHint)

            val pair = LanSyncV2Client.pairViaUsb(baseUrl, "usb-nonce-ok", "pixel9", "Pixel 9")
            assertEquals("tok_usb_777", pair.token)
            assertEquals("host-uuid-99", pair.hostId)

            val ctrl =
                LanSyncV2Client.postSyncProgress(
                    baseUrl = baseUrl,
                    token = pair.token,
                    payload =
                        SyncSessionProgressPayload(
                            deviceId = "pixel9",
                            transport = "usb",
                            phase = "downloading",
                            isRunning = true,
                            completedTracks = 1,
                            totalTracks = 3,
                            blockedForeignPaths = listOf("Old/Song.flac"),
                        ),
                )
            assertTrue(ctrl.ok)
            assertTrue(ctrl.cancelRequested)
            assertTrue(ctrl.pruneConfirmed)
            assertEquals("Bearer tok_usb_777", observedProgressAuth.get())
            assertTrue(observedProgressBody.get().orEmpty().contains("\"Old/Song.flac\""))
        }

        val outboxWithPolicyJson =
            """
            {
              "host_name": "Studio-PC",
              "generated_at_ms": 1763251200000,
              "total_bytes": 1000,
              "items": [],
              "tracks": [],
              "playlists": [],
              "sync_policy": {
                "mirror_prune_enabled": true,
                "prune_scope": "music_folder_all",
                "target_root": "Music",
                "outbox_authoritative": true,
                "missing_source_count": 0,
                "expected_playlist_names": ["Favorites"],
                "large_prune_confirm_threshold_count": 20,
                "large_prune_confirm_threshold_ratio": 0.30,
                "prune_confirmed_for_session": true
              }
            }
            """
                .trimIndent()
        val parsed = LanSyncV2Client.parseOutboxResponse(outboxWithPolicyJson)
        val policy = parsed.syncPolicy!!
        assertTrue(policy.mirrorPruneEnabled)
        assertEquals("music_folder_all", policy.pruneScope)
        assertEquals("Music", policy.targetRoot)
        assertTrue(policy.outboxAuthoritative)
        assertEquals(0, policy.missingSourceCount)
        assertEquals(listOf("Favorites"), policy.expectedPlaylistNames)
        assertEquals(20, policy.largePruneConfirmThresholdCount)
        assertEquals(0.30, policy.largePruneConfirmThresholdRatio, 0.001)
        assertTrue(policy.pruneConfirmedForSession)
    }

    @Test
    fun syncMirrorPrunePlanner_enforcesAllSafetyFusesAndScopes() {
        val localFiles =
            listOf(
                LocalMusicAudioFile("Keep/01.flac", "content://1", 100L),
                LocalMusicAudioFile("ManagedOld/02.flac", "content://2", 100L),
                LocalMusicAudioFile("ForeignOld/03.mp3", "content://3", 100L),
            )
        val desired = listOf("Keep/01.flac")
        val managed = setOf("Keep/01.flac", "ManagedOld/02.flac")
        val basePolicy =
            SyncPolicyDto(
                mirrorPruneEnabled = true,
                pruneScope = "music_folder_all",
                targetRoot = "Music",
                outboxAuthoritative = true,
                missingSourceCount = 0,
                expectedPlaylistNames = listOf("Mix"),
                largePruneConfirmThresholdCount = 20,
                largePruneConfirmThresholdRatio = 0.30,
                pruneConfirmedForSession = false,
            )

        // Fuse 1: Disabled
        assertEquals(
            PruneDecision.SKIP_DISABLED,
            SyncMirrorPrunePlanner.planPrune(
                basePolicy.copy(mirrorPruneEnabled = false),
                desired,
                localFiles,
                managed,
                syncCompletedCleanly = true,
            ).decision,
        )

        // Fuse 2: Incomplete sync / cancelled
        assertEquals(
            PruneDecision.SKIP_INCOMPLETE_SYNC,
            SyncMirrorPrunePlanner.planPrune(
                basePolicy,
                desired,
                localFiles,
                managed,
                syncCompletedCleanly = false,
            ).decision,
        )

        // Fuse 3: Empty desired list / unauthoritative / missing source
        assertEquals(
            PruneDecision.SKIP_EMPTY_OR_UNAUTHORITATIVE,
            SyncMirrorPrunePlanner.planPrune(
                basePolicy,
                emptyList(),
                localFiles,
                managed,
                syncCompletedCleanly = true,
            ).decision,
        )
        assertEquals(
            PruneDecision.SKIP_EMPTY_OR_UNAUTHORITATIVE,
            SyncMirrorPrunePlanner.planPrune(
                basePolicy.copy(outboxAuthoritative = false),
                desired,
                localFiles,
                managed,
                syncCompletedCleanly = true,
            ).decision,
        )
        assertEquals(
            PruneDecision.SKIP_EMPTY_OR_UNAUTHORITATIVE,
            SyncMirrorPrunePlanner.planPrune(
                basePolicy.copy(missingSourceCount = 1),
                desired,
                localFiles,
                managed,
                syncCompletedCleanly = true,
            ).decision,
        )

        // Scope: music_folder_all removes both ManagedOld and ForeignOld
        val allScopePlan =
            SyncMirrorPrunePlanner.planPrune(
                basePolicy,
                desired,
                localFiles,
                managed,
                syncCompletedCleanly = true,
            )
        assertEquals(PruneDecision.PROCEED, allScopePlan.decision)
        assertEquals(
            listOf("ManagedOld/02.flac", "ForeignOld/03.mp3"),
            allScopePlan.filesToDelete.map { it.relativePath },
        )

        // Scope: managed_only leaves ForeignOld untouched
        val managedScopePlan =
            SyncMirrorPrunePlanner.planPrune(
                basePolicy.copy(pruneScope = "managed_only"),
                desired,
                localFiles,
                managed,
                syncCompletedCleanly = true,
            )
        assertEquals(PruneDecision.PROCEED, managedScopePlan.decision)
        assertEquals(
            listOf("ManagedOld/02.flac"),
            managedScopePlan.filesToDelete.map { it.relativePath },
        )

        // Fuse 4: Large prune threshold confirmation (including exact boundary >= 20 && ratio >= 0.30)
        val manyLocal =
            (1..30).map { i -> LocalMusicAudioFile("Keep/$i.flac", "content://k$i", 10L) } +
                (1..20).map { i -> LocalMusicAudioFile("Extra/$i.flac", "content://e$i", 10L) }
        val manyDesired = (1..30).map { i -> "Keep/$i.flac" }

        val confirmPlan =
            SyncMirrorPrunePlanner.planPrune(
                basePolicy,
                manyDesired,
                manyLocal,
                emptySet(),
                syncCompletedCleanly = true,
            )
        assertEquals(PruneDecision.REQUIRE_LARGE_PRUNE_CONFIRMATION, confirmPlan.decision)
        assertEquals(20, confirmPlan.filesToDelete.size)

        // Confirmed session proceeds
        val confirmedPlan =
            SyncMirrorPrunePlanner.planPrune(
                basePolicy.copy(pruneConfirmedForSession = true),
                manyDesired,
                manyLocal,
                emptySet(),
                syncCompletedCleanly = true,
            )
        assertEquals(PruneDecision.PROCEED, confirmedPlan.decision)
        assertEquals(20, confirmedPlan.filesToDelete.size)
    }

    @Test
    fun managedSyncStorage_isAudioFilename_filtersAudioExtensionsOnly() {
        assertTrue(ManagedSyncStorage.isAudioFilename("01.flac"))
        assertTrue(ManagedSyncStorage.isAudioFilename("Track.MP3"))
        assertTrue(ManagedSyncStorage.isAudioFilename("Sonata.m4a"))
        assertTrue(ManagedSyncStorage.isAudioFilename("Master.dsf"))
        assertTrue(ManagedSyncStorage.isAudioFilename("Master.dff"))
        assertTrue(ManagedSyncStorage.isAudioFilename("Live.opus"))
        assertTrue(ManagedSyncStorage.isAudioFilename("Test.wav"))

        assertFalse(ManagedSyncStorage.isAudioFilename("cover.jpg"))
        assertFalse(ManagedSyncStorage.isAudioFilename("booklet.pdf"))
        assertFalse(ManagedSyncStorage.isAudioFilename("Favorites.m3u8"))
        assertFalse(ManagedSyncStorage.isAudioFilename("README"))
    }

    @Test
    fun isPrimaryMusicTreeDocumentId_acceptsPrimaryMusicOnly() {
        assertTrue(ManagedSyncStorage.isPrimaryMusicTreeDocumentId("primary:Music"))
        assertTrue(ManagedSyncStorage.isPrimaryMusicTreeDocumentId("primary:music/"))
        assertTrue(ManagedSyncStorage.isPrimaryMusicTreeDocumentId("PRIMARY:Music\\"))

        assertFalse(ManagedSyncStorage.isPrimaryMusicTreeDocumentId("primary:Music/Tonewise"))
        assertFalse(ManagedSyncStorage.isPrimaryMusicTreeDocumentId("1A2B-3C4D:Music"))
        assertFalse(ManagedSyncStorage.isPrimaryMusicTreeDocumentId("primary:Documents"))
        assertFalse(ManagedSyncStorage.isPrimaryMusicTreeDocumentId(""))
        assertFalse(ManagedSyncStorage.isPrimaryMusicTreeDocumentId(null))
    }

    @Test
    fun requestUsbFastPush_streamsNdjsonProgressAndComplete_andFallsBackOn501AndHandles409Cancel() {
        val mode = AtomicInteger(0)
        withServer(
            configure = { server ->
                server.createContext("/api/v2/usb/push-tracks") { ex ->
                    ex.requestBody.readBytes()
                    when (mode.get()) {
                        0 -> {
                            val ndjson =
                                """
                                {"event":"progress","stage":"pushing","completed_tracks":1,"total_tracks":2,"bytes_transferred":1024,"total_bytes":2048,"speed_mbps":38.5,"current_rel_path":"Artist/01.flac"}
                                {"event":"complete","used_direct_push":true,"pushed_tracks":[{"track_uid":"uid_ok","rel_path":"Artist/01.flac","file_size":1024}],"failed_track_uids":["uid_fail"],"covers_pushed":1,"covers_skipped":0,"total_bytes_transferred":1024,"elapsed_seconds":0.25,"average_mbps":39.1,"error":null}
                                """.trimIndent() + "\n"
                            val bytes = ndjson.toByteArray(Charsets.UTF_8)
                            ex.responseHeaders.add("Content-Type", "application/x-ndjson; charset=utf-8")
                            ex.sendResponseHeaders(200, bytes.size.toLong())
                            ex.responseBody.use { it.write(bytes) }
                        }
                        1 -> {
                            val bytes = """{"error":"usb_direct_push_unavailable"}""".toByteArray()
                            ex.sendResponseHeaders(501, bytes.size.toLong())
                            ex.responseBody.use { it.write(bytes) }
                        }
                        2 -> {
                            val bytes = """{"error":"sync_cancelled"}""".toByteArray()
                            ex.sendResponseHeaders(409, bytes.size.toLong())
                            ex.responseBody.use { it.write(bytes) }
                        }
                        else -> {
                            val ndjson =
                                """{"event":"complete","used_direct_push":false,"pushed_tracks":[],"failed_track_uids":["uid_ok"],"covers_pushed":0,"covers_skipped":0,"total_bytes_transferred":0,"elapsed_seconds":0.0,"average_mbps":0.0,"error":"sync_cancelled"}""" +
                                    "\n"
                            val bytes = ndjson.toByteArray(Charsets.UTF_8)
                            ex.sendResponseHeaders(200, bytes.size.toLong())
                            ex.responseBody.use { it.write(bytes) }
                        }
                    }
                }
            },
        ) { baseUrl ->
            val tracks = listOf(UsbFastPushTrackItem("uid_ok", "Artist/01.flac", 1024L, 100L))
            val covers = listOf(UsbFastPushCoverItem("Artist", "uid_ok"))
            val events = ArrayList<UsbFastPushProgressEvent>()

            mode.set(0)
            val ok =
                LanSyncV2Client.requestUsbFastPush(
                    baseUrl = baseUrl,
                    token = "tok_test",
                    tracks = tracks,
                    covers = covers,
                    onProgress = { events.add(it) },
                )
            assertTrue(ok.usedDirectPush)
            assertFalse(ok.cancelledByHost)
            assertEquals(1, events.size)
            assertEquals(38.5, events[0].speedMbps, 0.01)
            assertEquals(1, ok.pushedTracks.size)
            assertEquals("uid_ok", ok.pushedTracks[0].trackUid)
            assertEquals(listOf("uid_fail"), ok.failedTrackUids)
            assertEquals(1, ok.coversPushed)

            mode.set(1)
            val fallback = LanSyncV2Client.requestUsbFastPush(baseUrl, "tok_test", tracks, covers)
            assertFalse(fallback.usedDirectPush)
            assertFalse(fallback.cancelledByHost)

            mode.set(2)
            val cancelled409 = LanSyncV2Client.requestUsbFastPush(baseUrl, "tok_test", tracks, covers)
            assertFalse(cancelled409.usedDirectPush)
            assertTrue(cancelled409.cancelledByHost)

            mode.set(3)
            val cancelledNdjson = LanSyncV2Client.requestUsbFastPush(baseUrl, "tok_test", tracks, covers)
            assertFalse(cancelledNdjson.usedDirectPush)
            assertTrue(cancelledNdjson.cancelledByHost)
        }
    }

    @Test
    fun lanHttpClient_reusesKeepAliveConnectionAndDownloadsBufferedFile() {
        val payload = ByteArray(1_200_000) { i -> ((i * 73) and 0xFF).toByte() }
        val connectionHeaders = ArrayList<String?>()
        withServer(
            configure = { server ->
                server.createContext("/api/v2/tracks/t1/file") { ex ->
                    connectionHeaders.add(ex.requestHeaders.getFirst("Connection"))
                    ex.sendResponseHeaders(200, payload.size.toLong())
                    ex.responseBody.use { it.write(payload) }
                }
            },
        ) { baseUrl ->
            val tmpDir = Files.createTempDirectory("tonewise_http_dl_test").toFile()
            try {
                val part1 = File(tmpDir, "t1_a.part")
                val part2 = File(tmpDir, "t1_b.part")
                val token = LanHttpClient.CancelToken()
                val size1 =
                    LanHttpClient.downloadFileResumable(
                        url = "$baseUrl/api/v2/tracks/t1/file",
                        bearerToken = "tok",
                        partFile = part1,
                        expectedSize = payload.size.toLong(),
                        cancelToken = token,
                    )
                val size2 =
                    LanHttpClient.downloadFileResumable(
                        url = "$baseUrl/api/v2/tracks/t1/file",
                        bearerToken = "tok",
                        partFile = part2,
                        expectedSize = payload.size.toLong(),
                        cancelToken = token,
                    )
                assertEquals(payload.size.toLong(), size1)
                assertEquals(payload.size.toLong(), size2)
                assertEquals(2, connectionHeaders.size)
                assertFalse(connectionHeaders.any { "close".equals(it, ignoreCase = true) })
                assertTrue(part1.readBytes().contentEquals(payload))
                assertTrue(part2.readBytes().contentEquals(payload))
            } finally {
                tmpDir.deleteRecursively()
            }
        }
    }

    @Test
    fun managedSyncStorage_isSafAutoRenamedDuplicate_detectsNumericSuffixesOnly() {
        assertTrue(ManagedSyncStorage.isSafAutoRenamedDuplicate("Antonio Vivaldi", "Antonio Vivaldi (1)"))
        assertTrue(ManagedSyncStorage.isSafAutoRenamedDuplicate("Max Richter, Daniel Hope", "Max Richter, Daniel Hope (2)"))
        assertFalse(ManagedSyncStorage.isSafAutoRenamedDuplicate("Antonio Vivaldi", "Antonio Vivaldi"))
        assertFalse(ManagedSyncStorage.isSafAutoRenamedDuplicate("Antonio Vivaldi", "Antonio Vivaldi (Live)"))
        assertFalse(ManagedSyncStorage.isSafAutoRenamedDuplicate("Symphony No. 1", "Symphony No. 1 (Remastered)"))
    }

    @Test
    fun syncMirrorPrunePlanner_normalizesUnicodeNfcNfdIdentically() {
        val localNfd = LocalMusicAudioFile("Schubert/Mu\u0308llerin/01_Das Wandern.flac", "content://1", 100L)
        val desiredNfc = listOf("Schubert/M\u00FCllerin/01_Das Wandern.flac")
        val policy =
            SyncPolicyDto(
                mirrorPruneEnabled = true,
                pruneScope = "music_folder_all",
                targetRoot = "Music",
                outboxAuthoritative = true,
                missingSourceCount = 0,
            )

        val plan =
            SyncMirrorPrunePlanner.planPrune(
                policy = policy,
                desiredTargetRelPaths = desiredNfc,
                localMusicFiles = listOf(localNfd),
                managedRelPaths = emptySet(),
                syncCompletedCleanly = true,
            )
        assertEquals(PruneDecision.PROCEED, plan.decision)
        assertTrue(plan.filesToDelete.isEmpty())
    }

    @Test
    fun lanSyncRepository_resolveEffectivePruneConfirmed_trustsUsbLoopbackOnlyAndRequiresLocalForLan() {
        assertTrue(
            LanSyncRepository.resolveEffectivePruneConfirmed(
                transport = SyncTransportMode.USB,
                baseUrl = "http://127.0.0.1:45820",
                localConfirmed = false,
                remoteConfirmed = true,
            )
        )
        assertFalse(
            LanSyncRepository.resolveEffectivePruneConfirmed(
                transport = SyncTransportMode.LAN,
                baseUrl = "http://192.168.1.100:45820",
                localConfirmed = false,
                remoteConfirmed = true,
            )
        )
        assertTrue(
            LanSyncRepository.resolveEffectivePruneConfirmed(
                transport = SyncTransportMode.LAN,
                baseUrl = "http://192.168.1.100:45820",
                localConfirmed = true,
                remoteConfirmed = false,
            )
        )
    }
}
