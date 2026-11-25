package com.shawnshi.tonewise.sync

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class AutoDjEngineTest {
    private fun makeCandidate(
        idx: Int,
        playCount: Int = 0,
        bpm: Double? = 100.0,
        energy: String? = "medium",
        scenes: List<String> = emptyList(),
        moods: List<String> = emptyList(),
        workKey: String? = null,
        movementNumber: Int? = null,
        artist: String = "Artist ${idx % 30}",
        trackUid: String = "uid-$idx",
    ) =
        AutoDjTrackCandidate(
            trackUid = trackUid,
            title = "Track $idx",
            artist = artist,
            effectivePlayCount = playCount,
            rating = if (playCount > 0) 4 else 0,
            tempoBpm = bpm,
            tempoBucket = null,
            djEnergy = energy,
            djVocals = "vocal",
            djScenes = scenes,
            moods = moods,
            workKey = workKey,
            movementNumber = movementNumber,
        )

    @Test
    fun `resolveTimeProfile covers all 5 periods`() {
        assertEquals("morning", AutoDjEngine.resolveTimeProfile(6).periodId)
        assertEquals("day_focus", AutoDjEngine.resolveTimeProfile(10).periodId)
        assertEquals("afternoon", AutoDjEngine.resolveTimeProfile(15).periodId)
        assertEquals("evening", AutoDjEngine.resolveTimeProfile(20).periodId)
        assertEquals("late_night", AutoDjEngine.resolveTimeProfile(23).periodId)
        assertEquals("late_night", AutoDjEngine.resolveTimeProfile(2).periodId)
    }

    @Test
    fun `pure Auto DJ generates 50 songs with 70-30 split and atomic classical works`() {
        val candidates = ArrayList<AutoDjTrackCandidate>()
        // 3-movement classical work
        candidates.add(makeCandidate(1, playCount = 5, bpm = 92.0, workKey = "beethoven-5", movementNumber = 3, artist = "Berlin Phil"))
        candidates.add(makeCandidate(2, playCount = 5, bpm = 94.0, workKey = "beethoven-5", movementNumber = 1, artist = "Berlin Phil"))
        candidates.add(makeCandidate(3, playCount = 5, bpm = 90.0, workKey = "beethoven-5", movementNumber = 2, artist = "Berlin Phil"))
        for (i in 4..60) {
            candidates.add(makeCandidate(i, playCount = 4, bpm = 90.0 + (i % 15)))
        }
        for (i in 61..100) {
            candidates.add(makeCandidate(i, playCount = 0, bpm = 88.0 + (i % 16)))
        }

        val result = AutoDjEngine.generate(candidates, hourOfDay = 10, targetCount = 50, randomSeed = 42)
        assertEquals(50, result.orderedTrackUids.size)
        assertEquals(50, result.orderedTrackUids.distinct().size)
        assertTrue("Expected frequent >= 30, got ${result.frequentCount}", result.frequentCount >= 30)
        assertTrue("Expected discovery >= 10, got ${result.discoveryCount}", result.discoveryCount >= 10)

        // If any movement of beethoven-5 was selected, all 3 must appear contiguously in order 1,2,3 (uid-2, uid-3, uid-1)
        val idx1 = result.orderedTrackUids.indexOf("uid-2")
        if (idx1 >= 0) {
            assertEquals("uid-3", result.orderedTrackUids[idx1 + 1])
            assertEquals("uid-1", result.orderedTrackUids[idx1 + 2])
        }
    }

    @Test
    fun `scene Auto DJ prioritizes matched songs, caps at 50, and backfills when under 50`() {
        val candidates = ArrayList<AutoDjTrackCandidate>()
        val matchedUids = HashSet<String>()
        for (i in 1..15) {
            val uid = "uid-$i"
            matchedUids.add(uid)
            candidates.add(makeCandidate(i, playCount = if (i % 2 == 0) 3 else 0, bpm = 85.0, energy = "low", scenes = listOf("relax")))
        }
        for (i in 16..90) {
            candidates.add(makeCandidate(i, playCount = if (i % 3 == 0) 4 else 0, bpm = 75.0 + (i % 40), energy = if (i % 2 == 0) "low" else "high"))
        }

        val sceneCtx = AutoDjSceneContext(sceneId = "relax", sceneTitle = "Relax", matchedUids = matchedUids)
        val res = AutoDjEngine.generate(candidates, hourOfDay = 23, targetCount = 50, sceneContext = sceneCtx, randomSeed = 7)

        assertEquals(50, res.orderedTrackUids.size)
        assertTrue("Expected most matched tracks included, got ${res.sceneMatchCount}", res.sceneMatchCount >= 12)
        assertTrue(res.summaryText.contains("Relax"))
    }

    @Test
    fun `scene Auto DJ varies between morning and late night for same library`() {
        val candidates =
            (1..90).map { i ->
                val band = i % 3
                makeCandidate(
                    idx = i,
                    playCount = i % 5,
                    bpm = when (band) {
                        0 -> 68.0   // Late-night slow
                        1 -> 98.0   // Morning/day moderate
                        else -> 122.0 // Afternoon upbeat
                    },
                    energy = when (band) {
                        0 -> "low"
                        1 -> "medium"
                        else -> "high"
                    },
                    moods = when (band) {
                        0 -> listOf("calm", "soothing")
                        1 -> listOf("bright", "warm")
                        else -> listOf("uplifting", "energetic")
                    },
                )
            }
        val morning = AutoDjEngine.generate(candidates, hourOfDay = 7, targetCount = 50, randomSeed = 11)
        val lateNight = AutoDjEngine.generate(candidates, hourOfDay = 23, targetCount = 50, randomSeed = 11)

        assertEquals(50, morning.orderedTrackUids.size)
        assertEquals(50, lateNight.orderedTrackUids.size)
        assertTrue(morning.orderedTrackUids != lateNight.orderedTrackUids)
    }

    @Test
    fun `scene Auto DJ keeps matched classical works un-displaced by BPM penalty and honors sortArc`() {
        val candidates = ArrayList<AutoDjTrackCandidate>()
        val matchedUids = HashSet<String>()
        // 50 matched tracks including a slow 42 BPM 3-movement work outside evening window (72..114)
        for (m in 1..3) {
            val uid = "sym-$m"
            matchedUids.add(uid)
            candidates.add(
                makeCandidate(
                    idx = m,
                    playCount = 3,
                    bpm = 42.0,
                    energy = "low",
                    scenes = listOf("focus"),
                    workKey = "alb|cond::sym",
                    movementNumber = m,
                    artist = "Vienna Phil",
                    trackUid = uid,
                )
            )
        }
        for (i in 4..50) {
            val uid = "matched-$i"
            matchedUids.add(uid)
            candidates.add(
                makeCandidate(
                    idx = i,
                    playCount = if (i % 3 == 0) 0 else 4,
                    bpm = if (i % 2 == 0) 90.0 else 115.0,
                    energy = if (i % 2 == 0) "medium" else "high",
                    scenes = listOf("focus"),
                    trackUid = uid,
                )
            )
        }
        // 40 non-matched tracks with "perfect" BPM center score
        for (i in 100..140) {
            candidates.add(makeCandidate(idx = i, playCount = 10, bpm = 92.0, energy = "low"))
        }

        val ctx =
            AutoDjSceneContext(
                sceneId = "focus",
                sceneTitle = "Focus",
                matchedUids = matchedUids,
                sortArc = "energy_asc",
            )
        val res = AutoDjEngine.generate(candidates, hourOfDay = 20, targetCount = 50, sceneContext = ctx, randomSeed = 5)
        assertTrue(res.orderedTrackUids.all { it in matchedUids })
        val symIdx = res.orderedTrackUids.indexOf("sym-1")
        assertTrue("Expected slow classical matched work included", symIdx >= 0)
        assertEquals(0, symIdx)
        assertEquals("sym-2", res.orderedTrackUids[symIdx + 1])
        assertEquals("sym-3", res.orderedTrackUids[symIdx + 2])
    }

    @Test
    fun `Auto DJ does not truncate a single multi-movement work exceeding targetCount`() {
        val candidates =
            (1..6).reversed().map { m ->
                makeCandidate(
                    idx = m,
                    playCount = 2,
                    bpm = 96.0,
                    energy = "low",
                    workKey = "partita::4",
                    movementNumber = m,
                    artist = "Bach Pianist",
                )
            }
        val res = AutoDjEngine.generate(candidates, hourOfDay = 10, targetCount = 4, randomSeed = 1)
        assertEquals(listOf("uid-1", "uid-2", "uid-3", "uid-4", "uid-5", "uid-6"), res.orderedTrackUids)
    }
}
