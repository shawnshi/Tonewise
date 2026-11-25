/*
 * Copyright (c) 2026 Tonewise Project
 * SceneRuleEvaluator.kt is part of Tonewise.
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

package com.shawnshi.tonewise.sync

import org.oxycblt.musikr.Song
import org.oxycblt.musikr.core.MoodClustersRegistry
import org.oxycblt.musikr.core.TonewiseCore
import org.oxycblt.musikr.tag.Name

/**
 * 5D scene/filter rule JSON builders, SSOT mood-cluster lookup, and library track serialization
 * delegated to Rust `tonewise-rules` via [TonewiseCore].
 */
object SceneRuleEvaluator {
    fun resolveMoodClusterId(rawMood: String): String? =
        MoodClustersRegistry.resolveMoodClusterId(rawMood)

    private fun predicateMap(
        field: String,
        op: String,
        values: Collection<Any?>,
    ): Map<String, Any?> =
        linkedMapOf(
            "predicate" to
                linkedMapOf(
                    "field" to field,
                    "op" to op,
                    "values" to values.toList(),
                )
        )

    fun buildFocusFilterSceneJson(spec: FocusFilterSpec): String? {
        val predicates = ArrayList<Map<String, Any?>>()
        fun addFacet(field: String, map: Map<String, FocusPillState>) {
            val includes =
                map.entries
                    .filter { it.value == FocusPillState.INCLUDE }
                    .map { it.key.trim() }
                    .filter { it.isNotEmpty() }
            if (includes.isNotEmpty()) {
                predicates.add(predicateMap(field, "in", includes))
            }
            val excludes =
                map.entries
                    .filter { it.value == FocusPillState.EXCLUDE }
                    .map { it.key.trim() }
                    .filter { it.isNotEmpty() }
            if (excludes.isNotEmpty()) {
                predicates.add(predicateMap(field, "not_in", excludes))
            }
        }
        addFacet("dj_scene", spec.scenes)
        addFacet("dj_vocals", spec.vocals)
        addFacet("dj_energy", spec.energies)
        addFacet("tempo_bucket", spec.tempos)
        addFacet("mood_cluster", spec.moods)
        spec.unplayedDaysGte?.let { days ->
            predicates.add(predicateMap("unplayed_days_gte", "gte", listOf(days)))
        }
        val effectiveArc = spec.sortArc.ifBlank { "flat" }
        if (predicates.isEmpty() && (effectiveArc == "flat" || effectiveArc == "work_shuffle")) {
            return null
        }
        return TonewiseCore.stringifyJson(
            linkedMapOf(
                "id" to "00000000-0000-4000-8000-000000000003",
                "title" to "Focus Boolean Funnel",
                "updated_at_ms" to 0,
                "sort_arc" to effectiveArc,
                "tempo_null_passthrough" to true,
                "rule" to linkedMapOf("all" to predicates),
            )
        )
    }

    fun buildDjFilterSceneJson(
        scene: String?,
        vocals: String?,
        energy: String?,
        excludeVocal: Boolean = false,
        tempoBucket: String? = null,
        moodCluster: String? = null,
        sortArc: String = "flat",
    ): String? {
        val predicates = ArrayList<Map<String, Any?>>()
        scene?.takeIf { it.isNotBlank() && it != "all" }?.let {
            predicates.add(predicateMap("dj_scene", "in", listOf(it)))
        }
        if (excludeVocal) {
            predicates.add(linkedMapOf("not" to predicateMap("dj_vocals", "eq", listOf("vocal"))))
        } else {
            vocals?.takeIf { it.isNotBlank() && it != "all" }?.let {
                predicates.add(predicateMap("dj_vocals", "eq", listOf(it)))
            }
        }
        energy?.takeIf { it.isNotBlank() && it != "all" }?.let {
            predicates.add(predicateMap("dj_energy", "eq", listOf(it)))
        }
        tempoBucket?.takeIf { it.isNotBlank() && it != "all" }?.let {
            predicates.add(predicateMap("tempo_bucket", "eq", listOf(it)))
        }
        moodCluster?.takeIf { it.isNotBlank() && it != "all" }?.let {
            predicates.add(predicateMap("mood_cluster", "in", listOf(it)))
        }
        val effectiveArc = sortArc.ifBlank { "flat" }
        if (predicates.isEmpty() && (effectiveArc == "flat" || effectiveArc == "work_shuffle")) {
            return null
        }
        return TonewiseCore.stringifyJson(
            linkedMapOf(
                "id" to "00000000-0000-4000-8000-000000000002",
                "title" to "DJ 5D Matrix Filter",
                "updated_at_ms" to 0,
                "sort_arc" to effectiveArc,
                "tempo_null_passthrough" to true,
                "rule" to linkedMapOf("all" to predicates),
            )
        )
    }

