/*
 * Copyright (c) 2026 Tonewise Project
 * LanSyncV2Client.kt is part of Tonewise.
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

import java.io.File
import java.io.IOException
import java.net.URLEncoder
import kotlinx.coroutines.CancellationException
import org.oxycblt.musikr.core.RawJson
import org.oxycblt.musikr.core.TonewiseCore

class LanSyncAuthException(val httpCode: Int, message: String) : IllegalStateException(message)

class LanSyncRateLimitException(
    val retryAfterSeconds: Long,
    message: String,
) : IllegalStateException(message)

data class PairResponse(
    val token: String,
    val hostName: String,
    val deviceId: String,
    val deviceName: String,
    val pairedAtMs: Long,
)

data class OutboxItemDto(
    val id: String,
    val deviceId: String,
    val kind: String,
    val targetKey: String,
    val displayName: String,
    val mode: String,
    val trackUids: List<String>,
    val createdAtMs: Long,
)

data class OutboxTrackDto(
    val trackUid: String,
    val relPath: String,
    val targetRelPath: String,
    val title: String,
    val artist: String,
    val album: String,
    val albumArtist: String,
    val genre: String,
    val durationMs: Int,
    val fileSize: Long,
    val mtimeMs: Long,
    val hasCover: Boolean,
    val sourceOutboxIds: List<String>,
    val headerBlake3: String = "",
    val audioFrameBlake3: String = "",
)

enum class LocalSyncState {
    MISSING,
    UP_TO_DATE,
    OUTDATED,
}

data class OutboxPlaylistDto(
    val outboxId: String,
    val name: String,
    val relPaths: List<String>,
    val trackUids: List<String>,
)

data class OutboxResponse(
    val hostName: String,
    val generatedAtMs: Long,
    val items: List<OutboxItemDto>,
    val tracks: List<OutboxTrackDto>,
    val playlists: List<OutboxPlaylistDto>,
    val totalBytes: Long,
    val duplicateTrackCount: Int = 0,
)

data class PlayDeltaPayload(
    val eventId: String,
    val trackUid: String,
    val artist: String,
    val title: String,
    val album: String,
    val playCountDelta: Int,
    val lastPlayedTsMs: Long,
    val deviceId: String,
    val scrobbledDelta: Int = 0,
    val devicePlayCountTotal: Int = 0,
)

data class MutationPayload(
    val mutationKey: String,
    val trackUid: String,
    val artist: String,
    val title: String,
    val field: String,
    val value: String,
    val updatedAtMs: Long,
    val deviceId: String,
)

data class ReportResponse(
    val ok: Boolean,
    val mergedTracks: Int,
    val effectiveCounts: Map<String, Int>,
    val lovedUids: List<String>,
    val appliedMutations: Int,
    val acknowledgedMutationKeys: Set<String>,
    val unmatchedMutationUids: List<String>,
    val completedOutboxCount: Int,
    val handoffSlotsRawJson: String? = null,
    val unlovedUids: List<String> = emptyList(),
)

object LanSyncV2Client {
    fun ping(baseUrl: String): Boolean {
        val clean = baseUrl.trim().trimEnd('/')
        val pingUrl = "$clean/api/v2/ping"
        if (!LanHttpClient.isSafeLanOrHttpsUrl(pingUrl)) return false
        return try {
            val conn =
                LanHttpClient.openConnectionForUrl(pingUrl).apply {
                    instanceFollowRedirects = false
                    connectTimeout = 4_000
                    readTimeout = 4_000
                    requestMethod = "GET"
                }
            try {
                conn.responseCode in 200..299
            } finally {
                conn.disconnect()
            }
        } catch (_: Exception) {
            false
        }
    }

    fun unpair(
        baseUrl: String,
        token: String,
        cancelToken: LanHttpClient.CancelToken? = null,
    ): Boolean {
        val clean = baseUrl.trim().trimEnd('/')
        if (clean.isEmpty() || token.isBlank()) return false
        return try {
            val respText =
                LanHttpClient.postJson(
                    url = "$clean/api/v2/unpair",
                    body = "{}",
                    bearerToken = token,
                    cancelToken = cancelToken,
                    connectTimeoutMs = 3_000,
                    readTimeoutMs = 5_000,
                )
            val root = TonewiseCore.parseJson(respText) as? Map<*, *>
            (root?.get("ok") as? Boolean) ?: true
        } catch (ce: CancellationException) {
            throw ce
        } catch (_: Exception) {
            false
        }
    }

    fun pair(
        baseUrl: String,
        code: String,
        deviceId: String,
        deviceName: String,
        cancelToken: LanHttpClient.CancelToken? = null,
    ): PairResponse {
        val clean = baseUrl.trim().trimEnd('/')
        val body =
            TonewiseCore.stringifyJson(
                linkedMapOf(
                    "code" to code.trim(),
                    "device_id" to deviceId.trim(),
                    "device_name" to deviceName.trim(),
                )
            )
        val respText =
            LanHttpClient.postJson(
                url = "$clean/api/v2/pair",
                body = body,
                bearerToken = null,
                cancelToken = cancelToken,
            )
        @Suppress("UNCHECKED_CAST")
        val root =
            TonewiseCore.parseJson(respText) as? Map<String, Any?>
                ?: throw IOException("配对响应格式无效")
        val token = root["token"]?.toString()?.takeIf { it.isNotBlank() }
            ?: throw IOException("服务端未返回有效令牌")
        return PairResponse(
            token = token,
            hostName = root["host_name"]?.toString() ?: clean,
            deviceId = root["device_id"]?.toString() ?: deviceId,
            deviceName = root["device_name"]?.toString() ?: deviceName,
            pairedAtMs = asLong(root["paired_at_ms"]),
        )
    }

    fun fetchOutbox(
        baseUrl: String,
        token: String,
        cancelToken: LanHttpClient.CancelToken? = null,
    ): OutboxResponse {
        val clean = baseUrl.trim().trimEnd('/')
        val respText =
            LanHttpClient.getString(
                url = "$clean/api/v2/outbox",
                bearerToken = token,
                cancelToken = cancelToken,
                connectTimeoutMs = 5_000,
                readTimeoutMs = 60_000,
            )
        return parseOutboxResponse(respText)
    }

    fun parseOutboxResponse(jsonText: String): OutboxResponse {
        @Suppress("UNCHECKED_CAST")
        val root =
            TonewiseCore.parseJson(jsonText) as? Map<String, Any?>
                ?: throw IOException("同步清单格式无效")

        @Suppress("UNCHECKED_CAST")
        val rawItems = (root["items"] as? List<Any?>).orEmpty()
        val items =
            rawItems.mapNotNull { entry ->
                @Suppress("UNCHECKED_CAST")
                val m = entry as? Map<String, Any?> ?: return@mapNotNull null
                OutboxItemDto(
                    id = m["id"]?.toString() ?: return@mapNotNull null,
                    deviceId = m["device_id"]?.toString() ?: "*",
                    kind = m["kind"]?.toString() ?: "tracks",
                    targetKey = m["target_key"]?.toString() ?: "",
                    displayName = m["display_name"]?.toString() ?: "",
                    mode = m["mode"]?.toString() ?: "copy",
                    trackUids = asStringList(m["track_uids"]),
                    createdAtMs = asLong(m["created_at_ms"]),
                )
            }

        @Suppress("UNCHECKED_CAST")
        val rawTracks = (root["tracks"] as? List<Any?>).orEmpty()
        val tracks =
            rawTracks.mapNotNull { entry ->
                @Suppress("UNCHECKED_CAST")
                val m = entry as? Map<String, Any?> ?: return@mapNotNull null
                val uid = m["track_uid"]?.toString() ?: return@mapNotNull null
                OutboxTrackDto(
                    trackUid = uid,
                    relPath = m["rel_path"]?.toString() ?: "",
                    targetRelPath = m["target_rel_path"]?.toString() ?: (m["rel_path"]?.toString() ?: "$uid.flac"),
                    title = m["title"]?.toString() ?: uid,
                    artist = m["artist"]?.toString() ?: "",
                    album = m["album"]?.toString() ?: "",
                    albumArtist = m["album_artist"]?.toString() ?: "",
                    genre = m["genre"]?.toString() ?: "",
                    durationMs = asInt(m["duration_ms"]),
                    fileSize = asLong(m["file_size"]),
                    mtimeMs = asLong(m["mtime_ms"]),
                    hasCover = m["has_cover"] == true,
                    sourceOutboxIds = asStringList(m["source_outbox_ids"]),
                    headerBlake3 = m["header_blake3"]?.toString() ?: "",
                    audioFrameBlake3 = m["audio_frame_blake3"]?.toString() ?: "",
                )
            }

        @Suppress("UNCHECKED_CAST")
        val rawPlaylists = (root["playlists"] as? List<Any?>).orEmpty()
        val playlists =
            rawPlaylists.mapNotNull { entry ->
                @Suppress("UNCHECKED_CAST")
                val m = entry as? Map<String, Any?> ?: return@mapNotNull null
                val name = m["name"]?.toString() ?: return@mapNotNull null
                OutboxPlaylistDto(
                    outboxId = m["outbox_id"]?.toString() ?: "",
                    name = name,
                    relPaths = asStringList(m["rel_paths"]),
                    trackUids = asStringList(m["track_uids"]),
                )
            }

        return OutboxResponse(
            hostName = root["host_name"]?.toString() ?: "PC",
            generatedAtMs = asLong(root["generated_at_ms"]),
            items = items,
            tracks = tracks,
            playlists = playlists,
            totalBytes = asLong(root["total_bytes"]),
            duplicateTrackCount = asInt(root["duplicate_track_count"]),
        )
    }

    fun buildTrackFileUrl(baseUrl: String, trackUid: String): String {
        val clean = baseUrl.trim().trimEnd('/')
        return "$clean/api/v2/tracks/${URLEncoder.encode(trackUid, "UTF-8")}/file"
    }

    fun buildTrackCoverUrl(baseUrl: String, trackUid: String): String {
        val clean = baseUrl.trim().trimEnd('/')
        return "$clean/api/v2/tracks/${URLEncoder.encode(trackUid, "UTF-8")}/cover"
    }

    fun downloadFileResumable(
        url: String,
        token: String,
        partFile: File,
        expectedSize: Long,
        onProgress: (downloadedBytes: Long, totalBytes: Long) -> Unit = { _, _ -> },
        cancelToken: LanHttpClient.CancelToken? = null,
    ): Long {
        return LanHttpClient.downloadFileResumable(
            url = url,
            bearerToken = token,
            partFile = partFile,
            expectedSize = expectedSize,
            onProgress = onProgress,
            cancelToken = cancelToken,
        )
    }

    fun downloadCoverIfPresent(
        url: String,
        token: String,
        cancelToken: LanHttpClient.CancelToken? = null,
    ): ByteArray? {
        return try {
            LanHttpClient.getBytes(
                url = url,
                bearerToken = token,
                cancelToken = cancelToken,
                connectTimeoutMs = 5_000,
                readTimeoutMs = 10_000,
            )
        } catch (authErr: LanSyncAuthException) {
            throw authErr
        } catch (ce: CancellationException) {
            throw ce
        } catch (_: IOException) {
            null
        }
    }

    fun postReport(
        baseUrl: String,
        token: String,
        deviceId: String,
        playDeltas: List<PlayDeltaPayload>,
        mutations: List<MutationPayload>,
        completedOutboxIds: List<String>,
        handoffSlotsRawJson: String? = null,
        cancelToken: LanHttpClient.CancelToken? = null,
    ): ReportResponse {
        val clean = baseUrl.trim().trimEnd('/')
        val bodyMap =
            linkedMapOf<String, Any?>(
                "device_id" to deviceId,
                "play_events" to
                    playDeltas.map { p ->
                        linkedMapOf(
                            "event_id" to p.eventId,
                            "track_uid" to p.trackUid,
                            "artist" to p.artist,
                            "title" to p.title,
                            "album" to p.album,
                            "play_count_delta" to p.playCountDelta,
                            "last_played_ts_ms" to p.lastPlayedTsMs,
                            "device_id" to p.deviceId,
                            "scrobbled_delta" to p.scrobbledDelta,
                            "device_play_count_total" to p.devicePlayCountTotal,
                        )
                    },
                "mutations" to
                    mutations.map { m ->
                        linkedMapOf(
                            "mutation_key" to m.mutationKey,
                            "track_uid" to m.trackUid,
                            "artist" to m.artist,
                            "title" to m.title,
                            "field" to m.field,
                            "value" to m.value,
                            "lww_ts_ms" to m.updatedAtMs,
                            "updated_at_ms" to m.updatedAtMs,
                            "device_id" to m.deviceId,
                        )
                    },
                "completed_outbox_ids" to completedOutboxIds,
            )
        if (!handoffSlotsRawJson.isNullOrBlank()) {
            bodyMap["handoff_slots"] = RawJson(handoffSlotsRawJson)
        }
        val body = TonewiseCore.stringifyJson(bodyMap)

        val respText =
            LanHttpClient.postJson(
                url = "$clean/api/v2/report",
                body = body,
                bearerToken = token,
                cancelToken = cancelToken,
                connectTimeoutMs = 5_000,
                readTimeoutMs = 30_000,
            )
        return parseReportResponse(respText)
    }

    fun parseReportResponse(jsonText: String): ReportResponse {
        @Suppress("UNCHECKED_CAST")
        val root =
            TonewiseCore.parseJson(jsonText) as? Map<String, Any?>
                ?: throw IOException("回执格式无效")

        val counts = HashMap<String, Int>()
        @Suppress("UNCHECKED_CAST")
        val rawCounts = root["effective_counts"] as? Map<String, Any?>
        if (rawCounts != null) {
            for ((k, v) in rawCounts) {
                counts[k] = asInt(v)
            }
        }

        val handoffSlotsRawJson =
            (root["handoff_slots"] as? List<*>)?.let { rawSlots ->
                TonewiseCore.stringifyJson(rawSlots)
            }

        return ReportResponse(
            ok = root["ok"] == true,
            mergedTracks = asInt(root["merged_tracks"]),
            effectiveCounts = counts,
            lovedUids = asStringList(root["loved_uids"]),
            appliedMutations = asInt(root["applied_mutations"]),
            acknowledgedMutationKeys = asStringList(root["acknowledged_mutation_keys"]).toSet(),
            unmatchedMutationUids = asStringList(root["unmatched_mutation_uids"]),
            completedOutboxCount = asInt(root["completed_outbox_count"]),
            handoffSlotsRawJson = handoffSlotsRawJson,
            unlovedUids = asStringList(root["unloved_uids"]),
        )
    }

    fun sanitizeRelativePathSegments(relPath: String): List<String> {
        val segments = ArrayList<String>()
        for (raw in relPath.replace('\\', '/').split('/')) {
            val trimmed = raw.trim()
            if (trimmed.isEmpty() || trimmed == "." || trimmed == "..") continue
            val clean =
                buildString(trimmed.length) {
                    for (ch in trimmed) {
                        if (ch.code < 0x20 || ch in charArrayOf('<', '>', ':', '"', '/', '\\', '|', '?', '*')) {
                            append('_')
                        } else {
                            append(ch)
                        }
                    }
                }.trim()
            if (clean.isNotEmpty() && clean != "." && clean != "..") {
                segments.add(clean)
            }
        }
        return segments
    }

    fun filterAcknowledgedMutations(
        batch: List<TagMutationRecord>,
        keyByRecord: Map<TagMutationRecord, String>,
        ackKeys: Set<String>,
    ): List<TagMutationRecord> {
        if (ackKeys.isEmpty() || batch.isEmpty()) return emptyList()
        return batch.filter { rec ->
            val key = keyByRecord[rec]
            key != null && key in ackKeys
        }
    }

    fun deduplicateOutboxTracks(tracks: List<OutboxTrackDto>): Pair<List<OutboxTrackDto>, Int> {
        if (tracks.isEmpty()) return emptyList<OutboxTrackDto>() to 0
        val deduped = ArrayList<OutboxTrackDto>(tracks.size)
        val indexByUid = HashMap<String, Int>()
        val indexByRelPath = HashMap<String, Int>()
        var duplicateCount = 0

        for (track in tracks) {
            val uidKey = track.trackUid.trim().lowercase()
            val relKey = sanitizeRelativePathSegments(track.targetRelPath).joinToString("/").lowercase()
            val existingIdx =
                indexByUid[uidKey] ?: if (relKey.isNotEmpty()) indexByRelPath[relKey] else null

            if (existingIdx == null) {
                val idx = deduped.size
                deduped.add(track)
                if (uidKey.isNotEmpty()) indexByUid[uidKey] = idx
                if (relKey.isNotEmpty()) indexByRelPath[relKey] = idx
            } else {
                duplicateCount++
                val existing = deduped[existingIdx]
                val mergedOutboxIds = (existing.sourceOutboxIds + track.sourceOutboxIds).distinct()
                val preferred = if (track.mtimeMs > existing.mtimeMs) track else existing
                deduped[existingIdx] = preferred.copy(sourceOutboxIds = mergedOutboxIds)
            }
        }
        return deduped to duplicateCount
    }

    fun evaluateLocalTrackFreshness(
        remote: OutboxTrackDto,
        exists: Boolean,
        localFileSize: Long,
        localMtimeMs: Long,
        localHeaderBlake3: String = "",
        localAudioFrameBlake3: String = "",
    ): LocalSyncState {
        if (!exists) return LocalSyncState.MISSING

        if (remote.fileSize > 0L && localFileSize > 0L && localFileSize != remote.fileSize) {
            return LocalSyncState.OUTDATED
        }

        val remoteAudio = remote.audioFrameBlake3.trim()
        val localAudio = localAudioFrameBlake3.trim()
        if (remoteAudio.isNotEmpty() && localAudio.isNotEmpty() &&
            !remoteAudio.equals(localAudio, ignoreCase = true)
        ) {
            return LocalSyncState.OUTDATED
        }

        val remoteHeader = remote.headerBlake3.trim()
        val localHeader = localHeaderBlake3.trim()
        if (remoteHeader.isNotEmpty() && localHeader.isNotEmpty()) {
            return if (remoteHeader.equals(localHeader, ignoreCase = true)) {
                LocalSyncState.UP_TO_DATE
            } else {
                LocalSyncState.OUTDATED
            }
        }

        if (remote.mtimeMs > 0L && localMtimeMs > 0L && remote.mtimeMs > localMtimeMs + 2_000L) {
            return LocalSyncState.OUTDATED
        }

        return if (remote.fileSize <= 0L || localFileSize <= 0L || localFileSize == remote.fileSize) {
            LocalSyncState.UP_TO_DATE
        } else {
            LocalSyncState.OUTDATED
        }
    }

    private fun asInt(v: Any?): Int = (v as? Number)?.toInt() ?: v?.toString()?.toIntOrNull() ?: 0

    private fun asLong(v: Any?): Long = (v as? Number)?.toLong() ?: v?.toString()?.toLongOrNull() ?: 0L

    private fun asStringList(v: Any?): List<String> =
        (v as? List<*>)?.mapNotNull { it?.toString()?.takeIf { s -> s.isNotEmpty() } } ?: emptyList()
}
