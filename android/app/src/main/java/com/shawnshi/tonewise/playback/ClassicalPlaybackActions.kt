/*
 * Copyright (c) 2026 Tonewise Project
 * ClassicalPlaybackActions.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.playback

import com.shawnshi.tonewise.music.ClassicalMetadataEngine

/**
 * Pure decision helpers for deciding whether a playing track belongs to the classical domain
 * and extracting primary composer / conductor credits for direct library navigation.
 */
object ClassicalPlaybackActions {
    fun isClassicalGenreName(genreName: String?): Boolean =
        ClassicalMetadataEngine.isClassicalGenre(genreName)

    fun isExplicitNonClassicalGenreName(genreName: String?): Boolean =
        ClassicalMetadataEngine.isExplicitNonClassicalGenre(genreName)

    fun isClassicalWorkSignal(workName: String?): Boolean =
        ClassicalMetadataEngine.hasClassicalWorkSignal(workName)

    fun isClassicalTrack(
        workName: String?,
        genreNames: Collection<String>,
        conductors: Collection<String>,
        composers: Collection<String> = emptyList(),
        title: String? = null,
    ): Boolean {
        val primaryGenre =
            genreNames.firstOrNull { isClassicalGenreName(it) }
                ?: genreNames.firstOrNull { isExplicitNonClassicalGenreName(it) }
                ?: genreNames.firstOrNull { it.isNotBlank() }
        return ClassicalMetadataEngine.isClassicalTrack(
            genre = primaryGenre,
            work = workName,
            composer = resolvePrimaryCredit(composers),
            conductor = resolvePrimaryCredit(conductors),
            title = title,
        )
    }

    fun resolvePrimaryCredit(values: Collection<String>): String? =
        values.firstOrNull { it.isNotBlank() }?.trim()
}
