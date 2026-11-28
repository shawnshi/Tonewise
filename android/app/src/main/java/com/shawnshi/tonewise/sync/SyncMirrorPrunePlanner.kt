/*
 * Copyright (c) 2026 Tonewise Project
 * SyncMirrorPrunePlanner.kt is part of Tonewise.
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

import java.text.Normalizer
import java.util.Locale

enum class PruneDecision {
    PROCEED,
    SKIP_DISABLED,
    SKIP_EMPTY_OR_UNAUTHORITATIVE,
    SKIP_INCOMPLETE_SYNC,
    REQUIRE_LARGE_PRUNE_CONFIRMATION,
}

data class PrunePlan(
    val decision: PruneDecision,
    val filesToDelete: List<LocalMusicAudioFile>,
    val localAudioCount: Int,
    val pruneRatio: Double,
)

object SyncMirrorPrunePlanner {
    fun normalizeRelPath(raw: String): String {
        val nfc = Normalizer.normalize(raw, Normalizer.Form.NFC)
        return LanSyncV2Client.sanitizeRelativePathSegments(nfc)
            .joinToString("/")
            .lowercase(Locale.ROOT)
    }

    fun planPrune(
        policy: SyncPolicyDto?,
        desiredTargetRelPaths: Collection<String>,
        localMusicFiles: List<LocalMusicAudioFile>,
        managedRelPaths: Set<String>,
        syncCompletedCleanly: Boolean,
    ): PrunePlan {
        val localCount = localMusicFiles.size
        if (policy == null || !policy.mirrorPruneEnabled) {
            return PrunePlan(PruneDecision.SKIP_DISABLED, emptyList(), localCount, 0.0)
        }
        if (!syncCompletedCleanly) {
            return PrunePlan(PruneDecision.SKIP_INCOMPLETE_SYNC, emptyList(), localCount, 0.0)
        }

        val desiredSet =
            desiredTargetRelPaths
                .asSequence()
                .map(::normalizeRelPath)
                .filter { it.isNotEmpty() }
                .toHashSet()
        if (!policy.outboxAuthoritative || policy.missingSourceCount > 0 || desiredSet.isEmpty()) {
            return PrunePlan(PruneDecision.SKIP_EMPTY_OR_UNAUTHORITATIVE, emptyList(), localCount, 0.0)
        }

        val managedOnly = policy.pruneScope.equals("managed_only", ignoreCase = true)
        val managedSet =
            if (managedOnly) {
                managedRelPaths
                    .asSequence()
                    .map(::normalizeRelPath)
                    .filter { it.isNotEmpty() }
                    .toHashSet()
            } else {
                emptySet()
            }

        val filesToDelete =
            localMusicFiles.filter { file ->
                val norm = normalizeRelPath(file.relativePath)
                if (norm.isEmpty() || norm in desiredSet) {
                    false
                } else if (managedOnly) {
                    norm in managedSet
                } else {
                    true
                }
            }

        val ratio =
            if (localCount > 0) {
                filesToDelete.size.toDouble() / localCount.toDouble()
            } else {
                0.0
            }

        if (!policy.pruneConfirmedForSession &&
            filesToDelete.size >= policy.largePruneConfirmThresholdCount &&
            ratio >= policy.largePruneConfirmThresholdRatio
        ) {
            return PrunePlan(
                decision = PruneDecision.REQUIRE_LARGE_PRUNE_CONFIRMATION,
                filesToDelete = filesToDelete,
                localAudioCount = localCount,
                pruneRatio = ratio,
            )
        }

        return PrunePlan(
            decision = PruneDecision.PROCEED,
            filesToDelete = filesToDelete,
            localAudioCount = localCount,
            pruneRatio = ratio,
        )
    }
}
