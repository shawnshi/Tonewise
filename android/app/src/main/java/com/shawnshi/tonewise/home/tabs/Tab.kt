/*
 * Copyright (c) 2021 Auxio Project
 * Tab.kt is part of Auxio.
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
 
package com.shawnshi.tonewise.home.tabs

import com.shawnshi.tonewise.music.MusicType
import timber.log.Timber as L

/**
 * A representation of a library tab suitable for configuration.
 *
 * @param type The type of list in the home view this instance corresponds to.
 * @author Alexander Capehart (OxygenCobalt)
 */
sealed class Tab(open val type: MusicType) {
    /**
     * A visible tab. This will be visible in the home and tab configuration views.
     *
     * @param type The type of list in the home view this instance corresponds to.
     */
    data class Visible(override val type: MusicType) : Tab(type)

    /**
     * A visible tab. This will be visible in the tab configuration view, but not in the home view.
     *
     * @param type The type of list in the home view this instance corresponds to.
     */
    data class Invisible(override val type: MusicType) : Tab(type)

    companion object {
        // Like other IO-bound datatypes in Auxio, tabs are stored in a binary format. However, tabs
        // cannot be serialized on their own. Instead, they are saved as a sequence of tabs as shown
        // below:
        //
        // 0bTAB1_TAB2_TAB3_TAB4_TAB5
        //
        // Where TABN is a chunk representing a tab at position N.
        // Each chunk in a sequence is represented as:
        //
        // VTTT
        //
        // Where V is a bit representing the visibility and T is a 3-bit integer representing the
        // MusicMode for this tab.

        private const val SEQ_FORMAT_V2 = 1 shl 24

        /** The maximum index that a well-formed tab sequence should be. */
        const val MAX_SEQUENCE_IDX = 7

        /**
         * The default tab sequence, in integer form. This represents visible tabs ordered as
         * "Songs", "Albums", "Artists", "Genres", "Playlists", "Scenes", "Composers", and
         * "Conductors".
         */
        const val SEQUENCE_DEFAULT = 0b1000_1001_1010_1011_1100_1101_1110_1111.toInt()

        /** Maps between the integer code in the tab sequence and it's [MusicType]. */
        private val MODE_TABLE =
            arrayOf(
                MusicType.SONGS,
                MusicType.ALBUMS,
                MusicType.ARTISTS,
                MusicType.GENRES,
                MusicType.PLAYLISTS,
                MusicType.SCENES,
                MusicType.COMPOSERS,
                MusicType.CONDUCTORS,
            )

        /**
         * Convert an array of [Tab]s into it's integer representation.
         *
         * @param tabs The array of [Tab]s to convert
         * @return An integer representation of the [Tab] array
         */
        fun toIntCode(tabs: Array<Tab>): Int {
            // Like when deserializing, make sure there are no duplicate tabs for whatever reason.
            val distinct = tabs.distinctBy { it.type }.toMutableList()
            if (tabs.size != distinct.size) {
                L.w(
                    "Tab sequences should not have duplicates [old: ${tabs.size} new: ${distinct.size}]"
                )
            }
            for (mode in MODE_TABLE) {
                if (distinct.none { it.type == mode }) {
                    distinct += Visible(mode)
                }
            }

            var sequence = 0
            var shift = MAX_SEQUENCE_IDX * 4
            for (tab in distinct) {
                val bin =
                    when (tab) {
                        is Visible -> 1.shl(3) or MODE_TABLE.indexOf(tab.type)
                        is Invisible -> MODE_TABLE.indexOf(tab.type)
                    }

                sequence = sequence or bin.shl(shift)
                shift -= 4
            }

            return sequence
        }

        /**
         * Convert a [Tab] integer representation into it's corresponding array of [Tab]s.
         *
         * @param intCode The integer representation of the [Tab]s.
         * @return An array of [Tab]s corresponding to the sequence.
         */
        fun fromIntCode(intCode: Int): Array<Tab>? {
            decodeV3(intCode)?.let { return it }

            val isV2 = (intCode ushr 25) == 0 && (intCode and SEQ_FORMAT_V2) != 0
            val startIdx = if (isV2) 5 else 4
            val tabs = mutableListOf<Tab>()

            // Try to parse a mode for each chunk in the legacy sequence (V1: 5 tabs, V2: 6 tabs).
            // If we can't parse one, just skip it.
            for (shift in (0..startIdx * 4).reversed() step 4) {
                val chunk = intCode.ushr(shift) and 0b1111
                val mode = MODE_TABLE.getOrNull(chunk and 7) ?: continue

                // Figure out the visibility
                tabs +=
                    if (chunk and 1.shl(3) != 0) {
                        Visible(mode)
                    } else {
                        Invisible(mode)
                    }
            }

            // Append any newly introduced tab modes (e.g., migrating 5/6-tab configs -> 8-tab).
            for (mode in MODE_TABLE) {
                if (tabs.none { it.type == mode }) {
                    tabs += Visible(mode)
                }
            }

            // Make sure there are no duplicate tabs
            val distinct = tabs.distinctBy { it.type }
            if (tabs.size != distinct.size) {
                L.w(
                    "Tab sequences should not have duplicates [old: ${tabs.size} new: ${distinct.size}]"
                )
            }

            if (distinct.size < MODE_TABLE.size) {
                L.e("Sequence size was ${distinct.size}, which is invalid")
                return null
            }

            return distinct.toTypedArray()
        }

        private fun decodeV3(intCode: Int): Array<Tab>? {
            val tabs = ArrayList<Tab>(MODE_TABLE.size)
            val seen = BooleanArray(MODE_TABLE.size)
            for (idx in 0 until MODE_TABLE.size) {
                val shift = (MAX_SEQUENCE_IDX - idx) * 4
                val chunk = intCode.ushr(shift) and 0b1111
                val modeIdx = chunk and 7
                val mode = MODE_TABLE.getOrNull(modeIdx) ?: return null
                if (seen[modeIdx]) return null
                seen[modeIdx] = true
                tabs +=
                    if (chunk and 1.shl(3) != 0) {
                        Visible(mode)
                    } else {
                        Invisible(mode)
                    }
            }
            return tabs.toTypedArray()
        }
    }
}
