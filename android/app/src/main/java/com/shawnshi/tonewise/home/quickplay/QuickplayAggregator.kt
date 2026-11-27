/*
 * Copyright (c) 2026 Tonewise Project
 * QuickplayAggregator.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.home.quickplay

data class QuickplayTrackCandidate(
    val songUid: String,
    val title: String,
    val artist: String,
    val albumUid: String,
    val albumTitle: String,
    val addedMs: Long,
    val playCount: Int,
    val lastPlayedMs: Long,
    val coverId: String = "",
)

data class QuickplayAlbumSummary(
    val albumUid: String,
    val representativeSongUid: String,
    val albumTitle: String,
    val artist: String,
    val timestampMs: Long,
    val playCount: Int,
    val coverId: String = "",
)

data class QuickplaySections(
    val history: List<QuickplayAlbumSummary>,
    val newlyAdded: List<QuickplayAlbumSummary>,
    val rediscovery: List<QuickplayAlbumSummary>,
) {
    val isEmpty: Boolean
        get() = history.isEmpty() && newlyAdded.isEmpty() && rediscovery.isEmpty()
}

/**
 * Minimal pure-Kotlin JSON scanner/writer shared by Quickplay snapshot and pin codecs so JVM unit
 * tests run without Android `org.json` runtime stubs.
 */
internal object QuickplayJsonCodecSupport {
    fun splitArrayObjects(trimmedArray: String): List<String> {
        if (!trimmedArray.startsWith('[')) return emptyList()
        val out = mutableListOf<String>()
        var idx = 1
        val len = trimmedArray.length
        while (idx < len) {
            while (idx < len && (trimmedArray[idx].isWhitespace() || trimmedArray[idx] == ',')) idx++
            if (idx >= len || trimmedArray[idx] == ']') break
            if (trimmedArray[idx] != '{') break
            val objStart = idx
            var inString = false
            var escaped = false
            var depth = 0
            while (idx < len) {
                val ch = trimmedArray[idx]
                if (escaped) {
                    escaped = false
                } else if (ch == '\\' && inString) {
                    escaped = true
                } else if (ch == '"') {
                    inString = !inString
                } else if (!inString) {
                    if (ch == '{') depth++
                    else if (ch == '}') {
                        depth--
                        if (depth == 0) {
                            idx++
                            break
                        }
                    }
                }
                idx++
            }
            if (depth == 0) {
                out.add(trimmedArray.substring(objStart, idx))
            }
        }
        return out
    }

    fun parseFlatObject(obj: String): Map<String, String> {
        val fields = mutableMapOf<String, String>()
        var i = 1
        val len = obj.length - 1
        while (i < len) {
            while (i < len && (obj[i].isWhitespace() || obj[i] == ',')) i++
            if (i >= len || obj[i] != '"') break
            val (key, afterKey) = readJsonString(obj, i) ?: break
            i = afterKey
            while (i < len && (obj[i].isWhitespace() || obj[i] == ':')) i++
            if (i >= len) break
            if (obj[i] == '"') {
                val (value, afterVal) = readJsonString(obj, i) ?: break
                fields[key] = value
                i = afterVal
            } else {
                val numStart = i
                while (i < len && obj[i] != ',' && obj[i] != '}') i++
                fields[key] = obj.substring(numStart, i).trim()
            }
        }
        return fields
    }

    fun readTopLevelArrays(trimmedObj: String): Map<String, String> {
        if (!trimmedObj.startsWith('{')) return emptyMap()
        val arrays = mutableMapOf<String, String>()
        var i = 1
        val len = trimmedObj.length - 1
        while (i < len) {
            while (i < len && (trimmedObj[i].isWhitespace() || trimmedObj[i] == ',')) i++
            if (i >= len || trimmedObj[i] != '"') break
            val (key, afterKey) = readJsonString(trimmedObj, i) ?: break
            i = afterKey
            while (i < len && (trimmedObj[i].isWhitespace() || trimmedObj[i] == ':')) i++
            if (i >= len) break
            when (trimmedObj[i]) {
                '[' -> {
                    val start = i
                    var inStr = false
                    var esc = false
                    var depth = 0
                    while (i < trimmedObj.length) {
                        val ch = trimmedObj[i]
                        if (esc) {
                            esc = false
                        } else if (ch == '\\' && inStr) {
                            esc = true
                        } else if (ch == '"') {
                            inStr = !inStr
                        } else if (!inStr) {
                            if (ch == '[') depth++
                            else if (ch == ']') {
                                depth--
                                if (depth == 0) {
                                    i++
                                    break
                                }
                            }
                        }
                        i++
                    }
                    if (depth == 0) {
                        arrays[key] = trimmedObj.substring(start, i)
                    }
                }
                '"' -> {
                    val (_, afterVal) = readJsonString(trimmedObj, i) ?: break
                    i = afterVal
                }
                else -> {
                    while (i < len && trimmedObj[i] != ',' && trimmedObj[i] != '}') i++
                }
            }
        }
        return arrays
    }

