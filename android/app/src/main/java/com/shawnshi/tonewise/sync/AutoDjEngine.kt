/*
 * Copyright (c) 2026 Tonewise Project
 * AutoDjEngine.kt is part of Tonewise.
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

import java.util.Calendar
import kotlin.math.abs
import kotlin.math.log10
import kotlin.math.roundToInt
import kotlin.random.Random

data class AutoDjTrackCandidate(
    val trackUid: String,
    val title: String,
    val artist: String,
    val effectivePlayCount: Int,
    val rating: Int,
    val tempoBpm: Double?,
    val tempoBucket: String?,
    val djEnergy: String?,
    val djVocals: String?,
    val djScenes: List<String>,
    val moods: List<String>,
    val lastPlayedTsMs: Long = 0L,
    val workKey: String? = null,
    val movementNumber: Int? = null,
    val discNumber: Int = 0,
    val trackNumber: Int = 0,
    val relPath: String = "",
)

data class AutoDjTimeProfile(
    val periodId: String,
    val periodLabel: String,
    val description: String,
    val targetBpmCenter: Double,
    val targetBpmMin: Double,
    val targetBpmMax: Double,
    val targetEnergy: String,
    val targetValence: Double,
    val targetArousal: Double,
)

data class AutoDjSceneContext(
    val sceneId: String,
    val sceneTitle: String,
    val matchedUids: Set<String> = emptySet(),
    val sortArc: String = "flat",
)

data class AutoDjResult(
    val profile: AutoDjTimeProfile,
    val sceneTitle: String?,
    val orderedTrackUids: List<String>,
    val frequentCount: Int,
    val discoveryCount: Int,
    val sceneMatchCount: Int,
    val maxAdjacentBpmDelta: Double,
    val summaryText: String,
)

private data class SceneAcousticBias(
    val targetBpmCenter: Double,
    val targetBpmMin: Double,
    val targetBpmMax: Double,
    val targetArousal: Double,
    val targetValence: Double,
    val preferInstrumental: Boolean = false,
    val preferVocal: Boolean = false,
    val frequentRatio: Double = 0.70,
    val preferRediscovery: Boolean = false,
)

/**
 * Generates a cohesive 50-track Auto DJ mix keyed to the current local time of day and an optional
 * Scene context, keeping multi-movement classical works atomic and avoiding jarring BPM/mood jumps.
 */
object AutoDjEngine {
    const val DEFAULT_TARGET_COUNT = 50

    private val SCENE_BIASES =
        mapOf(
            "focus" to
                SceneAcousticBias(
                    targetBpmCenter = 90.0,
                    targetBpmMin = 68.0,
                    targetBpmMax = 112.0,
                    targetArousal = 0.34,
                    targetValence = 0.56,
                    preferInstrumental = true,
                ),
            "coding" to
                SceneAcousticBias(
                    targetBpmCenter = 106.0,
                    targetBpmMin = 84.0,
                    targetBpmMax = 128.0,
                    targetArousal = 0.48,
                    targetValence = 0.56,
                    preferInstrumental = true,
                ),
            "relax" to
                SceneAcousticBias(
                    targetBpmCenter = 76.0,
                    targetBpmMin = 56.0,
                    targetBpmMax = 98.0,
                    targetArousal = 0.25,
                    targetValence = 0.64,
                ),
            "energy" to
                SceneAcousticBias(
                    targetBpmCenter = 126.0,
                    targetBpmMin = 102.0,
                    targetBpmMax = 152.0,
                    targetArousal = 0.76,
                    targetValence = 0.68,
                ),
            "pop" to
                SceneAcousticBias(
                    targetBpmCenter = 110.0,
                    targetBpmMin = 88.0,
                    targetBpmMax = 132.0,
                    targetArousal = 0.58,
                    targetValence = 0.74,
                    preferVocal = true,
                ),
            "rediscovery_90d" to
                SceneAcousticBias(
                    targetBpmCenter = 94.0,
                    targetBpmMin = 70.0,
                    targetBpmMax = 120.0,
                    targetArousal = 0.42,
                    targetValence = 0.60,
                    frequentRatio = 0.15,
                    preferRediscovery = true,
                ),
        )

    private val POSITIVE_MOODS =
        setOf("warm", "romantic", "cheerful", "bright", "uplifting", "groovy", "happy")
    private val CALM_MOODS =
        setOf("calm", "chill", "mellow", "soothing", "peaceful", "relax", "dreamy")
    private val FOCUS_MOODS = setOf("focus", "coding", "contemplative")
    private val TENSE_MOODS =
        setOf("melancholic", "sad", "dark", "somber", "tragic", "aggressive", "dramatic", "energetic")

