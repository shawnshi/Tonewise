/*
 * Copyright (c) 2026 Tonewise Project
 * AutoPlaylistImporter.kt is part of Tonewise.
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
 
package com.shawnshi.tonewise.music

import android.content.Context
import android.content.SharedPreferences
import androidx.core.content.edit
import dagger.hilt.android.qualifiers.ApplicationContext
import java.text.Normalizer
import javax.inject.Inject
import org.oxycblt.musikr.DiscoveredPlaylist
import org.oxycblt.musikr.Library
import org.oxycblt.musikr.MutableLibrary
import org.oxycblt.musikr.Song
import org.oxycblt.musikr.fs.Path
import org.oxycblt.musikr.playlist.ExternalPlaylistManager
import org.oxycblt.musikr.playlist.ImportedPlaylistEntry
import org.oxycblt.musikr.playlist.PossiblePaths
import timber.log.Timber as L

/**
 * Automatically imports and updates `.m3u` / `.m3u8` playlists discovered inside `playlist`
 * folders during library indexing.
 */
class AutoPlaylistImporter
@Inject
constructor(@ApplicationContext private val context: Context) {
    private val externalPlaylistManager = ExternalPlaylistManager.from(context)
    private val prefs: SharedPreferences by lazy {
        context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
    }

    suspend fun sync(
        library: MutableLibrary,
        discoveredPlaylists: List<DiscoveredPlaylist>,
    ): MutableLibrary {
        if (discoveredPlaylists.isEmpty() || library.empty()) {
            return library
        }

        var currentLibrary = library
        val matcher = PlaylistSongMatcher(currentLibrary)
        val sorted = discoveredPlaylists.sortedBy { it.path.name?.lowercase() ?: "" }

        for (discovered in sorted) {
            val uriKey = discovered.uri.toString()
            val tracked = prefs.getString(uriKey, null)
            val trackedModifiedMs =
                if (tracked != null && tracked.startsWith("$TRACK_VERSION|")) {
                    tracked.removePrefix("$TRACK_VERSION|").substringBefore('|').toLongOrNull()
                } else {
                    null
                }
            if (trackedModifiedMs != null && trackedModifiedMs == discovered.modifiedMs) {
                continue
            }

            val imported = externalPlaylistManager.import(discovered.uri)
            if (imported == null) {
                L.w("Failed to parse discovered playlist: ${discovered.path}")
                continue
            }

            val fallbackName =
                discovered.path.name
                    ?.substringBeforeLast('.')
                    ?.replace(Regex("[_-]"), " ")
                    ?.replace(Regex("\\s+"), " ")
                    ?.trim()
            val playlistName = imported.name?.trim()?.takeIf { it.isNotEmpty() } ?: fallbackName
            if (playlistName.isNullOrEmpty()) {
                continue
            }

            val songs = matcher.matchEntries(imported.entries)
            if (songs.isEmpty()) {
                L.d("Skipping discovered playlist '$playlistName': no matching songs in library")
                continue
            }

            val existing = currentLibrary.findPlaylistByName(playlistName)
            if (existing == null) {
                L.d("Auto-importing playlist '$playlistName' (${songs.size} songs)")
                currentLibrary = currentLibrary.createPlaylist(playlistName, songs)
                saveTracked(uriKey, discovered.modifiedMs, playlistName)
            } else if (trackedModifiedMs != null || tracked != null) {
                if (existing.songs.map { it.uid } != songs.map { it.uid }) {
                    L.d("Updating auto-imported playlist '$playlistName' (${songs.size} songs)")
                    currentLibrary = currentLibrary.rewritePlaylist(existing, songs)
                }
                saveTracked(uriKey, discovered.modifiedMs, playlistName)
            } else if (existing.songs.isEmpty() || existing.songs.map { it.uid } == songs.map { it.uid }) {
                if (existing.songs.isEmpty()) {
                    currentLibrary = currentLibrary.rewritePlaylist(existing, songs)
                }
                saveTracked(uriKey, discovered.modifiedMs, playlistName)
            } else {
                L.d(
                    "Skipping auto-import for '$playlistName': user playlist with same name already exists"
                )
            }
        }

        return currentLibrary
    }

    private fun saveTracked(uriKey: String, modifiedMs: Long, name: String) {
        prefs.edit { putString(uriKey, "$TRACK_VERSION|$modifiedMs|$name") }
    }

    private companion object {
        const val PREFS_NAME = "tonewise_auto_m3u_imports"
        const val TRACK_VERSION = "v3"
    }
}

internal class PlaylistSongMatcher(private val library: Library) {
    private val metadataMatcher: AndroidPlaylistTrackMatcher<Song> by lazy {
        AndroidPlaylistTrackMatcher.fromSongs(library.songs)
    }

    private val byNormalizedRelPath: Map<String, List<Song>> by lazy {
        val grouped = HashMap<String, MutableList<Song>>()
        for (song in library.songs) {
            for (key in normalizedRelKeys(song.path)) {
                grouped.getOrPut(key) { mutableListOf() }.add(song)
            }
        }
        grouped
    }

