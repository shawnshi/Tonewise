/*
 * Copyright (c) 2026 Tonewise Project
 * Batch1CrossPlatformFoundationTest.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.sync

import com.shawnshi.tonewise.playback.persist.PlayLedgerWatermarkMath
import com.sun.net.httpserver.HttpServer
import java.net.InetSocketAddress
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test

class Batch1CrossPlatformFoundationTest {
    @Test
    fun sessionSlotStore_serializesParsesViaMiniJson_andDetectsEmptyPcSlots() {
        val emptySlot =
            SessionSlotItem(
                slotIndex = 2,
                name = "Slot 2",
                sceneId = null,
                updatedAtMs = 0L,
                queueTrackUids = emptyList(),
                currentIndex = 0,
                positionMs = 0L,
            )
        val populatedSlot =
            SessionSlotItem(
                slotIndex = 1,
                name = "Late Night Jazz",
                sceneId = "relax",
                updatedAtMs = 1_763_300_000_000L,
                queueTrackUids = listOf("a".repeat(64), "b".repeat(64)),
                currentIndex = 1,
                positionMs = 42_500L,
            )

        assertFalse(SessionSlotStore.isSlotPopulated(null))
        assertFalse(SessionSlotStore.isSlotPopulated(emptySlot))
        assertTrue(SessionSlotStore.isSlotPopulated(populatedSlot))

        val encoded = SessionSlotStore.serializeSlots(listOf(emptySlot, populatedSlot))
        val parsed = SessionSlotStore.parseSlots(encoded)
        assertEquals(2, parsed.size)
        assertEquals(populatedSlot, parsed[0])
        assertEquals(emptySlot, parsed[1])
    }

    @Test
    fun canonicalIdentityBridge_translatesLocalUidsAndResolvesWithFallback() {
        val blake1 = "0123456789abcdef".repeat(4)
        val blake2 = "fedcba9876543210".repeat(4)
        val canonical =
            LanSyncRepository.translateLocalUidsToCanonical(
                localUids = listOf("uas_local_1", "uas_local_2", "ums_unindexed_3"),
                localToBlake3 = mapOf("uas_local_1" to blake1.uppercase(), "uas_local_2" to blake2),
            )
        assertEquals(listOf(blake1, blake2, "ums_unindexed_3"), canonical)

        val resolved =
            LanSyncRepository.resolveCanonicalUidsToItems(
                uids = listOf(blake1.uppercase(), "ums_legacy_4"),
                blake3ToItem = mapOf(blake1 to "TrackA"),
                localUidToItem = mapOf("ums_legacy_4" to "TrackLegacy"),
            )
        assertEquals(listOf("TrackA", "TrackLegacy"), resolved)
    }

    @Test
    fun lanSyncV2Client_reportSerializesScrobbleSplitAndParsesHandoffSlots() {
        var capturedRequestBody = ""
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/api/v2/report") { ex ->
            capturedRequestBody = ex.requestBody.readBytes().toString(Charsets.UTF_8)
            val resp =
                """
                {
                  "ok": true,
                  "merged_tracks": 1,
                  "effective_counts": {"${"a".repeat(64)}": 11},
                  "loved_uids": ["${"a".repeat(64)}"],
                  "applied_mutations": 0,
                  "acknowledged_mutation_keys": [],
                  "unmatched_mutation_uids": [],
                  "completed_outbox_count": 0,
                  "handoff_slots": [
                    {"slot_index":1,"name":"Desk","scene_id":"focus","updated_at_ms":2000,"queue_track_uids":["${"a".repeat(64)}"],"current_index":0,"position_ms":12000},
                    {"slot_index":2,"name":"Slot 2","scene_id":null,"updated_at_ms":0,"queue_track_uids":[],"current_index":0,"position_ms":0}
                  ]
                }
                """
                    .trimIndent()
                    .toByteArray(Charsets.UTF_8)
            ex.sendResponseHeaders(200, resp.size.toLong())
            ex.responseBody.use { it.write(resp) }
        }
        server.start()
        try {
            val baseUrl = "http://127.0.0.1:${server.address.port}"
            val slotsJson =
                SessionSlotStore.serializeSlots(
                    listOf(
                        SessionSlotItem(1, "Phone", "focus", 1000L, listOf("a".repeat(64)), 0, 5000L)
                    )
                )
            val resp =
                LanSyncV2Client.postReport(
                    baseUrl = baseUrl,
                    token = "tok_test",
                    deviceId = "pixel9",
                    playDeltas =
                        listOf(
                            PlayDeltaPayload(
                                eventId = "ev1",
                                trackUid = "a".repeat(64),
                                artist = "Glenn Gould",
                                title = "Aria",
                                album = "BWV 988",
                                playCountDelta = 1,
                                lastPlayedTsMs = 1_763_300_000_000L,
                                deviceId = "pixel9",
                                scrobbledDelta = 1,
                                devicePlayCountTotal = 11,
                            )
                        ),
                    mutations = emptyList(),
                    completedOutboxIds = emptyList(),
                    handoffSlotsRawJson = slotsJson,
                )

            assertTrue(capturedRequestBody.contains("\"scrobbled_delta\":1"))
            assertTrue(capturedRequestBody.contains("\"device_play_count_total\":11"))
            assertTrue(capturedRequestBody.contains("\"handoff_slots\":["))
            assertNotNull(resp.handoffSlotsRawJson)

            val parsedSlots = SessionSlotStore.parseSlots(resp.handoffSlotsRawJson!!)
            assertEquals(2, parsedSlots.size)
            assertTrue(SessionSlotStore.isSlotPopulated(parsedSlots[0]))
            assertFalse(SessionSlotStore.isSlotPopulated(parsedSlots[1]))
        } finally {
            server.stop(0)
        }
    }

    @Test
    fun playLedgerWatermarkMath_preventsAllCrossLedgerFeedbackLoops() {
        // Loop A: PC count merged over LAN advances lastFmSyncedCount too -> zero backfill delta
        val afterLanPull =
            PlayLedgerWatermarkMath.mergeLanRemoteCount(
                PlayLedgerWatermarkMath.WatermarkState(playCount = 1, lanSyncedCount = 1, lastFmSyncedCount = 0),
                remoteCount = 20,
            )
        assertEquals(20, afterLanPull.playCount)
        assertEquals(20, afterLanPull.lanSyncedCount)
        assertEquals(20, afterLanPull.lastFmSyncedCount)

        // Loop C: Last.fm cloud pull advances lanSyncedCount too -> zero LAN push delta
        val afterCloudPull =
            PlayLedgerWatermarkMath.mergeCloudRemoteCount(
                PlayLedgerWatermarkMath.WatermarkState(playCount = 5, lanSyncedCount = 5, lastFmSyncedCount = 5),
                cloudCount = 18,
            )
        assertEquals(18, afterCloudPull.playCount)
        assertEquals(18, afterCloudPull.lanSyncedCount)
        assertEquals(18, afterCloudPull.lastFmSyncedCount)
        assertEquals(0, PlayLedgerWatermarkMath.computeLanPushDelta(afterCloudPull).totalDelta)

        // Loop B: Local play with active Last.fm scrobbling splits into scrobbledDelta=1, offlineDelta=0
        val playedAndScrobbled =
            PlayLedgerWatermarkMath.onLocalPlayScrobbled(
                afterCloudPull.copy(playCount = 19)
            )
        val scrobPush = PlayLedgerWatermarkMath.computeLanPushDelta(playedAndScrobbled)
        assertEquals(1, scrobPush.totalDelta)
        assertEquals(1, scrobPush.scrobbledDelta)
        assertEquals(0, scrobPush.offlineDelta)

        // Mixed: 2 offline plays (19->21) + 1 scrobbled play (21->22) => total=3, scrobbled=1, offline=2
        val mixed =
            PlayLedgerWatermarkMath.onLocalPlayScrobbled(
                PlayLedgerWatermarkMath.WatermarkState(
                    playCount = 22,
                    lanSyncedCount = 19,
                    lastFmSyncedCount = 19,
                )
            )
        val mixedPush = PlayLedgerWatermarkMath.computeLanPushDelta(mixed)
        assertEquals(3, mixedPush.totalDelta)
        assertEquals(1, mixedPush.scrobbledDelta)
        assertEquals(2, mixedPush.offlineDelta)
    }
}
