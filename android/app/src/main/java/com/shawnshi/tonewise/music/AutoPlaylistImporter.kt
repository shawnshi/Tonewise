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
import javax.inject.Inject
import org.oxycblt.musikr.DiscoveredPlaylist
import org.oxycblt.musikr.Library
import org.oxycblt.musikr.MutableLibrary
import org.oxycblt.musikr.Song
import org.oxycblt.musikr.fs.Path
import org.oxycblt.musikr.playlist.ExternalPlaylistManager
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
            val trackedModifiedMs = tracked?.substringBefore('|')?.toLongOrNull()
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

            val songs = matcher.match(imported.paths)
            if (songs.isEmpty()) {
                L.d("Skipping discovered playlist '$playlistName': no matching songs in library")
                continue
            }

            val existing = currentLibrary.findPlaylistByName(playlistName)
            if (existing == null) {
                L.d("Auto-importing playlist '$playlistName' (${songs.size} songs)")
                currentLibrary = currentLibrary.createPlaylist(playlistName, songs)
                saveTracked(uriKey, discovered.modifiedMs, playlistName)
            } else if (trackedModifiedMs != null) {
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
        prefs.edit { putString(uriKey, "$modifiedMs|$name") }
    }

    private companion object {
        const val PREFS_NAME = "tonewise_auto_m3u_imports"
    }
}

internal class PlaylistSongMatcher(private val library: Library) {
    private val byParentAndFile: Map<String, Song?> by lazy {
        val grouped = HashMap<String, MutableList<Song>>()
        for (song in library.songs) {
            val key = parentAndFileKey(song.path) ?: continue
            grouped.getOrPut(key) { mutableListOf() }.add(song)
        }
        grouped.mapValues { (_, matches) -> matches.singleOrNull() }
    }

    private val byFileName: Map<String, Song?> by lazy {
        val grouped = HashMap<String, MutableList<Song>>()
        for (song in library.songs) {
            val file = song.path.name?.lowercase() ?: continue
            grouped.getOrPut(file) { mutableListOf() }.add(song)
        }
        grouped.mapValues { (_, matches) -> matches.singleOrNull() }
    }

    fun match(paths: List<PossiblePaths>): List<Song> =
        paths.mapNotNull { candidates ->
            candidates.firstNotNullOfOrNull(library::findSongByPath)
                ?: candidates.firstNotNullOfOrNull { candidate ->
                    parentAndFileKey(candidate)?.let { byParentAndFile[it] }
                }
                ?: candidates.firstNotNullOfOrNull { candidate ->
                    candidate.name?.lowercase()?.let { byFileName[it] }
                }
        }

    private fun parentAndFileKey(path: Path): String? {
        val parts = path.components.components
        if (parts.size < 2) return null
        return "${parts[parts.size - 2].lowercase()}/${parts[parts.size - 1].lowercase()}"
    }
}
