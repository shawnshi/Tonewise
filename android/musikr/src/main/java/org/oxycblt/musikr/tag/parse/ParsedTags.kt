/*
 * Copyright (c) 2024 Auxio Project
 * ParsedTags.kt is part of Auxio.
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
 
package org.oxycblt.musikr.tag.parse

import org.oxycblt.musikr.tag.Date

data class ParsedTags(
    val durationMs: Long,
    val replayGainTrackAdjustment: Float? = null,
    val replayGainAlbumAdjustment: Float? = null,
    val musicBrainzId: String? = null,
    val name: String? = null,
    val sortName: String? = null,
    val track: Int? = null,
    val disc: Int? = null,
    val subtitle: String? = null,
    val date: Date? = null,
    val albumMusicBrainzId: String? = null,
    val albumName: String? = null,
    val albumSortName: String? = null,
    val releaseTypes: List<String> = listOf(),
    val artistMusicBrainzIds: List<String> = listOf(),
    val artistNames: List<String> = listOf(),
    val artistSortNames: List<String> = listOf(),
    val albumArtistMusicBrainzIds: List<String> = listOf(),
    val albumArtistNames: List<String> = listOf(),
    val albumArtistSortNames: List<String> = listOf(),
    val composerNames: List<String> = listOf(),
    val conductorNames: List<String> = listOf(),
    val genreNames: List<String> = listOf(),
    val djVocals: String? = null,
    val djEnergy: String? = null,
    val djScenes: List<String> = listOf(),
    val workName: String? = null,
    val movementName: String? = null,
    val movementNumber: Int? = null,
    val musicBrainzWorkId: String? = null,
    val moods: List<String> = listOf(),
)