    private val byParentAndFile: Map<String, List<Song>> by lazy {
        val grouped = HashMap<String, MutableList<Song>>()
        for (song in library.songs) {
            val key = parentAndFileKey(song.path) ?: continue
            grouped.getOrPut(key) { mutableListOf() }.add(song)
        }
        grouped
    }

    private val byFileName: Map<String, List<Song>> by lazy {
        val grouped = HashMap<String, MutableList<Song>>()
        for (song in library.songs) {
            val file = normalizeSegment(song.path.name) ?: continue
            grouped.getOrPut(file) { mutableListOf() }.add(song)
        }
        grouped
    }

    fun match(paths: List<PossiblePaths>): List<Song> =
        matchEntries(paths.map { ImportedPlaylistEntry(it) })

    fun matchEntries(entries: List<ImportedPlaylistEntry>): List<Song> =
        entries.mapNotNull { entry -> matchSingleEntry(entry) }

    private fun matchSingleEntry(entry: ImportedPlaylistEntry): Song? {
        val candidates = entry.paths
        if (candidates.isNotEmpty()) {
            candidates.firstNotNullOfOrNull(library::findSongByPath)?.let { return it }

            candidates
                .firstNotNullOfOrNull { candidate ->
                    normalizedRelKeys(candidate).firstNotNullOfOrNull { key ->
                        disambiguate(byNormalizedRelPath[key], entry)
                    }
                }
                ?.let { return it }

            candidates
                .firstNotNullOfOrNull { candidate ->
                    parentAndFileKey(candidate)?.let { key -> disambiguate(byParentAndFile[key], entry) }
                }
                ?.let { return it }

            candidates
                .firstNotNullOfOrNull { candidate ->
                    normalizeSegment(candidate.name)?.let { key -> disambiguate(byFileName[key], entry) }
                }
                ?.let { return it }
        }

        if (!entry.title.isNullOrBlank()) {
            metadataMatcher
                .match(
                    title = entry.title,
                    artist = entry.artist,
                    album = entry.album,
                    durationMs = entry.durationSec * 1000L,
                )
                ?.let { return it }
        }

        for (candidate in candidates) {
            val name = candidate.name ?: continue
            metadataMatcher.matchByFilenameHint(name)?.let { return it }
        }
        return null
    }

    private fun disambiguate(pool: List<Song>?, entry: ImportedPlaylistEntry): Song? {
        if (pool.isNullOrEmpty()) return null
        if (pool.size == 1) return pool[0]
        if (!entry.title.isNullOrBlank() || !entry.artist.isNullOrBlank()) {
            val scoped = AndroidPlaylistTrackMatcher.fromSongs(pool)
            val titleHint = entry.title ?: entry.artist
            if (!titleHint.isNullOrBlank()) {
                scoped
                    .match(
                        title = titleHint,
                        artist = if (entry.title != null) entry.artist else null,
                        album = entry.album,
                        durationMs = entry.durationSec * 1000L,
                    )
                    ?.let { return it }
            }
        }
        return null
    }

    private fun normalizeSegment(raw: String?): String? =
        raw?.let { Normalizer.normalize(it, Normalizer.Form.NFC).lowercase() }

    private fun normalizedRelKeys(path: Path): Set<String> {
        val full =
            Normalizer.normalize(
                    path.components.components.joinToString("/"),
                    Normalizer.Form.NFC,
                )
                .replace('\\', '/')
                .trim('/')
                .lowercase()
        if (full.isEmpty()) return emptySet()
        val keys = LinkedHashSet<String>()
        keys.add(full)
        val markerIdx = full.indexOf("/music/tonewise/")
        if (markerIdx >= 0) {
            keys.add(full.substring(markerIdx + "/music/tonewise/".length).trimStart('/'))
        }
        val musicMarkerIdx = full.indexOf("/music/")
        if (musicMarkerIdx >= 0) {
            keys.add(full.substring(musicMarkerIdx + "/music/".length).trimStart('/'))
        }
        val tonewiseMarkerIdx = full.indexOf("/tonewise/")
        if (tonewiseMarkerIdx >= 0) {
            keys.add(full.substring(tonewiseMarkerIdx + "/tonewise/".length).trimStart('/'))
        }
        if (full.startsWith("music/tonewise/")) {
            keys.add(full.substring("music/tonewise/".length).trimStart('/'))
        }
        if (full.startsWith("music/")) {
            keys.add(full.substring("music/".length).trimStart('/'))
        }
        if (full.startsWith("tonewise/")) {
            keys.add(full.substring("tonewise/".length).trimStart('/'))
        }
        keys.remove("")
        return keys
    }

    private fun parentAndFileKey(path: Path): String? {
        val parts = path.components.components
        if (parts.size < 2) return null
        val parent = normalizeSegment(parts[parts.size - 2]) ?: return null
        val file = normalizeSegment(parts[parts.size - 1]) ?: return null
        return "$parent/$file"
    }
}
