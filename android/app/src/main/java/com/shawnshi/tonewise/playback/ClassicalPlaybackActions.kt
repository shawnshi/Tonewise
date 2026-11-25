/*
 * Copyright (c) 2026 Tonewise Project
 * ClassicalPlaybackActions.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.playback

/**
 * Pure decision helpers for deciding whether a playing track belongs to the classical domain
 * and extracting primary composer / conductor credits for direct library navigation.
 */
object ClassicalPlaybackActions {
    private val CLASSICAL_GENRE_TOKENS =
        listOf(
            "classical",
            "古典",
            "交响",
            "协奏",
            "奏鸣",
            "室内乐",
            "歌剧",
            "巴洛克",
            "orchestra",
            "symphon",
            "concerto",
            "sonata",
            "chamber",
            "opera",
            "baroque",
        )

    private val NON_CLASSICAL_GENRE_TOKENS =
        listOf(
            "pop",
            "rock",
            "metal",
            "jazz",
            "hip-hop",
            "hip hop",
            "rap",
            "r&b",
            "electronic",
            "edm",
            "dance",
            "country",
            "folk",
            "blues",
            "reggae",
            "indie",
            "punk",
            "soul",
            "funk",
            "k-pop",
            "j-pop",
            "c-pop",
            "mandopop",
            "cantopop",
            "tai-pop",
            "new age",
            "ambient",
            "traditional",
            "latin",
            "ballad",
            "disco",
            "alternative",
            "world music",
            "acoustic",
            "fado",
            "reggaeton",
            "acg",
            "anime",
            "soundtrack",
            "ost",
            "game",
            "流行",
            "摇滚",
            "爵士",
            "电子",
            "民谣",
            "说唱",
            "嘻哈",
            "原声",
        )

    private val CLASSICAL_WORK_SIGNAL_TOKENS =
        listOf(
            "symphony",
            "symphonie",
            "concerto",
            "konzert",
            "sonata",
            "sonate",
            "quartet",
            "quintet",
            "trio",
            "variation",
            "prelude",
            "prélude",
            "nocturne",
            "etude",
            "étude",
            "fugue",
            "toccata",
            "suite",
            "partita",
            "requiem",
            "cantata",
            "oratorio",
            "overture",
            "ouvertüre",
            "rhapsody",
            "serenade",
            "divertimento",
            "scherzo",
            "chaconne",
            "passacaglia",
            "mass in",
            "op.",
            "bwv",
            "kv",
            "k.",
            "hob.",
            "rv ",
            "交响",
            "协奏",
            "奏鸣",
            "四重奏",
            "变奏曲",
            "前奏曲",
            "夜曲",
            "练习曲",
            "组曲",
            "弥撒",
            "安魂曲",
            "歌剧",
        )

    fun isClassicalGenreName(genreName: String?): Boolean {
        val raw = genreName?.trim()?.takeIf { it.isNotEmpty() } ?: return false
        val lower = raw.lowercase()
        return CLASSICAL_GENRE_TOKENS.any { lower.contains(it) }
    }

    fun isExplicitNonClassicalGenreName(genreName: String?): Boolean {
        val raw = genreName?.trim()?.takeIf { it.isNotEmpty() } ?: return false
        val lower = raw.lowercase()
        return NON_CLASSICAL_GENRE_TOKENS.any { lower.contains(it) }
    }

    fun isClassicalWorkSignal(workName: String?): Boolean {
        val raw = workName?.trim()?.takeIf { it.isNotEmpty() } ?: return false
        val lower = raw.lowercase()
        return CLASSICAL_WORK_SIGNAL_TOKENS.any { lower.contains(it) }
    }

    fun isClassicalTrack(
        workName: String?,
        genreNames: Collection<String>,
        conductors: Collection<String>,
    ): Boolean {
        if (genreNames.any { isClassicalGenreName(it) }) return true
        if (genreNames.any { isExplicitNonClassicalGenreName(it) }) {
            return isClassicalWorkSignal(workName)
        }
        if (!workName.isNullOrBlank()) return true
        return conductors.any { it.isNotBlank() }
    }

    fun resolvePrimaryCredit(values: Collection<String>): String? =
        values.firstOrNull { it.isNotBlank() }?.trim()
}
