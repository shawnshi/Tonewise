/*
 * Copyright (c) 2026 Tonewise Project
 * MobileSyncA1ToA5Test.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.sync

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test
import org.oxycblt.musikr.core.TonewiseCore

class MobileSyncA1ToA5Test {
    @Test
    fun buildDjFilterSceneJson_supportsExcludeVocalTempoMoodAndSortArc() {
        assertNull(
            LanSyncRepository.buildDjFilterSceneJson(
                scene = "all",
                vocals = "all",
                energy = "all",
                excludeVocal = false,
                tempoBucket = "all",
                moodCluster = "all",
                sortArc = "flat",
            )
        )

        val rawJson =
            LanSyncRepository.buildDjFilterSceneJson(
                scene = "focus",
                vocals = "vocal",
                energy = "low",
                excludeVocal = true,
                tempoBucket = "Slow",
                moodCluster = "calm_relaxed",
                sortArc = "energy_asc",
            )
        assertNotNull(rawJson)
        @Suppress("UNCHECKED_CAST")
        val root = TonewiseCore.parseJson(rawJson!!) as Map<String, Any?>
        assertEquals("energy_asc", root["sort_arc"])
        @Suppress("UNCHECKED_CAST")
        val rule = root["rule"] as Map<String, Any?>
        @Suppress("UNCHECKED_CAST")
        val all = rule["all"] as List<Map<String, Any?>>
        assertEquals(5, all.size)

        @Suppress("UNCHECKED_CAST")
        val notNode = all[1]["not"] as Map<String, Any?>
        @Suppress("UNCHECKED_CAST")
        val notPred = notNode["predicate"] as Map<String, Any?>
        assertEquals("dj_vocals", notPred["field"])
        assertEquals(listOf("vocal"), notPred["values"])
    }
    @Test
    fun lastFm_normalizeAndEnforce14DayWindowPlus120sDedup() {
        assertEquals(
            "glenn gould||variation 1",
            LastFmRepository.normalizeTrackKey("  Glenn  Gould ", "Variation 1"),
        )

        val nowSec = 1_763_251_000L
        val cloudPlays =
            listOf(
                LastFmRepository.CloudPlayRecord(
                    artist = "Glenn Gould",
                    title = "Variation 1",
                    timestampSec = 1_763_250_000L,
                )
            )

        val entries =
            listOf(
                // Within ±120s of cloud play -> deduplicated
                ScrobbleEntry("uid1", "  glenn  gould ", "variation 1", "Goldberg", 120, 1_763_250_090L),
                // Older than 14 days -> expired
                ScrobbleEntry(
                    "uid2",
                    "Bill Evans",
                    "Peace Piece",
                    "Everybody Digs",
                    380,
                    nowSec - 15L * 86_400L,
                ),
                // Outside ±120s and within 14 days -> eligible
                ScrobbleEntry("uid3", "Glenn Gould", "Variation 1", "Goldberg", 120, 1_763_250_300L),
                // Fresh local play -> eligible
                ScrobbleEntry("uid4", "Miles Davis", "So What", "Kind of Blue", 540, nowSec - 300L),
            )

        val filtered = LastFmRepository.filterAndDedupPendingScrobbles(entries, cloudPlays, nowSec)
        assertEquals(2, filtered.eligible.size)
        assertEquals("uid3", filtered.eligible[0].songUid)
        assertEquals("uid4", filtered.eligible[1].songUid)
        assertEquals(1, filtered.expiredSkipped)
        assertEquals(1, filtered.deduplicatedSkipped)
        assertTrue(filtered.eligible.all { nowSec - it.timestampSec <= 14L * 86_400L })

        val badSecret = LastFmRepository.validateCredentialsFormat("0123456789abcdef0123456789abcdef", "12345678")
        assertTrue(badSecret != null && badSecret.contains("8 chars"))
        assertEquals(
            null,
            LastFmRepository.validateCredentialsFormat(
                "0123456789abcdef0123456789abcdef",
                "fedcba9876543210fedcba9876543210",
            ),
        )
    }

    @Test
    fun buildFocusFilterSceneJson_supportsMultiDimensionIncludeExcludeAndMoodSynonyms() {
        assertEquals("calm_relaxed", LanSyncRepository.resolveMoodClusterId("Peaceful"))
        assertEquals("high_tension", LanSyncRepository.resolveMoodClusterId("DRAMATIC"))
        assertNull(LanSyncRepository.resolveMoodClusterId("nonexistent_mood"))

        assertNull(LanSyncRepository.buildFocusFilterSceneJson(FocusFilterSpec()))

        val spec =
            FocusFilterSpec(
                scenes = mapOf("focus" to FocusPillState.INCLUDE, "pop" to FocusPillState.EXCLUDE),
                vocals = mapOf("vocal" to FocusPillState.EXCLUDE),
                tempos = mapOf("Fast" to FocusPillState.EXCLUDE, "Slow" to FocusPillState.INCLUDE),
                moods =
                    mapOf(
                        "calm_relaxed" to FocusPillState.INCLUDE,
                        "high_tension" to FocusPillState.EXCLUDE,
                    ),
                sortArc = "rediscovery_90d",
                unplayedDaysGte = 90,
            )
        val rawJson = LanSyncRepository.buildFocusFilterSceneJson(spec)
        assertNotNull(rawJson)

        @Suppress("UNCHECKED_CAST")
        val root = TonewiseCore.parseJson(rawJson!!) as Map<String, Any?>
        assertEquals("rediscovery_90d", root["sort_arc"])

        @Suppress("UNCHECKED_CAST")
        val rule = root["rule"] as Map<String, Any?>
        @Suppress("UNCHECKED_CAST")
        val predicates =
            (rule["all"] as List<Map<String, Any?>>).map {
                @Suppress("UNCHECKED_CAST")
                it["predicate"] as Map<String, Any?>
            }
        assertEquals(8, predicates.size)
        assertTrue(
            predicates.any {
                it["field"] == "tempo_bucket" &&
                    it["op"] == "in" &&
                    it["values"] == listOf("Slow")
            }
        )
        assertTrue(
            predicates.any {
                it["field"] == "mood_cluster" &&
                    it["op"] == "not_in" &&
                    it["values"] == listOf("high_tension")
            }
        )
        assertTrue(
            predicates.any {
                it["field"] == "dj_scene" && it["op"] == "in" && it["values"] == listOf("focus")
            }
        )
        assertTrue(
            predicates.any {
                it["field"] == "dj_scene" && it["op"] == "not_in" && it["values"] == listOf("pop")
            }
        )
        assertTrue(
            predicates.any {
                it["field"] == "dj_vocals" &&
                    it["op"] == "not_in" &&
                    it["values"] == listOf("vocal")
            }
        )
        assertTrue(
            predicates.any {
                it["field"] == "tempo_bucket" &&
                    it["op"] == "not_in" &&
                    it["values"] == listOf("Fast")
            }
        )
        assertTrue(
            predicates.any {
                it["field"] == "mood_cluster" &&
                    it["op"] == "in" &&
                    it["values"] == listOf("calm_relaxed")
            }
        )
        assertTrue(
            predicates.any {
                it["field"] == "unplayed_days_gte" &&
                    it["op"] == "gte" &&
                    ((it["values"] as? List<*>)?.firstOrNull() as? Number)?.toLong() == 90L
            }
        )
    }

    @Test
    fun parseJson_rejectsExcessiveNestingDepth() {
        val deepArray = "[".repeat(70) + "]".repeat(70)
        assertThrows(IllegalArgumentException::class.java) {
            TonewiseCore.parseJson(deepArray)
        }
        val deepObject = "{\"a\":".repeat(70) + "1" + "}".repeat(70)
        assertThrows(IllegalArgumentException::class.java) {
            TonewiseCore.parseJson(deepObject)
        }
    }
}