    fun buildTracksJson(
        songs: Collection<Song>,
        nowMs: Long,
        getDjScenes: (Song) -> List<String>,
        getDjVocals: (Song) -> String,
        getDjEnergy: (Song) -> String,
        getDjTempo: (Song) -> String?,
        getMoods: (Song) -> List<String>,
        getPlayStats: (Song) -> Triple<Int, Long, Boolean>,
        getRatingOverride: (String) -> String?,
    ): String {
        val list = ArrayList<Map<String, Any?>>(songs.size)
        for (song in songs) {
            val uid = song.uid.toString()
            val primaryGenre = song.genres.mapNotNull { (it.name as? Name.Known)?.raw }.firstOrNull() ?: ""
            val (playCount, lastPlayedMs, loved) = getPlayStats(song)
            val unplayedDays =
                when {
                    playCount == 0 -> 120
                    lastPlayedMs > 0L -> ((nowMs - lastPlayedMs).coerceAtLeast(0L) / 86_400_000L).toInt()
                    else -> 1
                }
            val rating = getRatingOverride(uid)?.toIntOrNull() ?: if (loved) 5 else 0
            val workName = song.workName?.trim()?.takeIf { it.isNotEmpty() }
            val workKey = workName?.let { "${song.album.uid}::${it.lowercase()}" }
            val movementOrder = song.movementNumber?.takeIf { it > 0 }
            val composers = song.composers.mapNotNull { it.trim().takeIf { c -> c.isNotEmpty() } }
            val conductors = song.conductors.mapNotNull { it.trim().takeIf { c -> c.isNotEmpty() } }
            list.add(
                linkedMapOf(
                    "track_uid" to uid,
                    "title" to song.name.raw,
                    "dj_scene" to getDjScenes(song),
                    "dj_vocals" to getDjVocals(song),
                    "dj_energy" to getDjEnergy(song),
                    "tempo_bucket" to getDjTempo(song),
                    "moods" to getMoods(song),
                    "genre" to primaryGenre,
                    "rating" to rating,
                    "unplayed_days" to unplayedDays,
                    "work_key" to workKey,
                    "movement_order" to movementOrder,
                    "composers" to composers,
                    "conductors" to conductors,
                    "work_name" to workName,
                )
            )
        }
        return TonewiseCore.stringifyJson(list)
    }

    fun extractSortArc(rawSceneJson: String): String {
        @Suppress("UNCHECKED_CAST")
        val root = TonewiseCore.parseJson(rawSceneJson) as? Map<String, Any?> ?: return "flat"
        return root["sort_arc"]?.toString()?.trim()?.lowercase()?.takeIf { it.isNotEmpty() } ?: "flat"
    }

    fun parseScenesJson(raw: String): List<SceneBanner> {
        @Suppress("UNCHECKED_CAST")
        val root = TonewiseCore.parseJson(raw) as? Map<String, Any?> ?: return emptyList()
        @Suppress("UNCHECKED_CAST")
        val arr = root["scenes"] as? List<Any?> ?: return emptyList()
        val list = ArrayList<SceneBanner>(arr.size)
        for (entry in arr) {
            @Suppress("UNCHECKED_CAST")
            val obj = entry as? Map<String, Any?> ?: continue
            if (obj["deleted"] == true) continue
            val id = obj["id"]?.toString()?.takeIf { it.isNotEmpty() } ?: continue
            val title = obj["title"]?.toString() ?: continue
            val subtitle = humanizeSceneSubtitle(obj["subtitle"]?.toString().orEmpty())
            val accentHex = obj["accent_hex"]?.toString()?.takeIf { it.isNotEmpty() } ?: "#005EB8"
            list.add(
                SceneBanner(
                    id = id,
                    title = title,
                    subtitle = subtitle,
                    accentHex = accentHex,
                    rawSceneJson = TonewiseCore.stringifyJson(obj),
                )
            )
        }
        return list
    }

