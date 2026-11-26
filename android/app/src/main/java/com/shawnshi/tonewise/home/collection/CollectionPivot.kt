/*
 * Copyright (c) 2026 Tonewise Project
 * CollectionPivot.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.home.collection

import com.shawnshi.tonewise.music.MusicType

/**
 * Zune Level-2 lowercase sub-pivots inside the `collection` Panorama hub.
 * Tap-switched to preserve clean single-layer horizontal Panorama swipes on Level 1.
 */
enum class CollectionPivot(
    val label: String,
    val musicType: MusicType?,
) {
    ALBUMS("albums", MusicType.ALBUMS),
    TRACKS("tracks", MusicType.SONGS),
    ARTISTS("artists", MusicType.ARTISTS),
    GENRES("genres", MusicType.GENRES),
    CLASSICAL("classical", null),
    COMPOSERS("composers", MusicType.COMPOSERS),
    CONDUCTORS("conductors", MusicType.CONDUCTORS),
    PLAYLISTS("playlists", MusicType.PLAYLISTS);

    companion object {
        val DEFAULT_PIVOTS: List<CollectionPivot> = entries

        fun fromOrdinal(index: Int): CollectionPivot =
            entries.getOrElse(index) { ALBUMS }
    }
}