    fun resolveTimeProfile(hourOfDay: Int = Calendar.getInstance().get(Calendar.HOUR_OF_DAY)): AutoDjTimeProfile {
        val hour = ((hourOfDay % 24) + 24) % 24
        return when (hour) {
            in 5..8 ->
                AutoDjTimeProfile(
                    periodId = "morning",
                    periodLabel = "清晨唤醒",
                    description = "05:00–08:59 · 明快温和开启（中等偏舒缓节奏 · 暖调情绪）",
                    targetBpmCenter = 96.0,
                    targetBpmMin = 76.0,
                    targetBpmMax = 116.0,
                    targetEnergy = "low",
                    targetValence = 0.68,
                    targetArousal = 0.38,
                )
            in 9..13 ->
                AutoDjTimeProfile(
                    periodId = "day_focus",
                    periodLabel = "日间专注",
                    description = "09:00–13:59 · 稳定心流推动（均速稳定律动 · 克制情绪起伏）",
                    targetBpmCenter = 102.0,
                    targetBpmMin = 82.0,
                    targetBpmMax = 122.0,
                    targetEnergy = "medium",
                    targetValence = 0.58,
                    targetArousal = 0.46,
                )
            in 14..17 ->
                AutoDjTimeProfile(
                    periodId = "afternoon",
                    periodLabel = "午后律动",
                    description = "14:00–17:59 · 舒适松弛律动（温和明快 · 中等能量延续）",
                    targetBpmCenter = 108.0,
                    targetBpmMin = 86.0,
                    targetBpmMax = 126.0,
                    targetEnergy = "medium",
                    targetValence = 0.70,
                    targetArousal = 0.52,
                )
            in 18..21 ->
                AutoDjTimeProfile(
                    periodId = "evening",
                    periodLabel = "晚间漫游",
                    description = "18:00–21:59 · 舒缓放松过渡（柔缓中慢板 · 温暖沉浸氛围）",
                    targetBpmCenter = 92.0,
                    targetBpmMin = 72.0,
                    targetBpmMax = 114.0,
                    targetEnergy = "low",
                    targetValence = 0.64,
                    targetArousal = 0.40,
                )
            else ->
                AutoDjTimeProfile(
                    periodId = "late_night",
                    periodLabel = "深夜静谧",
                    description = "22:00–04:59 · 沉静低扰伴随（低能量慢板 · 低刺激平滑衔接）",
                    targetBpmCenter = 74.0,
                    targetBpmMin = 54.0,
                    targetBpmMax = 96.0,
                    targetEnergy = "low",
                    targetValence = 0.52,
                    targetArousal = 0.24,
                )
        }
    }

