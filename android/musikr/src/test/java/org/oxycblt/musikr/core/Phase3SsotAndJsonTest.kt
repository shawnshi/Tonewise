/*
 * Copyright (c) 2026 Tonewise Project
 * Phase3SsotAndJsonTest.kt is part of Tonewise.
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

package org.oxycblt.musikr.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class Phase3SsotAndJsonTest {
    @Test
    fun moodClustersRegistry_loadsCanonicalFiveClustersAndSynonyms() {
        assertEquals(5, MoodClustersRegistry.clusterCount())
        assertEquals("calm_relaxed", MoodClustersRegistry.resolveMoodClusterId("Chill"))
        assertEquals("deep_introspective", MoodClustersRegistry.resolveMoodClusterId("Melancholic"))
        assertEquals("high_tension", MoodClustersRegistry.resolveMoodClusterId("ENERGETIC"))
        assertEquals("warm_romantic", MoodClustersRegistry.resolveMoodClusterId("romantic"))
        assertEquals("bright_joyful", MoodClustersRegistry.resolveMoodClusterId("cheerful"))
        assertEquals("calm_relaxed", MoodClustersRegistry.resolveMoodClusterId("calm_relaxed"))
        assertNull(MoodClustersRegistry.resolveMoodClusterId("nonexistent_mood_token"))
    }

    @Test
    fun tonewiseCore_stringifyAndParseJson_roundTripsComplexEscapesAndRawJson() {
        val payload =
            linkedMapOf<String, Any?>(
                "title" to "Quote \"Hello\" \\ Path\nLine2\tCtrl\u0001End",
                "count" to 42L,
                "ratio" to 0.75,
                "active" to true,
                "nothing" to null,
                "tags" to listOf("a\"b", "c\\d", "focus"),
                "raw" to RawJson("[1,2,3]"),
            )

        val serialized = TonewiseCore.stringifyJson(payload)
        @Suppress("UNCHECKED_CAST")
        val parsed = TonewiseCore.parseJson(serialized) as Map<String, Any?>

        assertEquals("Quote \"Hello\" \\ Path\nLine2\tCtrl\u0001End", parsed["title"])
        assertEquals(42L, parsed["count"])
        assertEquals(0.75, (parsed["ratio"] as Number).toDouble(), 1e-6)
        assertEquals(true, parsed["active"])
        assertTrue(parsed.containsKey("nothing") && parsed["nothing"] == null)
        assertEquals(listOf("a\"b", "c\\d", "focus"), parsed["tags"])
        assertEquals(listOf(1L, 2L, 3L), parsed["raw"])
    }
}
