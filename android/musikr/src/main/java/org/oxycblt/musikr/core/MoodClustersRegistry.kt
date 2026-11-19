/*
 * Copyright (c) 2026 Tonewise Project
 * MoodClustersRegistry.kt is part of Tonewise.
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

package org.oxycblt.musikr.core

/**
 * Single-source-of-truth loader for `protocol/dictionaries/mood_clusters.json`, packaged into
 * `:musikr` classpath resources at build time so Rust, Desktop, and Android share one dictionary.
 */
object MoodClustersRegistry {
    private const val RESOURCE_NAME = "mood_clusters.json"

    private data class LoadedDictionary(
        val clusterIds: List<String>,
        val synonymToCluster: Map<String, String>,
    )

    private val dictionary: LoadedDictionary by lazy { loadDictionary() }

    val clusterIds: List<String>
        get() = dictionary.clusterIds

    fun clusterCount(): Int = dictionary.clusterIds.size

    fun resolveMoodClusterId(rawMood: String): String? =
        dictionary.synonymToCluster[rawMood.trim().lowercase()]

    private fun loadDictionary(): LoadedDictionary {
        val stream =
            MoodClustersRegistry::class.java.classLoader?.getResourceAsStream(RESOURCE_NAME)
                ?: Thread.currentThread().contextClassLoader?.getResourceAsStream(RESOURCE_NAME)
                ?: throw IllegalStateException("Missing SSOT resource $RESOURCE_NAME on classpath")

        val raw = stream.bufferedReader(Charsets.UTF_8).use { it.readText() }
        @Suppress("UNCHECKED_CAST")
        val root =
            TonewiseCore.parseJson(raw) as? Map<String, Any?>
                ?: throw IllegalStateException("Invalid $RESOURCE_NAME root object")

        @Suppress("UNCHECKED_CAST")
        val rawClusters =
            root["clusters"] as? List<Any?>
                ?: throw IllegalStateException("Missing clusters array in $RESOURCE_NAME")

        val ids = ArrayList<String>(rawClusters.size)
        val map = HashMap<String, String>(rawClusters.size * 12)
        for (entry in rawClusters) {
            @Suppress("UNCHECKED_CAST")
            val cluster = entry as? Map<String, Any?> ?: continue
            val id = cluster["id"]?.toString()?.trim()?.takeIf { it.isNotEmpty() } ?: continue
            ids.add(id)
            map[id.lowercase()] = id
            val synonyms = cluster["synonyms"] as? List<*> ?: emptyList<Any?>()
            for (syn in synonyms) {
                val clean = syn?.toString()?.trim()?.lowercase()?.takeIf { it.isNotEmpty() } ?: continue
                map[clean] = id
            }
        }
        return LoadedDictionary(clusterIds = ids, synonymToCluster = map)
    }
}