    fun generate(
        candidates: List<AutoDjTrackCandidate>,
        hourOfDay: Int = Calendar.getInstance().get(Calendar.HOUR_OF_DAY),
        targetCount: Int = DEFAULT_TARGET_COUNT,
        sceneContext: AutoDjSceneContext? = null,
        nowMs: Long = System.currentTimeMillis(),
        randomSeed: Int? = null,
    ): AutoDjResult {
        val timeProfile = resolveTimeProfile(hourOfDay)
        if (candidates.isEmpty() || targetCount <= 0) {
            val label = sceneContext?.sceneTitle?.let { "$it · ${timeProfile.periodLabel}" } ?: timeProfile.periodLabel
            return AutoDjResult(
                profile = timeProfile,
                sceneTitle = sceneContext?.sceneTitle,
                orderedTrackUids = emptyList(),
                frequentCount = 0,
                discoveryCount = 0,
                sceneMatchCount = 0,
                maxAdjacentBpmDelta = 0.0,
                summaryText = "$label AutoDJ：当前曲库无可编排曲目。",
            )
        }

        val sceneBias = resolveSceneBias(sceneContext)
        val effectiveProfile = blendProfile(timeProfile, sceneBias)
        val frequentRatio = sceneBias?.frequentRatio ?: 0.70

        val rng = if (randomSeed != null) Random(randomSeed) else Random.Default
        val deduplicated =
            LinkedHashMap<String, AutoDjTrackCandidate>(candidates.size).apply {
                for (c in candidates) {
                    putIfAbsent(c.trackUid, c)
                }
            }
        val features =
            deduplicated.values.mapIndexed { idx, candidate ->
                buildFeatureNode(
                    candidate = candidate,
                    profile = effectiveProfile,
                    sceneContext = sceneContext,
                    sceneBias = sceneBias,
                    nowMs = nowMs,
                    rng = rng,
                    index = idx,
                )
            }

        val total = minOf(targetCount, features.size)
        val targetFrequent = (total * frequentRatio).roundToInt().coerceIn(0, total)
        val targetDiscovery = total - targetFrequent

        val units = buildAtomicUnits(features)

        val freqPool: MutableList<UnitNode>
        val discPool: MutableList<UnitNode>
        if (sceneBias?.preferRediscovery == true) {
            val discCandidate = units.filter { it.isRediscoveryCandidate }
            if (discCandidate.sumOf { it.nodes.size } >= targetDiscovery) {
                discPool = discCandidate.toMutableList()
                freqPool = units.filter { !it.isRediscoveryCandidate }.toMutableList()
            } else {
                discPool = units.filter { it.effectivePlayCount <= 1 || it.isRediscoveryCandidate }.toMutableList()
                freqPool = units.filter { it !in discPool }.toMutableList()
            }
        } else {
            val freqAtLeast2 = units.filter { it.effectivePlayCount >= 2 }.sumOf { it.nodes.size }
            val freqAtLeast1 = units.filter { it.effectivePlayCount >= 1 }.sumOf { it.nodes.size }
            when {
                freqAtLeast2 >= targetFrequent -> {
                    freqPool = units.filter { it.effectivePlayCount >= 2 }.toMutableList()
                    discPool = units.filter { it.effectivePlayCount <= 1 }.toMutableList()
                }
                freqAtLeast1 > 0 -> {
                    freqPool = units.filter { it.effectivePlayCount >= 1 }.toMutableList()
                    discPool = units.filter { it.effectivePlayCount == 0 }.toMutableList()
                }
                else -> {
                    val orderedFallback =
                        units.sortedWith(
                            compareByDescending<UnitNode> { it.averageCompatibilityScore }
                                .thenByDescending { it.averageRating }
                                .thenBy { it.jitter }
                        )
                    freqPool = ArrayList()
                    discPool = ArrayList()
                    var freqTracks = 0
                    for (u in orderedFallback) {
                        if (freqTracks < targetFrequent && freqTracks + u.nodes.size <= total) {
                            freqPool.add(u)
                            freqTracks += u.nodes.size
                        } else {
                            discPool.add(u)
                        }
                    }
                }
            }
        }

        for (u in freqPool) {
            u.poolWeight =
                u.averageCompatibilityScore +
                    log10(1.0 + u.effectivePlayCount) * 1.15 +
                    u.averageRating * 0.12 +
                    u.jitter * 0.40
            u.isFrequent = true
            for (n in u.nodes) {
                n.poolWeight = u.poolWeight
                n.isFrequent = true
            }
        }

        for (u in discPool) {
            val rarityBonus = if (u.effectivePlayCount == 0) 1.0 else 0.65
            u.poolWeight =
                u.averageCompatibilityScore +
                    rarityBonus +
                    (if (u.averageRating >= 4.5) 0.25 else 0.0) +
                    u.jitter * 0.45
            u.isFrequent = false
            for (n in u.nodes) {
                n.poolWeight = u.poolWeight
                n.isFrequent = false
            }
        }

        val selectedUnits = ArrayList<UnitNode>(units.size)
        val usedUnitKeys = HashSet<String>()
        val artistCounts = HashMap<String, Int>()
        val matchedUnits = if (sceneContext != null) units.filter { it.isSceneMatched } else emptyList()
        var selectedTracks = 0

        if (matchedUnits.isNotEmpty()) {
            val matchedFreq = freqPool.filter { it.isSceneMatched }
            val matchedDisc = discPool.filter { it.isSceneMatched }
            selectedTracks +=
                pickCohesiveUnitSubset(
                    pool = matchedFreq,
                    neededTracks = targetFrequent,
                    maxTotalTracks = total,
                    dest = selectedUnits,
                    usedUnitKeys = usedUnitKeys,
                    artistCounts = artistCounts,
                    maxPerArtist = 2,
                )
            selectedTracks +=
                pickCohesiveUnitSubset(
                    pool = matchedDisc,
                    neededTracks = minOf(targetDiscovery, (total - selectedTracks).coerceAtLeast(0)),
                    maxTotalTracks = (total - selectedTracks).coerceAtLeast(0),
                    dest = selectedUnits,
                    usedUnitKeys = usedUnitKeys,
                    artistCounts = artistCounts,
                    maxPerArtist = 2,
                )
            if (selectedTracks < total) {
                val remainingMatched =
                    matchedUnits
                        .filter { it.unitKey !in usedUnitKeys }
                        .onEach { it.poolWeight = it.averageCompatibilityScore + it.jitter * 0.35 }
                selectedTracks +=
                    pickCohesiveUnitSubset(
                        pool = remainingMatched,
                        neededTracks = total - selectedTracks,
                        maxTotalTracks = total - selectedTracks,
                        dest = selectedUnits,
                        usedUnitKeys = usedUnitKeys,
                        artistCounts = artistCounts,
                        maxPerArtist = 4,
                    )
            }
        }

        if (selectedTracks < total) {
            val fallbackFreq = freqPool.filter { it.unitKey !in usedUnitKeys }
            val fallbackDisc = discPool.filter { it.unitKey !in usedUnitKeys }
            val freqPickedSoFar = selectedUnits.sumOf { if (it.isFrequent) it.nodes.size else 0 }
            val discPickedSoFar = selectedTracks - freqPickedSoFar
            val needFreq = (targetFrequent - freqPickedSoFar).coerceAtLeast(0)
            selectedTracks +=
                pickCohesiveUnitSubset(
                    pool = fallbackFreq,
                    neededTracks = minOf(needFreq, total - selectedTracks),
                    maxTotalTracks = (total - selectedTracks).coerceAtLeast(0),
                    dest = selectedUnits,
                    usedUnitKeys = usedUnitKeys,
                    artistCounts = artistCounts,
                    maxPerArtist = 2,
                )
            val needDisc = (targetDiscovery - discPickedSoFar).coerceAtLeast(0)
            selectedTracks +=
                pickCohesiveUnitSubset(
                    pool = fallbackDisc,
                    neededTracks = minOf(needDisc, total - selectedTracks),
                    maxTotalTracks = (total - selectedTracks).coerceAtLeast(0),
                    dest = selectedUnits,
                    usedUnitKeys = usedUnitKeys,
                    artistCounts = artistCounts,
                    maxPerArtist = 2,
                )
        }

        if (selectedTracks < total) {
            val remainingPool =
                units
                    .filter { it.unitKey !in usedUnitKeys }
                    .onEach { it.poolWeight = it.averageCompatibilityScore + it.jitter * 0.35 }
            selectedTracks +=
                pickCohesiveUnitSubset(
                    pool = remainingPool,
                    neededTracks = total - selectedTracks,
                    maxTotalTracks = total - selectedTracks,
                    dest = selectedUnits,
                    usedUnitKeys = usedUnitKeys,
                    artistCounts = artistCounts,
                    maxPerArtist = 4,
                )
        }

        if (selectedUnits.isEmpty() && units.isNotEmpty()) {
            selectedUnits.add((matchedUnits.ifEmpty { units }).minBy { it.nodes.size })
        }

        val sequencedUnits =
            when (sceneContext?.sortArc?.trim()?.lowercase()) {
                "energy_asc" ->
                    selectedUnits
                        .sortedWith(compareBy<UnitNode> { unitEnergyScore(it) }.thenBy { it.entryNode.bpm })
                        .toMutableList()
                "energy_desc_sleep" ->
                    selectedUnits
                        .sortedWith(
                            compareByDescending<UnitNode> { unitEnergyScore(it) }
                                .thenByDescending { it.entryNode.bpm }
                        )
                        .toMutableList()
                "rediscovery_90d" ->
                    selectedUnits
                        .sortedWith(
                            compareByDescending<UnitNode> { it.isRediscoveryCandidate }
                                .thenBy { it.effectivePlayCount }
                                .thenBy { it.minLastPlayedTsMs }
                        )
                        .toMutableList()
                "work_shuffle" -> selectedUnits.toMutableList().apply { shuffle(rng) }
                else ->
                    sequenceUnitsSmoothly(selectedUnits, effectiveProfile, frequentRatio).also {
                        applyLocalUnitSmoothingSwaps(it)
                    }
            }

        val sequenced = sequencedUnits.flatMap { it.nodes }

        var maxBpmDelta = 0.0
        for (i in 1 until sequenced.size) {
            val delta = abs(sequenced[i].bpm - sequenced[i - 1].bpm)
            if (delta > maxBpmDelta) maxBpmDelta = delta
        }

        val finalFreq = sequenced.count { it.isFrequent }
        val finalDisc = sequenced.size - finalFreq
        val sceneMatchCount = sequenced.count { it.isSceneMatched }
        val roundedDelta = ((maxBpmDelta * 10.0).roundToInt()) / 10.0
        val modeLabel =
            if (sceneContext != null) {
                "「${sceneContext.sceneTitle} · ${timeProfile.periodLabel}」"
            } else {
                "「${timeProfile.periodLabel}」"
            }
        val scenePart =
            if (sceneContext != null && sceneMatchCount > 0) {
                "场景命中 $sceneMatchCount 首 · "
            } else {
                ""
            }
        val summary =
            "Auto DJ 已按$modeLabel 生成 ${sequenced.size} 首（${scenePart}常听 $finalFreq 首 · 探索 $finalDisc 首 · 最大邻曲 BPM 差 ${formatDelta(roundedDelta)}）"

        return AutoDjResult(
            profile = timeProfile,
            sceneTitle = sceneContext?.sceneTitle,
            orderedTrackUids = sequenced.map { it.candidate.trackUid },
            frequentCount = finalFreq,
            discoveryCount = finalDisc,
            sceneMatchCount = sceneMatchCount,
            maxAdjacentBpmDelta = roundedDelta,
            summaryText = summary,
        )
    }

