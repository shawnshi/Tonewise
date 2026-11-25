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
    CLASSICAL("classical", null),
    COMPOSERS("composers", MusicType.COMPOSERS),
    CONDUCTORS("conductors", MusicType.CONDUCTORS),
    GENRES("genres", MusicType.GENRES),
    PLAYLISTS("playlists", MusicType.PLAYLISTS),
    SCENES("scenes", MusicType.SCENES);

    companion object {
        fun fromOrdinal(index: Int): CollectionPivot =
            entries.getOrElse(index) { ALBUMS }

        /**
         * Maps user-configured visible [MusicType] tabs (order + visibility from Settings ->
         * Library tabs) into level-2 Zune sub-pivots, while keeping `classical` inserted cleanly.
         */
        fun resolveVisiblePivots(visibleTabs: List<MusicType>): List<CollectionPivot> {
            if (visibleTabs.isEmpty()) return entries
            val mapped = ArrayList<CollectionPivot>(visibleTabs.size + 1)
            for (tab in visibleTabs) {
                entries.firstOrNull { it.musicType == tab }?.let(mapped::add)
            }
            if (mapped.isEmpty()) return entries
            val artistIdx = mapped.indexOf(ARTISTS)
            val insertIdx =
                when {
                    artistIdx >= 0 -> artistIdx + 1
                    else -> {
                        val compOrCond =
                            mapped.indexOfFirst { it == COMPOSERS || it == CONDUCTORS }
                        if (compOrCond >= 0) compOrCond else 0
                    }
                }
            mapped.add(insertIdx, CLASSICAL)
            return mapped
        }
    }
}
