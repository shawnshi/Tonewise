/*
 * Copyright (c) 2026 Tonewise Project
 * WorkShuffleOrder.kt is part of Tonewise.
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

package com.shawnshi.tonewise.playback.service

import androidx.annotation.OptIn
import androidx.media3.common.util.UnstableApi
import androidx.media3.exoplayer.source.ShuffleOrder
import kotlin.random.Random
import org.oxycblt.musikr.Song

/**
 * Work-preserving [ShuffleOrder]: shuffles classical works (`WORK` / `TIT1` on the same album) as
 * atomic multi-movement blocks so symphonies/concertos always play their movements in order.
 */
@OptIn(UnstableApi::class)
object WorkShuffleOrder {
    fun fromSongs(songs: List<Song>, startIndex: Int, random: Random = Random.Default): ShuffleOrder {
        if (songs.none { !it.workName.isNullOrBlank() }) {
            return BetterShuffleOrder(songs.size, startIndex)
        }
        return BetterShuffleOrder(createWorkAwareOrder(songs, startIndex, random))
    }

    fun createWorkAwareOrder(
        songs: List<Song>,
        startIndex: Int,
        random: Random = Random.Default,
    ): IntArray {
        if (songs.isEmpty()) return IntArray(0)

        val blocks = linkedMapOf<String, MutableList<Int>>()
        for (i in songs.indices) {
            val s = songs[i]
            val work = s.workName?.trim()?.lowercase()?.takeIf { it.isNotEmpty() }
            val conductor = s.conductors.firstOrNull()?.trim()?.lowercase().orEmpty()
            val key =
                if (work != null) {
                    "work::${s.album.uid}::$conductor::$work"
                } else {
                    "track::$i"
                }
            blocks.getOrPut(key) { mutableListOf() }.add(i)
        }

        val orderedBlocks =
            blocks.values
                .map { indices ->
                    indices.sortedWith(
                        compareBy(
                            { songs[it].disc?.number ?: 0 },
                            { songs[it].movementNumber ?: songs[it].track ?: it },
                            { it },
                        )
                    )
                }
                .toMutableList()

        val startBlockIdx =
            if (startIndex in songs.indices) {
                orderedBlocks.indexOfFirst { startIndex in it }
            } else {
                -1
            }

        val anchorBlock = if (startBlockIdx >= 0) orderedBlocks.removeAt(startBlockIdx) else null
        orderedBlocks.shuffle(random)

        val flattened = ArrayList<Int>(songs.size)
        if (anchorBlock != null) {
            flattened.addAll(anchorBlock)
        }
        for (block in orderedBlocks) {
            flattened.addAll(block)
        }
        return flattened.toIntArray()
    }
}