    private fun resolveSceneBias(sceneContext: AutoDjSceneContext?): SceneAcousticBias? {
        if (sceneContext == null) return null
        val id = sceneContext.sceneId.trim().lowercase()
        SCENE_BIASES[id]?.let { return it }
        BUILTIN_SCENE_UUID_TO_KEY[id]?.let { mapped ->
            SCENE_BIASES[mapped]?.let { return it }
        }
        return when (sceneContext.sortArc.trim().lowercase()) {
            "rediscovery_90d" -> SCENE_BIASES["rediscovery_90d"]
            "energy_desc_sleep" -> SCENE_BIASES["relax"]
            "energy_asc" -> SCENE_BIASES["energy"]
            "work_shuffle" -> SCENE_BIASES["coding"]
            else -> null
        }
    }

    private val BUILTIN_SCENE_UUID_TO_KEY =
        mapOf(
            "11111111-1111-4111-8111-111111111111" to "focus",
            "22222222-2222-4222-8222-222222222222" to "coding",
            "33333333-3333-4333-8333-333333333333" to "relax",
            "44444444-4444-4444-8444-444444444444" to "rediscovery_90d",
            "55555555-5555-4555-8555-555555555555" to "energy",
            "66666666-6666-4666-8666-666666666666" to "pop",
        )