    fun humanizeSceneSubtitle(raw: String): String {
        val clean = raw.trim()
        return when {
            clean.equals("DJ_SCENE = focus", ignoreCase = true) -> "器乐与低干扰声场 · 平直叙事"
            clean.equals("DJ_SCENE = coding", ignoreCase = true) -> "作品成组漫游 · 沉浸式编码背景"
            clean.equals("DJ_SCENE = relax", ignoreCase = true) -> "能量渐弱曲线 · 夜间慢速收束"
            clean.equals("DJ_SCENE = energy", ignoreCase = true) -> "能量递进弧线 · 高动态节拍"
            clean.equals("DJ_SCENE = pop", ignoreCase = true) -> "人声与旋律精选 · 明快律动"
            else -> clean
        }
    }

    const val DEFAULT_SCENES_JSON = """
        {
          "schema_version": 1,
          "scenes": [
            {
              "id": "11111111-1111-4111-8111-111111111111",
              "title": "Focus · 深度专注",
              "subtitle": "器乐与低干扰声场 · 平直叙事",
              "accent_hex": "#2B5B84",
              "updated_at_ms": 1763251200000,
              "sort_arc": "flat",
              "tempo_null_passthrough": true,
              "rule": {
                "all": [
                  { "predicate": { "field": "dj_scene", "op": "in", "values": ["focus"] } }
                ]
              }
            },
            {
              "id": "22222222-2222-4222-8222-222222222222",
              "title": "Coding · 心流编程",
              "subtitle": "作品成组漫游 · 沉浸式编码背景",
              "accent_hex": "#3B7A57",
              "updated_at_ms": 1763251200000,
              "sort_arc": "work_shuffle",
              "tempo_null_passthrough": true,
              "rule": {
                "all": [
                  { "predicate": { "field": "dj_scene", "op": "in", "values": ["coding"] } }
                ]
              }
            },
            {
              "id": "33333333-3333-4333-8333-333333333333",
              "title": "Relax · 松弛休憩",
              "subtitle": "能量渐弱曲线 · 夜间慢速收束",
              "accent_hex": "#5B6E8C",
              "updated_at_ms": 1763251200000,
              "sort_arc": "energy_desc_sleep",
              "tempo_null_passthrough": true,
              "rule": {
                "all": [
                  { "predicate": { "field": "dj_scene", "op": "in", "values": ["relax"] } }
                ]
              }
            },
            {
              "id": "55555555-5555-4555-8555-555555555555",
              "title": "Energy · 高能律动",
              "subtitle": "能量递进弧线 · 高动态节拍",
              "accent_hex": "#C85A32",
              "updated_at_ms": 1763251200000,
              "sort_arc": "energy_asc",
              "tempo_null_passthrough": true,
              "rule": {
                "all": [
                  { "predicate": { "field": "dj_scene", "op": "in", "values": ["energy"] } }
                ]
              }
            },
            {
              "id": "66666666-6666-4666-8666-666666666666",
              "title": "Pop · 流行精选",
              "subtitle": "人声与旋律精选 · 明快律动",
              "accent_hex": "#9E4770",
              "updated_at_ms": 1763251200000,
              "sort_arc": "flat",
              "tempo_null_passthrough": true,
              "rule": {
                "all": [
                  { "predicate": { "field": "dj_scene", "op": "in", "values": ["pop"] } }
                ]
              }
            },
            {
              "id": "44444444-4444-4444-8444-444444444444",
              "title": "沉睡馆藏唤醒",
              "subtitle": "尚未播放或超过 90 天未重温的本地曲库",
              "accent_hex": "#8B5E3C",
              "updated_at_ms": 1763251200000,
              "sort_arc": "rediscovery_90d",
              "tempo_null_passthrough": true,
              "rule": {
                "all": [
                  { "predicate": { "field": "unplayed_days_gte", "op": "gte", "values": [90] } }
                ]
              }
            }
          ]
        }
    """

    const val REDISCOVERY_90D_SCENE_JSON =
        """{"id":"44444444-4444-4444-8444-444444444444","title":"沉睡馆藏唤醒","updated_at_ms":1763251200000,"sort_arc":"rediscovery_90d","tempo_null_passthrough":true,"rule":{"all":[{"predicate":{"field":"unplayed_days_gte","op":"gte","values":[90]}}]}}"""
}
