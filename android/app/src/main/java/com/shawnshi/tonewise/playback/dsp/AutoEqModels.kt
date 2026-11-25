/*
 * Copyright (c) 2026 Tonewise Project
 * AutoEqModels.kt is part of Tonewise.
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

package com.shawnshi.tonewise.playback.dsp

import kotlin.math.abs
import org.oxycblt.musikr.core.TonewiseCore

enum class AutoEqGenreFamily(val slotKey: String, val displayName: String) {
    CLASSICAL("classical", "古典"),
    JAZZ("jazz", "爵士"),
    POP_RNB("pop_rnb", "流行 / R&B"),
    ROCK_METAL("rock_metal", "摇滚 / 金属"),
    FOLK_ACOUSTIC("folk_acoustic", "民谣 / 原声"),
    ELECTRONIC_DANCE("electronic_dance", "电子 / 舞曲"),
    OST_AMBIENT("ost_ambient", "原声 / 氛围"),
    OTHER("other", "其他");

    companion object {
        fun fromSlotKey(raw: String?): AutoEqGenreFamily {
            val clean = raw?.trim()?.lowercase().orEmpty()
            return entries.firstOrNull { it.slotKey == clean } ?: OTHER
        }
    }
}

enum class AutoEqVocalClass(val slotKey: String, val displayName: String) {
    VOCAL("vocal", "人声"),
    INSTRUMENTAL("instrumental", "纯器乐"),
    UNKNOWN("unknown", "未知");

    companion object {
        fun fromSlotKey(raw: String?): AutoEqVocalClass {
            val clean = raw?.trim()?.lowercase().orEmpty()
            return entries.firstOrNull { it.slotKey == clean } ?: UNKNOWN
        }
    }
}

private fun parseFilterList(rawList: Any?): List<BiquadBand> {
    @Suppress("UNCHECKED_CAST")
    val list = rawList as? List<Any?> ?: return DspChainConfig.defaultTenBands()
    if (list.isEmpty()) return DspChainConfig.defaultTenBands()
    return list.mapNotNull { entry ->
        @Suppress("UNCHECKED_CAST")
        val f = entry as? Map<String, Any?> ?: return@mapNotNull null
        BiquadBand(
            type = rustKindToBiquadType(f["kind"]?.toString() ?: "Peaking"),
            freqHz = (f["freq_hz"] as? Number)?.toDouble() ?: 1000.0,
            gainDb = (f["gain_db"] as? Number)?.toDouble() ?: 0.0,
            q = (f["q"] as? Number)?.toDouble() ?: 1.41,
            enabled = true,
        )
    }.ifEmpty { DspChainConfig.defaultTenBands() }
}

private fun bandsToMapList(bands: List<BiquadBand>): List<Map<String, Any?>> =
    bands.map { b ->
        linkedMapOf(
            "kind" to biquadTypeToRustKind(b.type),
            "freq_hz" to b.freqHz,
            "gain_db" to b.gainDb,
            "q" to b.q,
        )
    }

data class AutoEqRuleOverride(
    val preampDb: Double = 0.0,
    val bands: List<BiquadBand> = DspChainConfig.defaultTenBands(),
    val crossfeedOverride: Boolean? = null,
) {
    fun toMap(): Map<String, Any?> =
        linkedMapOf(
            "preamp_db" to preampDb,
            "filters" to bandsToMapList(bands),
            "crossfeed_override" to crossfeedOverride,
        )

    companion object {
        fun fromMap(map: Map<String, Any?>): AutoEqRuleOverride {
            val preamp = (map["preamp_db"] as? Number)?.toDouble() ?: 0.0
            val parsedBands = parseFilterList(map["filters"])
            val crossfeedOverride = map["crossfeed_override"] as? Boolean
            return AutoEqRuleOverride(
                preampDb = preamp,
                bands = parsedBands,
                crossfeedOverride = crossfeedOverride,
            )
        }
    }
}

data class AutoEqPolicy(
    val enabled: Boolean = false,
    val classicalAutoCrossfeed: Boolean = true,
    val lockWithinClassicalWork: Boolean = true,
    val updatedAtMs: Long = 0L,
    val customRules: Map<String, AutoEqRuleOverride> = emptyMap(),
) {
    fun toMap(): Map<String, Any?> =
        linkedMapOf(
            "enabled" to enabled,
            "classical_auto_crossfeed" to classicalAutoCrossfeed,
            "lock_within_classical_work" to lockWithinClassicalWork,
            "updated_at_ms" to updatedAtMs,
            "custom_rules" to customRules.mapValues { (_, v) -> v.toMap() },
        )

    fun toJsonString(): String = TonewiseCore.stringifyJson(toMap())

    companion object {
        @Suppress("UNCHECKED_CAST")
        fun fromMap(map: Map<String, Any?>): AutoEqPolicy {
            val rulesMap = map["custom_rules"] as? Map<String, Any?>
            val rules = LinkedHashMap<String, AutoEqRuleOverride>()
            if (rulesMap != null) {
                for ((key, v) in rulesMap) {
                    val ruleObj = v as? Map<String, Any?> ?: continue
                    rules[key] = AutoEqRuleOverride.fromMap(ruleObj)
                }
            }
            return AutoEqPolicy(
                enabled = map["enabled"] as? Boolean ?: false,
                classicalAutoCrossfeed = map["classical_auto_crossfeed"] as? Boolean ?: true,
                lockWithinClassicalWork = map["lock_within_classical_work"] as? Boolean ?: true,
                updatedAtMs = (map["updated_at_ms"] as? Number)?.toLong()?.coerceAtLeast(0L) ?: 0L,
                customRules = rules,
            )
        }

        @Suppress("UNCHECKED_CAST")
        fun fromJsonString(raw: String?): AutoEqPolicy {
            if (raw.isNullOrBlank()) return AutoEqPolicy()
            return runCatching {
                val parsed = TonewiseCore.parseJson(raw) as? Map<String, Any?> ?: return@runCatching AutoEqPolicy()
                fromMap(parsed)
            }.getOrDefault(AutoEqPolicy())
        }
    }
}

data class TrackAutoEqInput(
    val genres: List<String> = emptyList(),
    val djVocals: String? = null,
    val workName: String? = null,
    val workKey: String? = null,
)

data class ResolvedAutoEq(
    val applied: Boolean = false,
    val genreFamily: AutoEqGenreFamily = AutoEqGenreFamily.OTHER,
    val vocalClass: AutoEqVocalClass = AutoEqVocalClass.UNKNOWN,
    val matchedRuleKey: String = "other:unknown",
    val displayLabel: String = "其他 · 未知",
    val isCustomRule: Boolean = false,
    val lockedToPreviousWork: Boolean = false,
    val preampDb: Double = 0.0,
    val bands: List<BiquadBand> = DspChainConfig.defaultTenBands(),
    val enableCrossfeed: Boolean = false,
    val hasNonZeroEq: Boolean = false,
) {
    fun toMap(): Map<String, Any?> =
        linkedMapOf(
            "applied" to applied,
            "genre_family" to genreFamily.slotKey,
            "vocal_class" to vocalClass.slotKey,
            "matched_rule_key" to matchedRuleKey,
            "display_label" to displayLabel,
            "is_custom_rule" to isCustomRule,
            "locked_to_previous_work" to lockedToPreviousWork,
            "preamp_db" to preampDb,
            "enable_crossfeed" to enableCrossfeed,
            "has_non_zero_eq" to hasNonZeroEq,
            "filters" to bandsToMapList(bands),
        )

    companion object {
        fun fromMap(map: Map<String, Any?>): ResolvedAutoEq =
            ResolvedAutoEq(
                applied = map["applied"] as? Boolean ?: false,
                genreFamily = AutoEqGenreFamily.fromSlotKey(map["genre_family"]?.toString()),
                vocalClass = AutoEqVocalClass.fromSlotKey(map["vocal_class"]?.toString()),
                matchedRuleKey = map["matched_rule_key"]?.toString() ?: "other:unknown",
                displayLabel = map["display_label"]?.toString() ?: "其他 · 未知",
                isCustomRule = map["is_custom_rule"] as? Boolean ?: false,
                lockedToPreviousWork = map["locked_to_previous_work"] as? Boolean ?: false,
                preampDb = (map["preamp_db"] as? Number)?.toDouble() ?: 0.0,
                bands = parseFilterList(map["filters"]),
                enableCrossfeed = map["enable_crossfeed"] as? Boolean ?: false,
                hasNonZeroEq = map["has_non_zero_eq"] as? Boolean ?: false,
            )
    }
}

data class AutoEqCatalogEntry(
    val slotKey: String,
    val genreFamily: AutoEqGenreFamily,
    val vocalClass: AutoEqVocalClass,
    val label: String,
    val defaultPreampDb: Double,
    val defaultBands: List<BiquadBand>,
    val defaultCrossfeed: Boolean,
)

internal fun biquadTypeToRustKind(type: BiquadType): String =
    when (type) {
        BiquadType.PEAKING -> "Peaking"
        BiquadType.LOW_SHELF -> "LowShelf"
        BiquadType.HIGH_SHELF -> "HighShelf"
        BiquadType.LOW_PASS -> "LowPass"
        BiquadType.HIGH_PASS -> "HighPass"
    }

internal fun rustKindToBiquadType(kind: String): BiquadType =
    when (kind.trim().lowercase()) {
        "lowshelf", "low_shelf", "lsc", "ls" -> BiquadType.LOW_SHELF
        "highshelf", "high_shelf", "hsc", "hs" -> BiquadType.HIGH_SHELF
        "lowpass", "low_pass", "lp", "lpq" -> BiquadType.LOW_PASS
        "highpass", "high_pass", "hp", "hpq" -> BiquadType.HIGH_PASS
        else -> BiquadType.PEAKING
    }

object AutoEqResolverBridge {
    private val defaultCatalogCache: List<AutoEqCatalogEntry> by lazy {
        loadCatalogFromNativeOrFallback()
    }

    fun defaultCatalog(): List<AutoEqCatalogEntry> = defaultCatalogCache

    @Suppress("UNCHECKED_CAST")
    fun resolve(
        policy: AutoEqPolicy,
        track: TrackAutoEqInput,
        previousWorkKey: String? = null,
        previousResolved: ResolvedAutoEq? = null,
    ): ResolvedAutoEq {
        val reqMap =
            linkedMapOf<String, Any?>(
                "policy" to policy.toMap(),
                "track" to
                    linkedMapOf(
                        "genres" to track.genres,
                        "dj_vocals" to track.djVocals,
                        "work_name" to track.workName,
                        "work_key" to track.workKey,
                    ),
                "previous_work_key" to previousWorkKey,
                "previous_resolved" to previousResolved?.toMap(),
            )

        return runCatching {
            val raw = TonewiseCore.resolveAutoEqJson(TonewiseCore.stringifyJson(reqMap))
            val parsed = TonewiseCore.parseJson(raw) as Map<String, Any?>
            ResolvedAutoEq.fromMap(parsed)
        }.getOrElse {
            resolveFallbackJvm(policy, track, previousWorkKey, previousResolved)
        }
    }

    @Suppress("UNCHECKED_CAST")
    fun composeDsp(
        devicePreampDb: Double,
        deviceBands: List<BiquadBand>,
        contentPreampDb: Double,
        contentBands: List<BiquadBand>,
    ): Pair<Double, List<BiquadBand>> {
        val activeDeviceBands = deviceBands.filter { it.enabled }
        val activeContentBands = contentBands.filter { it.enabled }
        val reqMap =
            linkedMapOf<String, Any?>(
                "device_profile" to
                    linkedMapOf(
                        "preamp_db" to devicePreampDb,
                        "filters" to bandsToMapList(activeDeviceBands),
                    ),
                "content_profile" to
                    linkedMapOf(
                        "preamp_db" to contentPreampDb,
                        "filters" to bandsToMapList(activeContentBands),
                    ),
            )
        return runCatching {
            val raw = TonewiseCore.composeDspJson(TonewiseCore.stringifyJson(reqMap))
            val parsed = TonewiseCore.parseJson(raw) as Map<String, Any?>
            val preamp = (parsed["preamp_db"] as? Number)?.toDouble() ?: 0.0
            val outBands = parseFilterList(parsed["filters"])
            preamp to outBands
        }.getOrElse {
            composeFallbackJvm(devicePreampDb, activeDeviceBands, contentPreampDb, activeContentBands)
        }
    }

    @Suppress("UNCHECKED_CAST")
    private fun loadCatalogFromNativeOrFallback(): List<AutoEqCatalogEntry> {
        val native = runCatching {
            val raw = TonewiseCore.defaultAutoEqCatalogJson()
            val arr = TonewiseCore.parseJson(raw) as? List<Any?> ?: return@runCatching null
            arr.mapNotNull { entry ->
                val item = entry as? Map<String, Any?> ?: return@mapNotNull null
                AutoEqCatalogEntry(
                    slotKey = item["slot_key"]?.toString() ?: "other:unknown",
                    genreFamily = AutoEqGenreFamily.fromSlotKey(item["genre_family"]?.toString()),
                    vocalClass = AutoEqVocalClass.fromSlotKey(item["vocal_class"]?.toString()),
                    label = item["label"]?.toString() ?: "其他 · 未知",
                    defaultPreampDb = (item["default_preamp_db"] as? Number)?.toDouble() ?: 0.0,
                    defaultBands = parseFilterList(item["default_filters"]),
                    defaultCrossfeed = item["default_crossfeed"] as? Boolean ?: false,
                )
            }
        }.getOrNull()
        if (!native.isNullOrEmpty()) return native
        return fallbackCatalogJvm()
    }

    private fun resolveFallbackJvm(
        policy: AutoEqPolicy,
        track: TrackAutoEqInput,
        previousWorkKey: String?,
        previousResolved: ResolvedAutoEq?,
    ): ResolvedAutoEq {
        val genre = classifyGenreJvm(track.genres, track.workName)
        val vocal = classifyVocalJvm(track.djVocals, track.genres, track.workName)
        val exactKey = "${genre.slotKey}:${vocal.slotKey}"
        val label = "${genre.displayName} · ${vocal.displayName}"
        if (!policy.enabled) {
            return ResolvedAutoEq(
                applied = false,
                genreFamily = genre,
                vocalClass = vocal,
                matchedRuleKey = exactKey,
                displayLabel = label,
            )
        }
        if (policy.lockWithinClassicalWork &&
            genre == AutoEqGenreFamily.CLASSICAL &&
            !previousWorkKey.isNullOrBlank() &&
            !track.workKey.isNullOrBlank() &&
            previousWorkKey.trim().equals(track.workKey.trim(), ignoreCase = true) &&
            previousResolved?.applied == true
        ) {
            return previousResolved.copy(lockedToPreviousWork = true)
        }
        val fallbackEntry =
            fallbackCatalogJvm().firstOrNull { it.slotKey == exactKey }
                ?: fallbackCatalogJvm().last()
        val candidateKeys =
            listOf(exactKey, "${genre.slotKey}:any", "any:${vocal.slotKey}")
        for (key in candidateKeys) {
            val custom = policy.customRules[key] ?: continue
            val bands = normalizeTenSlotsJvm(custom.bands)
            val preamp = custom.preampDb.coerceIn(-15.0, 6.0)
            val nonZero =
                abs(preamp) > 1e-4 ||
                    bands.any { abs(it.gainDb) > 1e-4 || it.type == BiquadType.LOW_PASS || it.type == BiquadType.HIGH_PASS }
            return ResolvedAutoEq(
                applied = true,
                genreFamily = genre,
                vocalClass = vocal,
                matchedRuleKey = key,
                displayLabel = "$label (自定义)",
                isCustomRule = true,
                lockedToPreviousWork = false,
                preampDb = preamp,
                bands = bands,
                enableCrossfeed =
                    custom.crossfeedOverride
                        ?: (genre == AutoEqGenreFamily.CLASSICAL && vocal != AutoEqVocalClass.VOCAL && policy.classicalAutoCrossfeed),
                hasNonZeroEq = nonZero,
            )
        }
        val bands = fallbackEntry.defaultBands
        val nonZero =
            bands.any { abs(it.gainDb) > 1e-4 || it.type == BiquadType.LOW_PASS || it.type == BiquadType.HIGH_PASS }
        val autoCross =
            genre == AutoEqGenreFamily.CLASSICAL && vocal != AutoEqVocalClass.VOCAL && policy.classicalAutoCrossfeed
        return ResolvedAutoEq(
            applied = true,
            genreFamily = genre,
            vocalClass = vocal,
            matchedRuleKey = exactKey,
            displayLabel = label,
            isCustomRule = false,
            lockedToPreviousWork = false,
            preampDb = 0.0,
            bands = bands,
            enableCrossfeed = autoCross,
            hasNonZeroEq = nonZero,
        )
    }

    private fun composeFallbackJvm(
        devicePreampDb: Double,
        deviceBands: List<BiquadBand>,
        contentPreampDb: Double,
        contentBands: List<BiquadBand>,
    ): Pair<Double, List<BiquadBand>> {
        val merged = (deviceBands + contentBands).take(64)
        val active =
            merged.filter {
                abs(it.gainDb) > 1e-4 || it.type == BiquadType.LOW_PASS || it.type == BiquadType.HIGH_PASS
            }
        if (active.isEmpty()) {
            return (devicePreampDb + contentPreampDb).coerceIn(-24.0, 6.0) to merged
        }
        val filters = active.map { BiquadFilter64(it, 48000, 1) }
        val fMin = kotlin.math.ln(25.0)
        val fMax = kotlin.math.ln(18000.0)
        var peakDb = 0.0
        for (i in 0 until 48) {
            val freq = kotlin.math.exp(fMin + (fMax - fMin) * i / 47.0)
            var mag = 1.0
            for (f in filters) mag *= f.magnitudeAtHz(freq, 48000)
            if (mag > 1.0) {
                val db = 20.0 * kotlin.math.log10(mag)
                if (db > peakDb) peakDb = db
            }
        }
        val preamp = kotlin.math.min(devicePreampDb + contentPreampDb, -peakDb).coerceIn(-24.0, 6.0)
        return preamp to merged
    }

    private fun normalizeTenSlotsJvm(bands: List<BiquadBand>): List<BiquadBand> {
        if (bands.isEmpty()) return DspChainConfig.defaultTenBands()
        if (bands.size >= 10) return bands.take(16)
        val defaults = DspChainConfig.defaultTenBands()
        return bands + defaults.drop(bands.size)
    }

    private fun splitTokens(genres: List<String>): List<String> =
        genres.flatMap { raw ->
            raw.split(';', '/', ',', '、', '|').map { it.trim().lowercase() }.filter { it.isNotEmpty() }
        }

    private fun classifyGenreJvm(genres: List<String>, workName: String?): AutoEqGenreFamily {
        val tokens = splitTokens(genres)
        val classicalKw = listOf("classical", "古典", "交响", "协奏", "奏鸣", "室内乐", "歌剧", "巴洛克", "symphon", "concerto", "sonat", "chamber", "opera", "baroque", "choral", "quartet", "partita", "requiem")
        val jazzKw = listOf("jazz", "爵士", "swing", "bebop", "bossa")
        val popKw = listOf("pop", "r&b", "rnb", "soul", "hip-hop", "hip hop", "rap", "mandopop", "cantopop", "c-pop", "j-pop", "k-pop", "ballad", "流行", "说唱", "嘻哈")
        val rockKw = listOf("rock", "metal", "punk", "indie", "alternative", "摇滚", "金属", "朋克")
        val folkKw = listOf("folk", "acoustic", "country", "blues", "民谣", "乡村", "蓝调")
        val elecKw = listOf("electronic", "edm", "dance", "techno", "house", "trance", "synth", "lo-fi", "lofi", "电子", "舞曲")
        val ostKw = listOf("soundtrack", "ost", "ambient", "new age", "anime", "acg", "game", "原声", "配乐", "氛围")

        for (t in tokens) {
            when {
                classicalKw.any { t.contains(it) } -> return AutoEqGenreFamily.CLASSICAL
                jazzKw.any { t.contains(it) } -> return AutoEqGenreFamily.JAZZ
                popKw.any { t.contains(it) } -> return AutoEqGenreFamily.POP_RNB
                rockKw.any { t.contains(it) } -> return AutoEqGenreFamily.ROCK_METAL
                folkKw.any { t.contains(it) } -> return AutoEqGenreFamily.FOLK_ACOUSTIC
                elecKw.any { t.contains(it) } -> return AutoEqGenreFamily.ELECTRONIC_DANCE
                ostKw.any { t.contains(it) } -> return AutoEqGenreFamily.OST_AMBIENT
            }
        }
        if (tokens.isEmpty() && !workName.isNullOrBlank()) {
            val w = workName.lowercase()
            if (classicalKw.any { w.contains(it) }) return AutoEqGenreFamily.CLASSICAL
        }
        return AutoEqGenreFamily.OTHER
    }

    private fun classifyVocalJvm(djVocals: String?, genres: List<String>, workName: String?): AutoEqVocalClass {
        when (djVocals?.trim()?.lowercase()) {
            "vocal" -> return AutoEqVocalClass.VOCAL
            "instrumental" -> return AutoEqVocalClass.INSTRUMENTAL
        }
        val combined = (splitTokens(genres) + listOfNotNull(workName?.trim()?.lowercase())).joinToString(" ")
        val vocalKw = listOf("opera", "歌剧", "choral", "choir", "合唱", "lieder", "vocal", "rap", "hip-hop", "mandopop", "cantopop", "pop", "ballad", "r&b", "soul")
        if (vocalKw.any { combined.contains(it) }) return AutoEqVocalClass.VOCAL
        val instKw = listOf("instrumental", "纯音乐", "纯器乐", "symphon", "concerto", "sonat", "quartet", "quintet", "partita", "chamber", "室内乐")
        if (instKw.any { combined.contains(it) }) return AutoEqVocalClass.INSTRUMENTAL
        return AutoEqVocalClass.UNKNOWN
    }

    private fun editBands(vararg edits: Triple<Int, BiquadType, Pair<Double, Double>>): List<BiquadBand> {
        val bands = DspChainConfig.defaultTenBands().toMutableList()
        for ((idx, type, gq) in edits) {
            if (idx in bands.indices) {
                bands[idx] = bands[idx].copy(type = type, gainDb = gq.first, q = gq.second)
            }
        }
        return bands
    }

    private fun fallbackCatalogJvm(): List<AutoEqCatalogEntry> {
        val entries = ArrayList<AutoEqCatalogEntry>(24)
        for (g in AutoEqGenreFamily.entries) {
            for (v in AutoEqVocalClass.entries) {
                val key = "${g.slotKey}:${v.slotKey}"
                val bands =
                    when (g to v) {
                        AutoEqGenreFamily.CLASSICAL to AutoEqVocalClass.VOCAL ->
                            editBands(
                                Triple(6, BiquadType.PEAKING, 1.0 to 1.0),
                                Triple(7, BiquadType.PEAKING, 0.5 to 1.41),
                                Triple(8, BiquadType.PEAKING, -0.5 to 1.41),
                            )
                        AutoEqGenreFamily.CLASSICAL to AutoEqVocalClass.INSTRUMENTAL,
                        AutoEqGenreFamily.CLASSICAL to AutoEqVocalClass.UNKNOWN -> DspChainConfig.defaultTenBands()
                        AutoEqGenreFamily.JAZZ to AutoEqVocalClass.VOCAL ->
                            editBands(
                                Triple(3, BiquadType.PEAKING, 0.8 to 1.2),
                                Triple(6, BiquadType.PEAKING, 1.5 to 1.2),
                                Triple(8, BiquadType.HIGH_SHELF, 0.8 to 0.71),
                            )
                        AutoEqGenreFamily.JAZZ to AutoEqVocalClass.INSTRUMENTAL ->
                            editBands(
                                Triple(1, BiquadType.LOW_SHELF, 1.0 to 0.71),
                                Triple(8, BiquadType.HIGH_SHELF, 1.0 to 0.71),
                            )
                        AutoEqGenreFamily.JAZZ to AutoEqVocalClass.UNKNOWN ->
                            editBands(
                                Triple(1, BiquadType.LOW_SHELF, 0.8 to 0.71),
                                Triple(8, BiquadType.HIGH_SHELF, 0.8 to 0.71),
                            )
                        AutoEqGenreFamily.POP_RNB to AutoEqVocalClass.VOCAL ->
                            editBands(
                                Triple(1, BiquadType.LOW_SHELF, 1.5 to 0.71),
                                Triple(3, BiquadType.PEAKING, -0.8 to 1.2),
                                Triple(6, BiquadType.PEAKING, 1.2 to 1.2),
                                Triple(7, BiquadType.PEAKING, 0.8 to 1.41),
                                Triple(8, BiquadType.HIGH_SHELF, 1.0 to 0.71),
                            )
                        AutoEqGenreFamily.POP_RNB to AutoEqVocalClass.INSTRUMENTAL ->
                            editBands(
                                Triple(1, BiquadType.LOW_SHELF, 1.5 to 0.71),
                                Triple(8, BiquadType.HIGH_SHELF, 1.0 to 0.71),
                            )
                        AutoEqGenreFamily.POP_RNB to AutoEqVocalClass.UNKNOWN ->
                            editBands(
                                Triple(1, BiquadType.LOW_SHELF, 1.2 to 0.71),
                                Triple(3, BiquadType.PEAKING, -0.6 to 1.2),
                                Triple(6, BiquadType.PEAKING, 1.0 to 1.2),
                                Triple(8, BiquadType.HIGH_SHELF, 0.8 to 0.71),
                            )
                        AutoEqGenreFamily.ROCK_METAL to AutoEqVocalClass.VOCAL ->
                            editBands(
                                Triple(1, BiquadType.LOW_SHELF, 1.5 to 0.71),
                                Triple(3, BiquadType.PEAKING, -1.0 to 1.2),
                                Triple(6, BiquadType.PEAKING, 1.5 to 1.2),
                                Triple(7, BiquadType.PEAKING, -0.8 to 1.41),
                            )
                        AutoEqGenreFamily.ROCK_METAL to AutoEqVocalClass.INSTRUMENTAL ->
                            editBands(
                                Triple(1, BiquadType.LOW_SHELF, 1.8 to 0.71),
                                Triple(3, BiquadType.PEAKING, -1.0 to 1.2),
                                Triple(7, BiquadType.PEAKING, -0.5 to 1.41),
                            )
                        AutoEqGenreFamily.ROCK_METAL to AutoEqVocalClass.UNKNOWN ->
                            editBands(
                                Triple(1, BiquadType.LOW_SHELF, 1.5 to 0.71),
                                Triple(3, BiquadType.PEAKING, -1.0 to 1.2),
                                Triple(7, BiquadType.PEAKING, -0.5 to 1.41),
                            )
                        AutoEqGenreFamily.FOLK_ACOUSTIC to AutoEqVocalClass.VOCAL ->
                            editBands(
                                Triple(3, BiquadType.PEAKING, -0.5 to 1.2),
                                Triple(6, BiquadType.PEAKING, 1.2 to 1.2),
                                Triple(8, BiquadType.HIGH_SHELF, 1.2 to 0.71),
                            )
                        AutoEqGenreFamily.FOLK_ACOUSTIC to AutoEqVocalClass.INSTRUMENTAL ->
                            editBands(
                                Triple(3, BiquadType.PEAKING, -0.5 to 1.2),
                                Triple(8, BiquadType.HIGH_SHELF, 1.2 to 0.71),
                            )
                        AutoEqGenreFamily.FOLK_ACOUSTIC to AutoEqVocalClass.UNKNOWN ->
                            editBands(
                                Triple(3, BiquadType.PEAKING, -0.5 to 1.2),
                                Triple(8, BiquadType.HIGH_SHELF, 1.0 to 0.71),
                            )
                        AutoEqGenreFamily.ELECTRONIC_DANCE to AutoEqVocalClass.VOCAL ->
                            editBands(
                                Triple(1, BiquadType.LOW_SHELF, 1.8 to 0.71),
                                Triple(3, BiquadType.PEAKING, -1.0 to 1.2),
                                Triple(6, BiquadType.PEAKING, 1.2 to 1.2),
                            )
                        AutoEqGenreFamily.ELECTRONIC_DANCE to AutoEqVocalClass.INSTRUMENTAL ->
                            editBands(
                                Triple(1, BiquadType.LOW_SHELF, 2.0 to 0.71),
                                Triple(3, BiquadType.PEAKING, -1.0 to 1.2),
                                Triple(8, BiquadType.HIGH_SHELF, 1.2 to 0.71),
                            )
                        AutoEqGenreFamily.ELECTRONIC_DANCE to AutoEqVocalClass.UNKNOWN ->
                            editBands(
                                Triple(1, BiquadType.LOW_SHELF, 1.8 to 0.71),
                                Triple(3, BiquadType.PEAKING, -1.0 to 1.2),
                                Triple(8, BiquadType.HIGH_SHELF, 1.0 to 0.71),
                            )
                        AutoEqGenreFamily.OST_AMBIENT to AutoEqVocalClass.VOCAL ->
                            editBands(Triple(6, BiquadType.PEAKING, 1.0 to 1.2))
                        AutoEqGenreFamily.OST_AMBIENT to AutoEqVocalClass.INSTRUMENTAL ->
                            editBands(
                                Triple(1, BiquadType.LOW_SHELF, 1.0 to 0.71),
                                Triple(8, BiquadType.HIGH_SHELF, 0.8 to 0.71),
                            )
                        AutoEqGenreFamily.OTHER to AutoEqVocalClass.VOCAL ->
                            editBands(
                                Triple(3, BiquadType.PEAKING, -0.6 to 1.2),
                                Triple(6, BiquadType.PEAKING, 1.2 to 1.2),
                            )
                        else -> DspChainConfig.defaultTenBands()
                    }
                entries.add(
                    AutoEqCatalogEntry(
                        slotKey = key,
                        genreFamily = g,
                        vocalClass = v,
                        label = "${g.displayName} · ${v.displayName}",
                        defaultPreampDb = 0.0,
                        defaultBands = bands,
                        defaultCrossfeed = g == AutoEqGenreFamily.CLASSICAL && v != AutoEqVocalClass.VOCAL,
                    )
                )
            }
        }
        return entries
    }
}