    private fun blendProfile(
        timeProfile: AutoDjTimeProfile,
        sceneBias: SceneAcousticBias?,
    ): AutoDjTimeProfile {
        if (sceneBias == null) return timeProfile
        return timeProfile.copy(
            targetBpmCenter = sceneBias.targetBpmCenter * 0.60 + timeProfile.targetBpmCenter * 0.40,
            targetBpmMin = sceneBias.targetBpmMin * 0.60 + timeProfile.targetBpmMin * 0.40,
            targetBpmMax = sceneBias.targetBpmMax * 0.60 + timeProfile.targetBpmMax * 0.40,
            targetArousal = sceneBias.targetArousal * 0.60 + timeProfile.targetArousal * 0.40,
            targetValence = sceneBias.targetValence * 0.60 + timeProfile.targetValence * 0.40,
        )
    }

    private fun formatDelta(value: Double): String =
        if (abs(value - value.roundToInt()) < 0.05) {
            value.roundToInt().toString()
        } else {
            String.format(java.util.Locale.US, "%.1f", value)
        }

    private class FeatureNode(
        val candidate: AutoDjTrackCandidate,
        val originalIndex: Int,
        val bpm: Double,
        val arousal: Double,
        val valence: Double,
        val baseCompatibilityScore: Double,
        val jitter: Double,
        val primaryArtist: String,
        val isSceneMatched: Boolean,
        val isRediscoveryCandidate: Boolean,
    ) {
        var poolWeight: Double = 0.0
        var isFrequent: Boolean = false
    }

    private class UnitNode(
        val unitKey: String,
        val nodes: List<FeatureNode>,
        val effectivePlayCount: Int,
        val averageRating: Double,
        val averageCompatibilityScore: Double,
        val jitter: Double,
        val primaryArtist: String,
        val isRediscoveryCandidate: Boolean,
        val isSceneMatched: Boolean,
        val minLastPlayedTsMs: Long,
    ) {
        val entryNode: FeatureNode get() = nodes.first()
        val exitNode: FeatureNode get() = nodes.last()
        var poolWeight: Double = 0.0
        var isFrequent: Boolean = false
    }

