/*
 * Copyright (c) 2024 Auxio Project
 * Separators.kt is part of Auxio.
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
 
package org.oxycblt.musikr.tag.interpret

import org.oxycblt.musikr.util.correctWhitespace
import org.oxycblt.musikr.util.splitEscaped

/**
 * Defines the user-specified parsing of multi-value tags. This should be used to parse any tags
 * that may be delimited with a separator character.
 *
 * @author Alexander Capehart (OxygenCobalt)
 */
interface Separators {
    /**
     * Parse a separated value from one or more strings. If the value is already composed of more
     * than one value, nothing is done. Otherwise, it will attempt to split it based on the user's
     * separator preferences.
     *
     * @return A new list of one or more [String]s parsed by the separator configuration
     */
    fun split(strings: List<String>): List<String>

    companion object {
        const val COMMA = ','
        const val SEMICOLON = ';'
        const val SLASH = '/'
        const val PLUS = '+'
        const val AND = '&'

        /**
         * Creates a new instance from a string of separator characters to use.
         *
         * @param chars The separator characters to use. Each character in the string will be
         *   checked for when splitting a string list.
         * @return A new [Separators] instance reflecting the separators.
         */
        fun from(chars: String) =
            if (chars.isNotEmpty()) {
                CharSeparators(chars.toSet())
            } else {
                NoSeparators
            }
    }
}

private object ArtistWhitelistRegistry {
    // Mirrors `protocol/dictionaries/artist_whitelist.json` sorted longest-first for greedy placeholder masking
    val protectedNames: List<String> =
        listOf(
                "Tom Petty & The Heartbreakers",
                "Crosby, Stills, Nash & Young",
                "Nick Cave & The Bad Seeds",
                "Florence + The Machine",
                "Hootie & the Blowfish",
                "Sly & the Family Stone",
                "Crosby, Stills & Nash",
                "Angus & Julia Stone",
                "Earth, Wind & Fire",
                "Simon & Garfunkel",
                "Belle & Sebastian",
                "Tears for Fears",
                "Kool & the Gang",
                "Above & Beyond",
                "The Civil Wars",
                "Mumford & Sons",
                "Brooks & Dunn",
                "Hall & Oates",
                "Iron & Wine",
                "AC/DC",
            )
            .sortedByDescending { it.length }

    fun splitPreservingWhitelist(input: String, splitUnmasked: (String) -> List<String>): List<String> {
        var working = input
        val replacements = ArrayList<Pair<String, String>>()
        var seq = 0
        for ((idx, name) in protectedNames.withIndex()) {
            var searchFrom = 0
            while (true) {
                val matchIdx = working.indexOf(name, startIndex = searchFrom, ignoreCase = true)
                if (matchIdx < 0) break
                val token = "__TW_WL_${idx}_${seq++}__"
                val originalSlice = working.substring(matchIdx, matchIdx + name.length)
                working = working.replaceRange(matchIdx, matchIdx + name.length, token)
                replacements.add(token to originalSlice)
                searchFrom = matchIdx + token.length
            }
        }
        val chunks = splitUnmasked(working)
        if (replacements.isEmpty()) return chunks
        return chunks
            .map { chunk ->
                var restored = chunk
                for ((token, original) in replacements) {
                    if (restored.contains(token)) {
                        restored = restored.replace(token, original)
                    }
                }
                restored.trim()
            }
            .filter { it.isNotEmpty() }
    }
}

private data class CharSeparators(private val chars: Set<Char>) : Separators {
    override fun split(strings: List<String>) =
        if (strings.size == 1) splitImpl(strings.first()) else strings

    private fun splitImpl(string: String): List<String> =
        ArtistWhitelistRegistry.splitPreservingWhitelist(string) { unmasked ->
            unmasked.splitEscaped { chars.contains(it) }.correctWhitespace()
        }
}

private object NoSeparators : Separators {
    override fun split(strings: List<String>) = strings
}
