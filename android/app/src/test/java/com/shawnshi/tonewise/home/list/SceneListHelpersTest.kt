/*
 * Copyright (c) 2026 Tonewise Project
 * SceneListHelpersTest.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.home.list

import com.shawnshi.tonewise.R
import com.shawnshi.tonewise.sync.SceneBanner
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class SceneListHelpersTest {
    private fun banner(
        id: String,
        title: String,
        rawJson: String = "{}",
    ) =
        SceneBanner(
            id = id,
            title = title,
            subtitle = "",
            accentHex = "#2B5B84",
            rawSceneJson = rawJson,
        )

    @Test
    fun hasBuiltinRediscoveryScene_detectsUuidIdAndRuleVariants() {
        assertTrue(
            hasBuiltinRediscoveryScene(
                listOf(banner(id = "44444444-4444-4444-8444-444444444444", title = "沉睡馆藏唤醒"))
            )
        )
        assertTrue(
            hasBuiltinRediscoveryScene(
                listOf(
                    banner(
                        id = "custom-oldies",
                        title = "Oldies",
                        rawJson = """{"sort_arc":"rediscovery_90d"}""",
                    )
                )
            )
        )
        assertTrue(
            hasBuiltinRediscoveryScene(
                listOf(
                    banner(
                        id = "custom-90d",
                        title = "Dusty",
                        rawJson = """{"predicate":{"field":"unplayed_days_gte"}}""",
                    )
                )
            )
        )
        assertFalse(
            hasBuiltinRediscoveryScene(
                listOf(
                    banner(id = "11111111-1111-4111-8111-111111111111", title = "Focus"),
                    banner(id = "22222222-2222-4222-8222-222222222222", title = "Coding"),
                )
            )
        )
    }

    @Test
    fun resolveSceneIconRes_mapsBuiltin5DScenesAndFallbackDistinctly() {
        assertEquals(
            R.drawable.ic_filter_24,
            resolveSceneIconRes(banner("11111111-1111-4111-8111-111111111111", "Focus · 深度专注")),
        )
        assertEquals(
            R.drawable.ic_code_24,
            resolveSceneIconRes(banner("22222222-2222-4222-8222-222222222222", "Coding · 心流编程")),
        )
        assertEquals(
            R.drawable.ic_dark_24,
            resolveSceneIconRes(banner("33333333-3333-4333-8333-333333333333", "Relax · 松弛休憩")),
        )
        assertEquals(
            R.drawable.ic_light_24,
            resolveSceneIconRes(banner("55555555-5555-4555-8555-555555555555", "Energy · 高能律动")),
        )
        assertEquals(
            R.drawable.ic_song_24,
            resolveSceneIconRes(banner("66666666-6666-4666-8666-666666666666", "Pop · 流行精选")),
        )
        assertEquals(
            R.drawable.ic_time_24,
            resolveSceneIconRes(banner("44444444-4444-4444-8444-444444444444", "沉睡馆藏唤醒")),
        )
        assertEquals(
            R.drawable.ic_playlist_24,
            resolveSceneIconRes(banner("custom-jazz", "Late Night Jazz")),
        )
    }
}