    private fun unitEnergyScore(unit: UnitNode): Double =
        unit.nodes
            .map { node ->
                when (node.candidate.djEnergy?.trim()?.lowercase()) {
                    "low" -> 1.0
                    "medium" -> 2.0
                    "high" -> 3.0
                    else -> node.arousal * 3.0
                }
            }
            .average()

    private fun buildAtomicUnits(features: List<FeatureNode>): List<UnitNode> {
        val blocksByKey = LinkedHashMap<String, MutableList<FeatureNode>>(features.size)
        for (node in features) {
            val work = node.candidate.workKey?.trim()?.lowercase().orEmpty()
            val unitKey = if (work.isEmpty()) "track::${node.candidate.trackUid}" else "work::$work"
            blocksByKey.getOrPut(unitKey) { ArrayList(2) }.add(node)
        }

        val result = ArrayList<UnitNode>(blocksByKey.size)
        for ((key, list) in blocksByKey) {
            if (list.size > 1) {
                list.sortWith { a, b ->
                    val da = a.candidate.discNumber.takeIf { it > 0 } ?: Int.MAX_VALUE
                    val db = b.candidate.discNumber.takeIf { it > 0 } ?: Int.MAX_VALUE
                    if (da != db) return@sortWith da.compareTo(db)
                    val ma = a.candidate.movementNumber?.takeIf { it > 0 } ?: Int.MAX_VALUE
                    val mb = b.candidate.movementNumber?.takeIf { it > 0 } ?: Int.MAX_VALUE
                    if (ma != mb) return@sortWith ma.compareTo(mb)
                    val ta = a.candidate.trackNumber.takeIf { it > 0 } ?: Int.MAX_VALUE
                    val tb = b.candidate.trackNumber.takeIf { it > 0 } ?: Int.MAX_VALUE
                    if (ta != tb) return@sortWith ta.compareTo(tb)
                    val pathCmp = a.candidate.relPath.compareTo(b.candidate.relPath, ignoreCase = true)
                    if (pathCmp != 0) pathCmp else a.originalIndex.compareTo(b.originalIndex)
                }
            }
            result.add(
                UnitNode(
                    unitKey = key,
                    nodes = list,
                    effectivePlayCount = list.maxOf { it.candidate.effectivePlayCount },
                    averageRating = list.map { it.candidate.rating.toDouble() }.average(),
                    averageCompatibilityScore = list.map { it.baseCompatibilityScore }.average(),
                    jitter = list.first().jitter,
                    primaryArtist = list.first().primaryArtist,
                    isRediscoveryCandidate = list.any { it.isRediscoveryCandidate },
                    isSceneMatched = list.any { it.isSceneMatched },
                    minLastPlayedTsMs = list.minOf { it.candidate.lastPlayedTsMs },
                )
            )
        }
        return result
    }

    private fun buildFeatureNode(
        candidate: AutoDjTrackCandidate,
        profile: AutoDjTimeProfile,
        sceneContext: AutoDjSceneContext?,
        sceneBias: SceneAcousticBias?,
        nowMs: Long,
        rng: Random,
        index: Int,
    ): FeatureNode {
        val bpm = estimateBpm(candidate, profile)
        val arousal = estimateArousal(candidate, bpm)
        val valence = estimateValence(candidate)

        val bpmDelta = abs(bpm - profile.targetBpmCenter)
        val bandPenalty =
            if (bpm < profile.targetBpmMin - 12.0 || bpm > profile.targetBpmMax + 12.0) 7.5 else 0.0
        var compat =
            6.0 -
                (bpmDelta / 16.0) -
                bandPenalty -
                4.0 * abs(arousal - profile.targetArousal) -
                3.5 * abs(valence - profile.targetValence)

        val isSceneMatched = sceneContext != null && candidate.trackUid in sceneContext.matchedUids
        if (isSceneMatched) {
            compat += 6.0
        }

        val vocals = candidate.djVocals?.trim()?.lowercase().orEmpty()
        if (sceneBias?.preferInstrumental == true) {
            when (vocals) {
                "instrumental" -> compat += 1.8
                "vocal" -> compat -= 1.2
            }
        } else if (sceneBias?.preferVocal == true) {
            when (vocals) {
                "vocal" -> compat += 1.6
                "instrumental" -> compat -= 0.8
            }
        }

        val isRediscovery =
            candidate.effectivePlayCount == 0 ||
                candidate.lastPlayedTsMs <= 0L ||
                (nowMs - candidate.lastPlayedTsMs) >= 90L * 86_400_000L
        if (sceneBias?.preferRediscovery == true && isRediscovery) {
            compat += 2.5
        }

        val primaryArtist =
            candidate.artist
                .split(';', ',', '、', '，', '/', '×')
                .firstOrNull { it.isNotBlank() }
                ?.trim()
                .orEmpty()

        return FeatureNode(
            candidate = candidate,
            originalIndex = index,
            bpm = bpm,
            arousal = arousal,
            valence = valence,
            baseCompatibilityScore = compat,
            jitter = rng.nextDouble(),
            primaryArtist = primaryArtist,
            isSceneMatched = isSceneMatched,
            isRediscoveryCandidate = isRediscovery,
        )
    }

