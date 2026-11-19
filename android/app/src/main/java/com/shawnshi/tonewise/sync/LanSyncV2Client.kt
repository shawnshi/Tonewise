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
import java.io.FileOutputStream
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL
import java.net.URLEncoder
import org.oxycblt.musikr.core.TonewiseCore

class LanSyncAuthException(val httpCode: Int, message: String) : IllegalStateException(message)

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
)

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
)

object LanSyncV2Client {
    fun ping(baseUrl: String): Boolean {
        val clean = baseUrl.trim().trimEnd('/')
        return try {
            val conn =
                (URL("$clean/api/v2/ping").openConnection() as HttpURLConnection).apply {
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

    fun pair(
        baseUrl: String,
        code: String,
        deviceId: String,
        deviceName: String,
    ): PairResponse {
        val clean = baseUrl.trim().trimEnd('/')
        val body =
            buildString {
                append('{')
                append("\"code\":\"").append(escapeJson(code.trim())).append("\",")
                append("\"device_id\":\"").append(escapeJson(deviceId.trim())).append("\",")
                append("\"device_name\":\"").append(escapeJson(deviceName.trim())).append('"')
                append('}')
            }
        val respText = httpPostJson("$clean/api/v2/pair", body, token = null)
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

    fun fetchOutbox(baseUrl: String, token: String): OutboxResponse {
        val clean = baseUrl.trim().trimEnd('/')
        val respText = httpGetString("$clean/api/v2/outbox", token)
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
    ): Long {
        return downloadFileResumableInternal(url, token, partFile, expectedSize, onProgress, allowRetry416 = true)
    }

    private fun downloadFileResumableInternal(
        url: String,
        token: String,
        partFile: File,
        expectedSize: Long,
        onProgress: (Long, Long) -> Unit,
        allowRetry416: Boolean,
    ): Long {
        partFile.parentFile?.mkdirs()
        var existingBytes = if (partFile.isFile) partFile.length() else 0L
        if (expectedSize > 0L && existingBytes == expectedSize) {
            onProgress(expectedSize, expectedSize)
            return expectedSize
        }
        if (expectedSize > 0L && existingBytes > expectedSize) {
            partFile.delete()
            existingBytes = 0L
        }

        val conn =
            (URL(url).openConnection() as HttpURLConnection).apply {
                connectTimeout = 8_000
                readTimeout = 30_000
                requestMethod = "GET"
                setRequestProperty("Authorization", "Bearer $token")
                if (existingBytes > 0L) {
                    setRequestProperty("Range", "bytes=$existingBytes-")
                }
            }

        return try {
            val code = conn.responseCode
            if (code == HttpURLConnection.HTTP_UNAUTHORIZED) {
                throw LanSyncAuthException(code, "未授权或配对已过期，请重新配对")
            }
            if (code == 416) {
                if (expectedSize > 0L && existingBytes == expectedSize) {
                    onProgress(expectedSize, expectedSize)
                    return expectedSize
                }
                if (allowRetry416) {
                    partFile.delete()
                    conn.disconnect()
                    return downloadFileResumableInternal(url, token, partFile, expectedSize, onProgress, allowRetry416 = false)
                }
                throw IOException("HTTP 416 Range mismatch for $url")
            }
            if (code !in 200..299) {
                throw IOException("HTTP $code for $url")
            }

            val append = (code == HttpURLConnection.HTTP_PARTIAL && existingBytes > 0L)
            if (!append && existingBytes > 0L) {
                existingBytes = 0L
            }
            var written = existingBytes
            val contentLen = conn.contentLengthLong
            val totalTarget =
                when {
                    expectedSize > 0L -> expectedSize
                    contentLen > 0L -> existingBytes + contentLen
                    else -> -1L
                }

            val buffer = ByteArray(32 * 1024)
            conn.inputStream.use { input ->
                FileOutputStream(partFile, append).use { output ->
                    while (true) {
                        val n = input.read(buffer)
                        if (n <= 0) break
                        output.write(buffer, 0, n)
                        written += n
                        onProgress(written, totalTarget)
                    }
                    output.flush()
                }
            }

            val finalSize = partFile.length()
            if (expectedSize > 0L && finalSize != expectedSize) {
                throw IOException("文件传输未完成（已接收 $finalSize / $expectedSize 字节），下次同步将继续断点续传")
            }
            finalSize
        } finally {
            conn.disconnect()
        }
    }

    fun downloadCoverIfPresent(url: String, token: String): ByteArray? {
        val conn =
            (URL(url).openConnection() as HttpURLConnection).apply {
                connectTimeout = 5_000
                readTimeout = 10_000
                requestMethod = "GET"
                setRequestProperty("Authorization", "Bearer $token")
            }
        return try {
            val code = conn.responseCode
            if (code == HttpURLConnection.HTTP_UNAUTHORIZED) {
                throw LanSyncAuthException(code, "未授权或配对已过期")
            }
            if (code == HttpURLConnection.HTTP_NOT_FOUND) return null
            if (code !in 200..299) return null
            conn.inputStream.readBytes()
        } finally {
            conn.disconnect()
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
    ): ReportResponse {
        val clean = baseUrl.trim().trimEnd('/')
        val body =
            buildString {
                append('{')
                append("\"device_id\":\"").append(escapeJson(deviceId)).append("\",")
                append("\"play_events\":[")
                playDeltas.forEachIndexed { idx, p ->
                    if (idx > 0) append(',')
                    append('{')
                    append("\"event_id\":\"").append(escapeJson(p.eventId)).append("\",")
                    append("\"track_uid\":\"").append(escapeJson(p.trackUid)).append("\",")
                    append("\"artist\":\"").append(escapeJson(p.artist)).append("\",")
                    append("\"title\":\"").append(escapeJson(p.title)).append("\",")
                    append("\"album\":\"").append(escapeJson(p.album)).append("\",")
                    append("\"play_count_delta\":").append(p.playCountDelta).append(',')
                    append("\"last_played_ts_ms\":").append(p.lastPlayedTsMs).append(',')
                    append("\"device_id\":\"").append(escapeJson(p.deviceId)).append('"')
                    append('}')
                }
                append("],\"mutations\":[")
                mutations.forEachIndexed { idx, m ->
                    if (idx > 0) append(',')
                    append('{')
                    append("\"mutation_key\":\"").append(escapeJson(m.mutationKey)).append("\",")
                    append("\"track_uid\":\"").append(escapeJson(m.trackUid)).append("\",")
                    append("\"artist\":\"").append(escapeJson(m.artist)).append("\",")
                    append("\"title\":\"").append(escapeJson(m.title)).append("\",")
                    append("\"field\":\"").append(escapeJson(m.field)).append("\",")
                    append("\"value\":\"").append(escapeJson(m.value)).append("\",")
                    append("\"updated_at_ms\":").append(m.updatedAtMs).append(',')
                    append("\"device_id\":\"").append(escapeJson(m.deviceId)).append('"')
                    append('}')
                }
                append("],\"completed_outbox_ids\":[")
                completedOutboxIds.forEachIndexed { idx, id ->
                    if (idx > 0) append(',')
                    append('"').append(escapeJson(id)).append('"')
                }
                append(']')
                if (!handoffSlotsRawJson.isNullOrBlank()) {
                    append(",\"handoff_slots\":").append(handoffSlotsRawJson)
                }
                append('}')
            }

        val respText = httpPostJson("$clean/api/v2/report", body, token)
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

        return ReportResponse(
            ok = root["ok"] == true,
            mergedTracks = asInt(root["merged_tracks"]),
            effectiveCounts = counts,
            lovedUids = asStringList(root["loved_uids"]),
            appliedMutations = asInt(root["applied_mutations"]),
            acknowledgedMutationKeys = asStringList(root["acknowledged_mutation_keys"]).toSet(),
            unmatchedMutationUids = asStringList(root["unmatched_mutation_uids"]),
            completedOutboxCount = asInt(root["completed_outbox_count"]),
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

    private fun httpGetString(url: String, token: String): String {
        val conn =
            (URL(url).openConnection() as HttpURLConnection).apply {
                connectTimeout = 5_000
                readTimeout = 15_000
                requestMethod = "GET"
                setRequestProperty("Authorization", "Bearer $token")
            }
        return try {
            val code = conn.responseCode
            if (code == HttpURLConnection.HTTP_UNAUTHORIZED) {
                throw LanSyncAuthException(code, "未授权或配对已过期，请重新配对")
            }
            if (code !in 200..299) {
                throw IOException("HTTP $code for $url")
            }
            String(conn.inputStream.readBytes(), Charsets.UTF_8)
        } finally {
            conn.disconnect()
        }
    }

    private fun httpPostJson(url: String, body: String, token: String?): String {
        val conn =
            (URL(url).openConnection() as HttpURLConnection).apply {
                connectTimeout = 5_000
                readTimeout = 20_000
                requestMethod = "POST"
                doOutput = true
                setRequestProperty("Content-Type", "application/json; charset=utf-8")
                if (!token.isNullOrBlank()) {
                    setRequestProperty("Authorization", "Bearer $token")
                }
            }
        return try {
            conn.outputStream.use { it.write(body.toByteArray(Charsets.UTF_8)) }
            val code = conn.responseCode
            if (code == HttpURLConnection.HTTP_UNAUTHORIZED) {
                throw LanSyncAuthException(code, "配对码错误或未授权")
            }
            if (code !in 200..299) {
                throw IOException("HTTP $code for $url")
            }
            String(conn.inputStream.readBytes(), Charsets.UTF_8)
        } finally {
            conn.disconnect()
        }
    }

    private fun asInt(v: Any?): Int = (v as? Number)?.toInt() ?: v?.toString()?.toIntOrNull() ?: 0

    private fun asLong(v: Any?): Long = (v as? Number)?.toLong() ?: v?.toString()?.toLongOrNull() ?: 0L

    private fun asStringList(v: Any?): List<String> =
        (v as? List<*>)?.mapNotNull { it?.toString()?.takeIf { s -> s.isNotEmpty() } } ?: emptyList()

    private fun escapeJson(value: String): String =
        buildString {
            for (ch in value) {
                when (ch) {
                    '"' -> append("\\\"")
                    '\\' -> append("\\\\")
                    '\b' -> append("\\b")
                    '\u000C' -> append("\\f")
                    '\n' -> append("\\n")
                    '\r' -> append("\\r")
                    '\t' -> append("\\t")
                    else -> {
                        if (ch.code < 0x20) {
                            append("\\u")
                            append(ch.code.toString(16).padStart(4, '0'))
                        } else {
                            append(ch)
                        }
                    }
                }
            }
        }
}
