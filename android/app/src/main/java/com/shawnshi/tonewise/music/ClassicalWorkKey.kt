/*
 * Copyright (c) 2026 Tonewise Project
 * ClassicalWorkKey.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.music

import org.oxycblt.musikr.Song
import org.oxycblt.musikr.tag.Name

/**
 * Single source of truth for classical recording/work identity keys and movement ordering across
 * library indexing, WorkShuffle, ReplayGain dynamic context, AutoDJ, scene evaluation, and
 * sleep-timer boundaries.
 */
object ClassicalWorkKey {
    fun recordingScope(song: Song): String {
        val albumUid = song.album.uid.toString()
        val conductor = song.conductors.firstOrNull()?.trim()?.lowercase().orEmpty()
        return if (conductor.isNotEmpty()) "$albumUid|$conductor" else albumUid
    }

    fun workBlockKey(song: Song): String? {
        val explicitWork = song.workName?.trim()?.lowercase()?.takeIf { it.isNotEmpty() }
        if (explicitWork != null) {
            return "${recordingScope(song)}::$explicitWork"
        }

        val rawTitle = song.name.raw
        if (!ClassicalMetadataEngine.isClassicalTrack(
                genre = (song.genres.firstOrNull()?.name as? Name.Known)?.raw,
                work = null,
                composer = song.composers.firstOrNull(),
                conductor = song.conductors.firstOrNull(),
                title = rawTitle,
            )
        ) {
            return null
        }

        val parsed =
            ClassicalMetadataEngine.splitWorkAndMovement(
                rawTitle = rawTitle,
                explicitWork = null,
                explicitMovement = song.movementName,
                explicitMovementNumber = song.movementNumber,
            )
        val derivedWork = parsed.work.trim().lowercase()
        if (derivedWork.isEmpty() || (parsed.movement.isBlank() && parsed.movementNumber == null)) {
            return null
        }
        return "${recordingScope(song)}::$derivedWork"
    }

    fun movementComparator(): Comparator<Song> =
        compareBy(
            { it.disc?.number?.takeIf { d -> d > 0 } ?: Int.MAX_VALUE },
            { it.movementNumber?.takeIf { m -> m > 0 } ?: Int.MAX_VALUE },
            { it.track?.takeIf { t -> t > 0 } ?: Int.MAX_VALUE },
            { it.name.raw },
        )

    fun orderPreservingWorks(songs: List<Song>): List<Song> {
        if (songs.size <= 1) return songs
        val blocks = LinkedHashMap<String, MutableList<Song>>(songs.size)
        for ((idx, song) in songs.withIndex()) {
            val blockKey = workBlockKey(song) ?: "track::$idx::${song.uid}"
            blocks.getOrPut(blockKey) { ArrayList(2) }.add(song)
        }
        val cmp = movementComparator()
        val ordered = ArrayList<Song>(songs.size)
        for (block in blocks.values) {
            if (block.size > 1) {
                block.sortWith(cmp)
            }
            ordered.addAll(block)
        }
        return ordered
    }
}
