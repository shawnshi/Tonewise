/*
 * Copyright (c) 2026 Tonewise Project
 * ClassicalHierarchyLogic.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.home.classical

import com.shawnshi.tonewise.music.ClassicalWork
import com.shawnshi.tonewise.music.WorkRecording
import java.util.Locale

data class ClassicalComposerGroup(
    val composerName: String,
    val initial: String,
    val works: List<ClassicalWork>,
)

/**
 * Pure Kotlin grouping, Zune lowercase badge formatting, and A–Z / '#' jump matrix index
 * extraction for the 3-level `classical` hierarchy view.
 */
object ClassicalHierarchyLogic {
    val JUMP_MATRIX_KEYS: List<String> = listOf("#") + ('A'..'Z').map { it.toString() }

    fun normalizeInitial(composerName: String): String {
        val trimmed = composerName.trim()
        val first = trimmed.firstOrNull { !it.isWhitespace() } ?: return "#"
        val upper = first.uppercaseChar()
        return if (upper in 'A'..'Z') upper.toString() else "#"
    }

    fun groupByComposer(works: List<ClassicalWork>): List<ClassicalComposerGroup> {
        if (works.isEmpty()) return emptyList()
        return works
            .groupBy { it.composerName.trim().ifEmpty { "Unknown" } }
            .entries
            .sortedBy { it.key.lowercase(Locale.ROOT) }
            .map { (composer, composerWorks) ->
                ClassicalComposerGroup(
                    composerName = composer,
                    initial = normalizeInitial(composer),
                    works = composerWorks.sortedBy { it.workTitle.lowercase(Locale.ROOT) },
                )
            }
    }

    fun extractAvailableInitials(groups: List<ClassicalComposerGroup>): Set<String> =
        groups.mapTo(LinkedHashSet()) { it.initial }

    fun formatRecordingBadge(recording: WorkRecording): String {
        val who =
            recording.conductorLabel.trim().ifEmpty {
                recording.ensembleLabel.trim()
            }
        val yearText = recording.year?.toString()
        val parts = listOfNotNull(who.takeIf { it.isNotEmpty() }, yearText)
        return parts.joinToString(" · ").lowercase(Locale.ROOT)
    }
}
