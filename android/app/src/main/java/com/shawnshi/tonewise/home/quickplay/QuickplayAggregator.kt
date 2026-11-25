/*
 * Copyright (c) 2026 Tonewise Project
 * QuickplayAggregator.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.home.quickplay

data class QuickplayTrackCandidate(
    val songUid: String,
    val title: String,
    val artist: String,
    val albumUid: String,
    val albumTitle: String,
    val addedMs: Long,
    val playCount: Int,
    val lastPlayedMs: Long,
)

data class QuickplayAlbumSummary(
    val albumUid: String,
    val representativeSongUid: String,
    val albumTitle: String,
    val artist: String,
    val timestampMs: Long,
    val playCount: Int,
)

data class QuickplaySections(
    val history: List<QuickplayAlbumSummary>,
    val newlyAdded: List<QuickplayAlbumSummary>,
    val rediscovery: List<QuickplayAlbumSummary>,
)

/**
 * Pure Kotlin Quickplay section computation (History, Newly Added, 90d Rediscovery) matching
 * Tonewise Desktop's aggregation logic without Android runtime dependencies.
 */
object QuickplayAggregator {
    const val REDISCOVERY_AGE_MS: Long = 90L * 24L * 60L * 60L * 1000L

    fun computeSections(
        candidates: List<QuickplayTrackCandidate>,
        nowMs: Long,
        limit: Int = 12,
    ): QuickplaySections {
        if (candidates.isEmpty()) {
            return QuickplaySections(emptyList(), emptyList(), emptyList())
        }

        val history =
            dedupByAlbum(
                candidates
                    .filter { it.lastPlayedMs > 0L }
                    .sortedByDescending { it.lastPlayedMs },
                limit = limit,
                timestampSelector = { it.lastPlayedMs },
            )

        val newlyAdded =
            dedupByAlbum(
                candidates.sortedWith(
                    compareByDescending<QuickplayTrackCandidate> { it.addedMs }
                        .thenBy { it.albumTitle }
                ),
                limit = limit,
                timestampSelector = { it.addedMs },
            )

        val cutoff = nowMs - REDISCOVERY_AGE_MS
        val rediscovery =
            dedupByAlbum(
                candidates
                    .filter { it.playCount > 0 && it.lastPlayedMs in 1L until cutoff }
                    .sortedWith(
                        compareBy<QuickplayTrackCandidate> { it.lastPlayedMs }
                            .thenByDescending { it.playCount }
                    ),
                limit = limit,
                timestampSelector = { it.lastPlayedMs },
            )

        return QuickplaySections(
            history = history,
            newlyAdded = newlyAdded,
            rediscovery = rediscovery,
        )
    }

    private inline fun dedupByAlbum(
        ordered: List<QuickplayTrackCandidate>,
        limit: Int,
        timestampSelector: (QuickplayTrackCandidate) -> Long,
    ): List<QuickplayAlbumSummary> {
        val seenAlbums = LinkedHashSet<String>()
        val result = ArrayList<QuickplayAlbumSummary>(limit)
        for (item in ordered) {
            if (!seenAlbums.add(item.albumUid)) continue
            result.add(
                QuickplayAlbumSummary(
                    albumUid = item.albumUid,
                    representativeSongUid = item.songUid,
                    albumTitle = item.albumTitle,
                    artist = item.artist,
                    timestampMs = timestampSelector(item),
                    playCount = item.playCount,
                )
            )
            if (result.size >= limit) break
        }
        return result
    }
}