    private fun estimateBpm(c: AutoDjTrackCandidate, profile: AutoDjTimeProfile): Double {
        val bpm = c.tempoBpm
        if (bpm != null && bpm.isFinite() && bpm in 30.0..240.0) {
            return bpm
        }
        when (c.tempoBucket?.trim()) {
            "Very Slow" -> return 58.0
            "Slow" -> return 78.0
            "Moderate" -> return 105.0
            "Fast" -> return 135.0
            "Very Fast" -> return 160.0
        }
        return when (c.djEnergy?.trim()?.lowercase()) {
            "low" -> profile.targetBpmCenter - 8.0
            "high" -> profile.targetBpmCenter + 12.0
            else -> profile.targetBpmCenter
        }
    }

    private fun estimateArousal(c: AutoDjTrackCandidate, bpm: Double): Double {
        val energyScore =
            when (c.djEnergy?.trim()?.lowercase()) {
                "low" -> 0.24
                "medium" -> 0.52
                "high" -> 0.84
                else -> 0.48
            }
        val bpmNorm = ((bpm - 55.0) / 100.0).coerceIn(0.0, 1.0)
        return (energyScore * 0.60 + bpmNorm * 0.40).coerceIn(0.0, 1.0)
    }

    private fun estimateValence(c: AutoDjTrackCandidate): Double {
        var v = 0.55
        var moodHits = 0
        for (raw in c.moods + c.djScenes) {
            val m = raw.trim().lowercase()
            if (m.isEmpty()) continue
            when {
                m in POSITIVE_MOODS -> {
                    v += 0.22
                    moodHits++
                }
                m in CALM_MOODS -> {
                    v += 0.10
                    moodHits++
                }
                m in FOCUS_MOODS -> {
                    v += 0.02
                    moodHits++
                }
                m in TENSE_MOODS -> {
                    v -= 0.18
                    moodHits++
                }
            }
        }
        return if (moodHits > 0) v.coerceIn(0.15, 0.95) else 0.55
    }

    private fun pickCohesiveUnitSubset(
        pool: List<UnitNode>,
        neededTracks: Int,
        maxTotalTracks: Int,
        dest: MutableList<UnitNode>,
        usedUnitKeys: MutableSet<String>,
        artistCounts: MutableMap<String, Int>,
        maxPerArtist: Int,
    ): Int {
        val cap = minOf(neededTracks, maxTotalTracks)
        if (cap <= 0 || pool.isEmpty()) return 0
        val ranked = pool.sortedByDescending { it.poolWeight }
        var pickedTracks = 0

        for (pass in 0 until 2) {
            if (pickedTracks >= cap) break
            val enforceArtistCap = pass == 0
            for (unit in ranked) {
                if (pickedTracks >= cap) break
                if (unit.unitKey in usedUnitKeys) continue
                val unitLen = unit.nodes.size
                if (pickedTracks + unitLen > maxTotalTracks) continue
                if (pickedTracks > 0 && pickedTracks + unitLen > cap && unitLen > 1 && pickedTracks + 1 >= cap) {
                    continue
                }
                val artistKey = unit.primaryArtist.lowercase()
                val effectiveArtistCap =
                    if (unit.unitKey.startsWith("work::")) maxOf(maxPerArtist, 3) else maxPerArtist
                if (enforceArtistCap && artistKey.isNotEmpty() && (artistCounts[artistKey] ?: 0) >= effectiveArtistCap) {
                    continue
                }

                usedUnitKeys.add(unit.unitKey)
                if (artistKey.isNotEmpty()) {
                    artistCounts[artistKey] = (artistCounts[artistKey] ?: 0) + 1
                }
                dest.add(unit)
                pickedTracks += unitLen
            }
        }
        return pickedTracks
    }