    private fun readJsonString(src: String, startQuote: Int): Pair<String, Int>? {
        val sb = StringBuilder()
        var i = startQuote + 1
        val len = src.length
        while (i < len) {
            val ch = src[i++]
            when (ch) {
                '"' -> return sb.toString() to i
                '\\' -> {
                    if (i >= len) return null
                    when (val esc = src[i++]) {
                        '"' -> sb.append('"')
                        '\\' -> sb.append('\\')
                        '/' -> sb.append('/')
                        'b' -> sb.append('\b')
                        'f' -> sb.append('\u000C')
                        'n' -> sb.append('\n')
                        'r' -> sb.append('\r')
                        't' -> sb.append('\t')
                        'u' -> {
                            if (i + 4 > len) return null
                            val code = src.substring(i, i + 4).toIntOrNull(16) ?: return null
                            sb.append(code.toChar())
                            i += 4
                        }
                        else -> sb.append(esc)
                    }
                }
                else -> sb.append(ch)
            }
        }
        return null
    }

    fun appendJsonField(sb: StringBuilder, key: String, value: String, comma: Boolean) {
        if (comma) sb.append(',')
        sb.append('"')
        appendEscaped(sb, key)
        sb.append("\":\"")
        appendEscaped(sb, value)
        sb.append('"')
    }

    fun appendEscaped(sb: StringBuilder, value: String) {
        for (ch in value) {
            when (ch) {
                '"' -> sb.append("\\\"")
                '\\' -> sb.append("\\\\")
                '\n' -> sb.append("\\n")
                '\r' -> sb.append("\\r")
                '\t' -> sb.append("\\t")
                '\b' -> sb.append("\\b")
                '\u000C' -> sb.append("\\f")
                else -> if (ch.code < 0x20) sb.append("\\u%04x".format(ch.code)) else sb.append(ch)
            }
        }
    }
}

/**
 * Pure Kotlin JSON snapshot codec for [QuickplaySections] persisted in `sync_kv` so cold start
 * displays history/newlyAdded immediately before `AuxioService` binds and hydrates the library.
 */
object QuickplaySectionsCodec {
    private const val MAX_ITEMS_PER_SECTION = 12

    fun serializeSections(sections: QuickplaySections): String {
        val sb = StringBuilder(1024)
        sb.append("{\"v\":1,\"history\":")
        appendSummaryArray(sb, sections.history.take(MAX_ITEMS_PER_SECTION))
        sb.append(",\"newlyAdded\":")
        appendSummaryArray(sb, sections.newlyAdded.take(MAX_ITEMS_PER_SECTION))
        sb.append(",\"rediscovery\":")
        appendSummaryArray(sb, sections.rediscovery.take(MAX_ITEMS_PER_SECTION))
        sb.append('}')
        return sb.toString()
    }

    fun deserializeSections(raw: String?): QuickplaySections {
        if (raw.isNullOrBlank()) return QuickplaySections(emptyList(), emptyList(), emptyList())
        val trimmed = raw.trim()
        if (!trimmed.startsWith('{')) return QuickplaySections(emptyList(), emptyList(), emptyList())
        val arrays = QuickplayJsonCodecSupport.readTopLevelArrays(trimmed)
        return QuickplaySections(
            history = parseSummaryArray(arrays["history"]),
            newlyAdded = parseSummaryArray(arrays["newlyAdded"]),
            rediscovery = parseSummaryArray(arrays["rediscovery"]),
        )
    }

