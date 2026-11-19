/*
 * Copyright (c) 2026 Tonewise Project
 * PlaylistM3u8Helper.kt is part of Tonewise.
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

import org.oxycblt.musikr.Song
import org.oxycblt.musikr.tag.Name

/**
 * Pure M3U8 formatting, path normalization, filename sanitization, and relative-path matching
 * helpers shared across LAN playlist synchronization and write-back.
 */
object PlaylistM3u8Helper {
    private val INVALID_FILENAME_CHARS_REGEX = Regex("[\\u0000-\\u001F\\\\/:*?\"<>|]+")
    private val MULTI_SPACE_REGEX = Regex(" {2,}")
    private val LEGACY_UNDERSCORE_REGEX = Regex("[\\\\/:*?\"<>|\\s]+")

    fun sanitizePlaylistFilename(raw: String): String {
        val collapsed =
            raw.replace('\r', ' ')
                .replace('\n', ' ')
                .replace('\t', ' ')
                .replace(INVALID_FILENAME_CHARS_REGEX, "_")
                .replace(MULTI_SPACE_REGEX, " ")
                .trim()
                .trimEnd('.')
                .trim()
        return if (collapsed.isEmpty() || collapsed == "." || collapsed == "..") {
            "Default"
        } else {
            collapsed
        }
    }

    fun legacyUnderscoredPlaylistFilename(raw: String): String =
        raw.trim().ifEmpty { "Default" }.replace(LEGACY_UNDERSCORE_REGEX, "_")

    fun extractPlaylistNameFromM3u8(lines: List<String>, fallbackName: String): String {
        for ((idx, rawLine) in lines.withIndex()) {
            val line = if (idx == 0) rawLine.removePrefix("\uFEFF").trim() else rawLine.trim()
            if (line.startsWith("#PLAYLIST:", ignoreCase = true)) {
                val declared =
                    line.substring("#PLAYLIST:".length)
                        .replace('\r', ' ')
                        .replace('\n', ' ')
                        .trim()
                if (declared.isNotEmpty()) return declared
            }
        }
        return fallbackName.trim().ifEmpty { "Default" }
    }

    fun normalizePlaylistEntryForPc(raw: String): String {
        val s = raw.trim().replace('\\', '/').trimStart('/')
        val lower = s.lowercase()
        val tonewiseMarker = "/tonewise/"
        val markerIdx = lower.indexOf(tonewiseMarker)
        if (markerIdx >= 0) {
            return s.substring(markerIdx + tonewiseMarker.length).trimStart('/')
        }
        if (lower.startsWith("tonewise/")) {
            return s.substring("tonewise/".length).trimStart('/')
        }
        val musicMarker = "/music/"
        val musicIdx = lower.indexOf(musicMarker)
        if (musicIdx >= 0) {
            return s.substring(musicIdx + musicMarker.length).trimStart('/')
        }
        if (lower.startsWith("music/")) {
            return s.substring("music/".length).trimStart('/')
        }
        return s
    }

    fun formatM3u8Content(songs: List<Song>, playlistName: String? = null): String =
        formatM3u8Entries(
            songs.map { song ->
                LanSyncRepository.M3u8Entry(
                    durationMs = song.durationMs,
                    title = song.name.raw,
                    artists = song.artists.mapNotNull { (it.name as? Name.Known)?.raw },
                    pathComponents = song.path.components,
                )
            },
            playlistName = playlistName,
        )

    fun formatM3u8Entries(
        entries: List<LanSyncRepository.M3u8Entry>,
        playlistName: String? = null,
    ): String {
        val sb = StringBuilder()
        sb.append("#EXTM3U\n")
        val cleanName =
            playlistName?.replace('\r', ' ')?.replace('\n', ' ')?.trim()?.takeIf {
                it.isNotEmpty()
            }
        if (cleanName != null) {
            sb.append("#PLAYLIST:").append(cleanName).append('\n')
        }
        for (entry in entries) {
            val secs = (entry.durationMs / 1000L).coerceAtLeast(0L)
            val artist = entry.artists.filter { it.isNotBlank() }.joinToString(", ")
            val extinf = if (artist.isNotEmpty()) "$artist - ${entry.title}" else entry.title
            sb.append("#EXTINF:").append(secs).append(',').append(extinf).append('\n')
            val relPath = entry.pathComponents.components.joinToString("/").replace('\\', '/')
            sb.append(relPath).append('\n')
        }
        return sb.toString()
    }

    fun buildSongsByRelSuffixMap(songs: Collection<Song>): List<Pair<String, Song>> =
        songs.map { song ->
            val pathStr =
                song.path.components.components.joinToString("/").replace('\\', '/').lowercase()
            pathStr to song
        }

    fun findMatchingSongByRelPath(
        targetRelPath: String,
        indexedSongs: List<Pair<String, Song>>,
    ): Song? {
        val cleanRel =
            LanSyncV2Client.sanitizeRelativePathSegments(targetRelPath)
                .joinToString("/")
                .lowercase()
        if (cleanRel.isEmpty()) return null
        return indexedSongs.firstOrNull { (fullRel, _) ->
            fullRel == cleanRel ||
                fullRel == "music/$cleanRel" ||
                fullRel.endsWith("/music/$cleanRel")
        }?.second
            ?: indexedSongs.firstOrNull { (fullRel, _) ->
                fullRel == "tonewise/$cleanRel" ||
                    fullRel.endsWith("/tonewise/$cleanRel") ||
                    fullRel.endsWith("/$cleanRel")
            }?.second
    }
}