    private fun sequenceUnitsSmoothly(
        selected: List<UnitNode>,
        profile: AutoDjTimeProfile,
        ratioTarget: Double,
    ): MutableList<UnitNode> {
        if (selected.size <= 2) return selected.toMutableList()
        val remaining = selected.toMutableList()
        val result = ArrayList<UnitNode>(selected.size)

        val first =
            remaining.minWith(
                compareBy<UnitNode> { if (it.isFrequent) 0 else 1 }
                    .thenBy {
                        abs(it.entryNode.bpm - profile.targetBpmCenter) +
                            12.0 * abs(it.entryNode.arousal - profile.targetArousal)
                    }
            )
        remaining.remove(first)
        result.add(first)
        var tracksSoFar = first.nodes.size
        var freqSoFar = if (first.isFrequent) first.nodes.size else 0

        while (remaining.isNotEmpty()) {
            val prev = result.last()
            var best: UnitNode? = null
            var bestCost = Double.MAX_VALUE
            for (cand in remaining) {
                val projTotal = tracksSoFar + cand.nodes.size
                val projFreq = freqSoFar + if (cand.isFrequent) cand.nodes.size else 0
                val cadencePenalty = abs(projFreq - (ratioTarget * projTotal)) * 0.45
                val cost = computeTransitionCost(prev.exitNode, cand.entryNode) + cadencePenalty
                if (cost < bestCost) {
                    bestCost = cost
                    best = cand
                }
            }
            val chosen = best ?: remaining.first()
            result.add(chosen)
            remaining.remove(chosen)
            tracksSoFar += chosen.nodes.size
            if (chosen.isFrequent) freqSoFar += chosen.nodes.size
        }
        return result
    }

    private fun computeTransitionCost(a: FeatureNode, b: FeatureNode): Double {
        val bpmDiff = abs(a.bpm - b.bpm)
        val bpmCost = (bpmDiff / 10.0) + if (bpmDiff > 14.0) (bpmDiff - 14.0) * 0.35 else 0.0
        val arousalCost = abs(a.arousal - b.arousal) * 4.5
        val valenceCost = abs(a.valence - b.valence) * 4.0
        val sameArtistCost =
            if (a.primaryArtist.isNotEmpty() && a.primaryArtist.equals(b.primaryArtist, ignoreCase = true)) {
                2.8
            } else {
                0.0
            }
        return bpmCost + arousalCost + valenceCost + sameArtistCost
    }

    private fun squaredPairCost(a: FeatureNode, b: FeatureNode): Double {
        val c = computeTransitionCost(a, b)
        return c * c
    }

    private fun applyLocalUnitSmoothingSwaps(seq: MutableList<UnitNode>) {
        val n = seq.size
        if (n < 4) return
        for (pass in 0 until 3) {
            var improved = false
            for (i in 1 until n - 2) {
                for (j in i + 1 until n - 1) {
                    if (seq[i].isFrequent != seq[j].isFrequent) continue
                    val before =
                        squaredPairCost(seq[i - 1].exitNode, seq[i].entryNode) +
                            (if (j == i + 1) {
                                squaredPairCost(seq[i].exitNode, seq[j].entryNode)
                            } else {
                                squaredPairCost(seq[i].exitNode, seq[i + 1].entryNode) +
                                    squaredPairCost(seq[j - 1].exitNode, seq[j].entryNode)
                            }) +
                            squaredPairCost(seq[j].exitNode, seq[j + 1].entryNode)

                    val after =
                        squaredPairCost(seq[i - 1].exitNode, seq[j].entryNode) +
                            (if (j == i + 1) {
                                squaredPairCost(seq[j].exitNode, seq[i].entryNode)
                            } else {
                                squaredPairCost(seq[j].exitNode, seq[i + 1].entryNode) +
                                    squaredPairCost(seq[j - 1].exitNode, seq[i].entryNode)
                            }) +
                            squaredPairCost(seq[i].exitNode, seq[j + 1].entryNode)

                    if (after + 1e-6 < before) {
                        val tmp = seq[i]
                        seq[i] = seq[j]
                        seq[j] = tmp
                        improved = true
                    }
                }
            }
            if (!improved) break
        }
    }
}