    private fun appendSummaryArray(sb: StringBuilder, items: List<QuickplayAlbumSummary>) {
        sb.append('[')
        for ((index, item) in items.withIndex()) {
            if (index > 0) sb.append(',')
            sb.append('{')
            QuickplayJsonCodecSupport.appendJsonField(sb, "albumUid", item.albumUid, comma = false)
            QuickplayJsonCodecSupport.appendJsonField(
                sb,
                "representativeSongUid",
                item.representativeSongUid,
                comma = true,
            )
            QuickplayJsonCodecSupport.appendJsonField(sb, "albumTitle", item.albumTitle, comma = true)
            QuickplayJsonCodecSupport.appendJsonField(sb, "artist", item.artist, comma = true)
            if (item.coverId.isNotEmpty()) {
                QuickplayJsonCodecSupport.appendJsonField(sb, "coverId", item.coverId, comma = true)
            }
            sb.append(",\"timestampMs\":").append(item.timestampMs)
            sb.append(",\"playCount\":").append(item.playCount)
            sb.append('}')
        }
        sb.append(']')
    }

    private fun parseSummaryArray(arrayJson: String?): List<QuickplayAlbumSummary> {
        if (arrayJson.isNullOrBlank()) return emptyList()
        val objects = QuickplayJsonCodecSupport.splitArrayObjects(arrayJson.trim())
        val out = ArrayList<QuickplayAlbumSummary>(objects.size)
        for (obj in objects) {
            val fields = QuickplayJsonCodecSupport.parseFlatObject(obj)
            val albumUid = fields["albumUid"]?.takeIf { it.isNotEmpty() } ?: continue
            val repUid = fields["representativeSongUid"].orEmpty()
            val albumTitle = fields["albumTitle"].orEmpty()
            val artist = fields["artist"].orEmpty()
            val coverId = fields["coverId"].orEmpty()
            val ts = fields["timestampMs"]?.toLongOrNull() ?: 0L
            val plays = fields["playCount"]?.toIntOrNull() ?: 0
            out.add(
                QuickplayAlbumSummary(
                    albumUid = albumUid,
                    representativeSongUid = repUid,
                    albumTitle = albumTitle,
                    artist = artist,
                    timestampMs = ts,
                    playCount = plays,
                    coverId = coverId,
                )
            )
            if (out.size >= MAX_ITEMS_PER_SECTION) break
        }
        return out
    }
}

/**
 * Pure Kotlin Quickplay section computation (History, Newly Added, 90d Rediscovery) matching
 * Tonewise Desktop's aggregation logic without Android runtime dependencies.
 */
object QuickplayAggregator {
    const val REDISCOVERY_AGE_MS: Long = 90L * 24L * 60L * 60L * 1000L

    fun computeSections(
        candidates: List<QuickplayTrackCandidate>,
        nowMs: Long,
        limit: Int = 12,
    ): QuickplaySections {
        if (candidates.isEmpty()) {
            return QuickplaySections(emptyList(), emptyList(), emptyList())
        }

        val history =
            dedupByAlbum(
                candidates
                    .filter { it.lastPlayedMs > 0L }
                    .sortedByDescending { it.lastPlayedMs },
                limit = limit,
                timestampSelector = { it.lastPlayedMs },
            )

        val newlyAdded =
            dedupByAlbum(
                candidates.sortedWith(
                    compareByDescending<QuickplayTrackCandidate> { it.addedMs }
                        .thenBy { it.albumTitle }
                ),
                limit = limit,
                timestampSelector = { it.addedMs },
            )

        val cutoff = nowMs - REDISCOVERY_AGE_MS
        val rediscovery =
            dedupByAlbum(
                candidates
                    .filter { it.playCount > 0 && it.lastPlayedMs in 1L until cutoff }
                    .sortedWith(
                        compareBy<QuickplayTrackCandidate> { it.lastPlayedMs }
                            .thenByDescending { it.playCount }
                    ),
                limit = limit,
                timestampSelector = { it.lastPlayedMs },
            )

        return QuickplaySections(
            history = history,
            newlyAdded = newlyAdded,
            rediscovery = rediscovery,
        )
    }

    private inline fun dedupByAlbum(
        ordered: List<QuickplayTrackCandidate>,
        limit: Int,
        timestampSelector: (QuickplayTrackCandidate) -> Long,
    ): List<QuickplayAlbumSummary> {
        val seenAlbums = LinkedHashSet<String>()
        val result = ArrayList<QuickplayAlbumSummary>(limit)
        for (item in ordered) {
            if (!seenAlbums.add(item.albumUid)) continue
            result.add(
                QuickplayAlbumSummary(
                    albumUid = item.albumUid,
                    representativeSongUid = item.songUid,
                    albumTitle = item.albumTitle,
                    artist = item.artist,
                    timestampMs = timestampSelector(item),
                    playCount = item.playCount,
                    coverId = item.coverId,
                )
            )
            if (result.size >= limit) break
        }
        return result
    }
}
