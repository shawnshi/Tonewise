/*
 * Copyright (c) 2026 Tonewise Project
 * ClassicalHierarchyLogic.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.home.classical

import com.shawnshi.tonewise.music.ClassicalMetadataEngine
import com.shawnshi.tonewise.music.ClassicalWork
import com.shawnshi.tonewise.music.WorkRecording
import java.io.File
import java.util.Locale

data class ClassicalComposerGroup(
    val composerName: String,
    val initial: String,
    val works: List<ClassicalWork>,
    val era: String? = null,
    val workCount: Int = works.size,
    val recordingCount: Int = works.sumOf { maxOf(1, it.recordings.size) },
    val localPortraitPath: String? = null,
) {
    val displayLowercase: String
        get() = composerName.lowercase(Locale.ROOT)
}

/**
 * Pure Kotlin grouping, Zune lowercase badge formatting, Era/Form filtering, Roman-numeral movement
 * formatting, and `# / a–z` jump matrix indexing for the 2-stage `classical` collection view.
 */
object ClassicalHierarchyLogic {
    val JUMP_MATRIX_KEYS: List<String> = listOf("#") + ('A'..'Z').map { it.toString() }
    val ERA_KEYS: List<String> = listOf("baroque", "classical", "romantic", "modern")
    val FORM_FILTERS: List<String> = listOf("all", "symphonies", "concertos", "chamber", "solo")

    fun normalizeInitial(composerName: String): String {
        val trimmed = composerName.trim()
        val first = trimmed.firstOrNull { !it.isWhitespace() } ?: return "#"
        val upper = first.uppercaseChar()
        return if (upper in 'A'..'Z') upper.toString() else "#"
    }

    fun resolveLocalComposerPortrait(composerName: String, candidateDirs: List<File> = emptyList()): String? {
        if (composerName.isBlank() || candidateDirs.isEmpty()) return null
        val slug = composerName.trim().lowercase(Locale.ROOT).replace(Regex("[^a-z0-9\\u4e00-\\u9fff]+"), "_")
        val exts = listOf("jpg", "jpeg", "png", "webp")
        for (dir in candidateDirs) {
            if (!dir.isDirectory) continue
            for (ext in exts) {
                val exact = File(dir, "${composerName.trim()}.$ext")
                if (exact.isFile) return exact.absolutePath
                val slugFile = File(dir, "$slug.$ext")
                if (slugFile.isFile) return slugFile.absolutePath
            }
        }
        return null
    }

    fun groupByComposer(
        works: List<ClassicalWork>,
        portraitDirs: List<File> = emptyList(),
    ): List<ClassicalComposerGroup> {
        if (works.isEmpty()) return emptyList()
        return works
            .groupBy { it.composerName.trim().ifEmpty { "Unknown" } }
            .entries
            .sortedBy { it.key.lowercase(Locale.ROOT) }
            .map { (composer, composerWorks) ->
                val sortedWorks = composerWorks.sortedBy { it.workTitle.lowercase(Locale.ROOT) }
                val era =
                    sortedWorks.firstNotNullOfOrNull { it.era }
                        ?: ClassicalMetadataEngine.classifyComposerEra(composer, null)
                ClassicalComposerGroup(
                    composerName = composer,
                    initial = normalizeInitial(composer),
                    works = sortedWorks,
                    era = era,
                    workCount = sortedWorks.size,
                    recordingCount = sortedWorks.sumOf { maxOf(1, it.recordings.size) },
                    localPortraitPath = resolveLocalComposerPortrait(composer, portraitDirs),
                )
            }
    }

    fun extractAvailableInitials(groups: List<ClassicalComposerGroup>): Set<String> =
        groups.mapTo(LinkedHashSet()) { it.initial }

    fun filterComposersByEra(
        groups: List<ClassicalComposerGroup>,
        era: String?,
    ): List<ClassicalComposerGroup> {
        val norm = era?.trim()?.lowercase(Locale.ROOT)?.takeIf { it.isNotEmpty() } ?: return groups
        return groups.filter { it.era == norm }
    }

    fun filterWorksByForm(
        works: List<ClassicalWork>,
        form: String?,
    ): List<ClassicalWork> {
        val norm = form?.trim()?.lowercase(Locale.ROOT)?.takeIf { it.isNotEmpty() && it != "all" }
            ?: return works
        return works.filter { work ->
            val resolvedForm =
                if (work.form.isNotBlank() && work.form != "other") {
                    work.form
                } else {
                    ClassicalMetadataEngine.classifyWorkForm(work.workTitle, null)
                }
            resolvedForm == norm
        }
    }

    fun formatRecordingBadge(recording: WorkRecording): String {
        val who =
            recording.conductorLabel.trim().ifEmpty {
                recording.ensembleLabel.trim()
            }
        val yearText = recording.year?.toString()
        val parts = listOfNotNull(who.takeIf { it.isNotEmpty() }, yearText)
        return parts.joinToString(" · ").lowercase(Locale.ROOT)
    }

    fun formatComposerStatsLine(group: ClassicalComposerGroup): String {
        val stats = "${group.workCount} works · ${group.recordingCount} recordings"
        return group.era?.takeIf { it.isNotBlank() }?.let { "$it · $stats" } ?: stats
    }

    fun formatMovementDisplay(
        rawTitle: String,
        explicitWork: String?,
        explicitMovement: String?,
        explicitMovementNumber: Int?,
        index: Int,
    ): Pair<String, String> {
        val parsed =
            ClassicalMetadataEngine.splitWorkAndMovement(
                rawTitle = rawTitle,
                explicitWork = explicitWork,
                explicitMovement = explicitMovement,
                explicitMovementNumber = explicitMovementNumber,
            )
        val num = parsed.movementNumber ?: (index + 1)
        val roman = "${ClassicalMetadataEngine.formatRomanNumeral(num)}."
        val cleanTitle = parsed.movement.ifBlank { rawTitle.trim() }
        return roman to cleanTitle
    }
}
