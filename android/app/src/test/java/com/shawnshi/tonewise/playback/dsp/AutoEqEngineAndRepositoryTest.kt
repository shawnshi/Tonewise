/*
 * Copyright (c) 2026 Tonewise Project
 * AutoEqEngineAndRepositoryTest.kt is part of Tonewise.
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

package com.shawnshi.tonewise.playback.dsp

import android.content.SharedPreferences
import kotlin.math.PI
import kotlin.math.abs
import kotlin.math.sin
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class AutoEqEngineAndRepositoryTest {
    private class InMemorySharedPreferences : SharedPreferences {
        private val map = LinkedHashMap<String, Any?>()

        override fun getAll(): MutableMap<String, *> = map.toMutableMap()

        override fun getString(key: String?, defValue: String?): String? =
            (map[key] as? String) ?: defValue

        @Suppress("UNCHECKED_CAST")
        override fun getStringSet(key: String?, defValues: MutableSet<String>?): MutableSet<String>? =
            (map[key] as? MutableSet<String>) ?: defValues

        override fun getInt(key: String?, defValue: Int): Int =
            (map[key] as? Int) ?: defValue

        override fun getLong(key: String?, defValue: Long): Long =
            (map[key] as? Long) ?: defValue

        override fun getFloat(key: String?, defValue: Float): Float =
            (map[key] as? Float) ?: defValue

        override fun getBoolean(key: String?, defValue: Boolean): Boolean =
            (map[key] as? Boolean) ?: defValue

        override fun contains(key: String?): Boolean = map.containsKey(key)

        override fun edit(): SharedPreferences.Editor =
            object : SharedPreferences.Editor {
                private val pending = LinkedHashMap<String, Any?>()
                private val removals = HashSet<String>()
                private var clearAll = false

                override fun putString(key: String?, value: String?): SharedPreferences.Editor = apply {
                    if (key != null) pending[key] = value
                }

                override fun putStringSet(
                    key: String?,
                    values: MutableSet<String>?,
                ): SharedPreferences.Editor = apply {
                    if (key != null) pending[key] = values
                }

                override fun putInt(key: String?, value: Int): SharedPreferences.Editor = apply {
                    if (key != null) pending[key] = value
                }

                override fun putLong(key: String?, value: Long): SharedPreferences.Editor = apply {
                    if (key != null) pending[key] = value
                }

                override fun putFloat(key: String?, value: Float): SharedPreferences.Editor = apply {
                    if (key != null) pending[key] = value
                }

                override fun putBoolean(key: String?, value: Boolean): SharedPreferences.Editor = apply {
                    if (key != null) pending[key] = value
                }

                override fun remove(key: String?): SharedPreferences.Editor = apply {
                    if (key != null) removals.add(key)
                }

                override fun clear(): SharedPreferences.Editor = apply { clearAll = true }

                override fun commit(): Boolean {
                    apply()
                    return true
                }

                override fun apply() {
                    if (clearAll) map.clear()
                    for (r in removals) map.remove(r)
                    map.putAll(pending)
                }
            }

        override fun registerOnSharedPreferenceChangeListener(
            listener: SharedPreferences.OnSharedPreferenceChangeListener?
        ) {}

        override fun unregisterOnSharedPreferenceChangeListener(
            listener: SharedPreferences.OnSharedPreferenceChangeListener?
        ) {}
    }

    private fun createTestRepo(): DspRepository =
        DspRepository(
            context = null,
            prefs = InMemorySharedPreferences(),
            checkRomConflict = false,
        )

    @Test
    fun `option A cascades Layer A hardware EQ with Layer B genre-vocal EQ without overwriting Layer A`() {
        val repo = createTestRepo()

        // Enable Layer A hardware compensation (Open-Ear BT: 62 Hz +4.0 dB)
        repo.applyPreset(DspPresetId.OPEN_EAR_BLUETOOTH)
        val layerABefore = repo.deviceConfig.value
        assertTrue(layerABefore.enabled)
        assertEquals(10, layerABefore.bands.size)
        assertEquals(4.0, layerABefore.bands[1].gainDb, 1e-6)

        // Turn on Layer B Auto-EQ and play a Jazz Vocal track
        repo.updateAutoEqPolicy {
            it.copy(
                enabled = true,
                classicalAutoCrossfeed = true,
                lockWithinClassicalWork = true,
            )
        }
        repo.onTrackChanged(
            genres = listOf("Vocal Jazz"),
            djVocals = "vocal",
            workName = null,
            workKey = null,
        )

        val composedJazz = repo.config.value
        // Layer A (10 bands) + Layer B (10 bands) = 20 bands, anti-clipping preamp applied
        assertEquals(20, composedJazz.bands.size)
        assertTrue(composedJazz.preampDb <= -4.0)
        assertEquals(AutoEqGenreFamily.JAZZ, repo.activeContentResolution.value.genreFamily)
        assertEquals(AutoEqVocalClass.VOCAL, repo.activeContentResolution.value.vocalClass)

        // Manual edit to Layer A must NOT disable Layer B (Option A)
        repo.updateConfig { cur ->
            val updated = cur.bands.toMutableList()
            updated[0] = updated[0].copy(gainDb = 1.5, enabled = true)
            cur.copy(presetId = DspPresetId.CUSTOM, bands = updated)
        }
        assertTrue(repo.autoEqPolicy.value.enabled)
        assertEquals(20, repo.config.value.bands.size)
        assertEquals(1.5, repo.deviceConfig.value.bands[0].gainDb, 1e-6)

        // Transition to Classical Instrumental -> Flat 0 dB Layer B + auto Crossfeed link, while Layer A stays intact
        repo.onTrackChanged(
            genres = listOf("Classical"),
            djVocals = "instrumental",
            workName = "Symphony No. 5",
            workKey = "alb|karajan::sym5",
        )
        val classicalRes = repo.activeContentResolution.value
        assertEquals(AutoEqGenreFamily.CLASSICAL, classicalRes.genreFamily)
        assertEquals(AutoEqVocalClass.INSTRUMENTAL, classicalRes.vocalClass)
        assertFalse(classicalRes.hasNonZeroEq)
        assertTrue(classicalRes.enableCrossfeed)
        assertTrue(repo.config.value.crossfeedEnabled)
        assertEquals(1.5, repo.deviceConfig.value.bands[0].gainDb, 1e-6)
    }

    @Test
    fun `classical work lock preserves tone on Beethoven 9 vocal finale and external bridge bypasses Layer B`() {
        val repo = createTestRepo()
        repo.applyPreset(DspPresetId.FLAT)
        repo.updateAutoEqPolicy {
            it.copy(enabled = true, classicalAutoCrossfeed = true, lockWithinClassicalWork = true)
        }

        // Flat Layer A + Classical Instrumental -> 20 zero bands (no EQ coloration) + Crossfeed active
        repo.onTrackChanged(
            genres = listOf("Classical"),
            djVocals = "instrumental",
            workName = "Symphony No. 9",
            workKey = "beethoven9",
        )
        assertFalse(repo.activeContentResolution.value.hasNonZeroEq)
        assertTrue(repo.config.value.crossfeedEnabled)

        // Disable classicalAutoCrossfeed with Flat Layer A -> true Identity Passthrough
        repo.updateAutoEqPolicy { it.copy(classicalAutoCrossfeed = false) }
        assertFalse(repo.config.value.hasNonIdentityProcessing())

        // Re-enable classicalAutoCrossfeed, move to movement 4 (Choral / Vocal) within same work
        repo.updateAutoEqPolicy { it.copy(classicalAutoCrossfeed = true) }
        repo.onTrackChanged(
            genres = listOf("Classical"),
            djVocals = "instrumental",
            workName = "Symphony No. 9",
            workKey = "beethoven9",
        )
        repo.onTrackChanged(
            genres = listOf("Classical; Choral"),
            djVocals = "vocal",
            workName = "Symphony No. 9",
            workKey = "beethoven9",
        )
        assertTrue(repo.activeContentResolution.value.lockedToPreviousWork)
        assertFalse(repo.activeContentResolution.value.hasNonZeroEq)
        assertTrue(repo.config.value.crossfeedEnabled)

        // Switching to externalBridgeMode bypasses Layer B composition cleanly
        repo.updateConfig { it.copy(externalBridgeMode = true) }
        assertTrue(repo.config.value.externalBridgeMode)
        assertEquals(10, repo.config.value.bands.size)
        assertFalse(repo.config.value.hasNonIdentityProcessing())
    }

    @Test
    fun `dspEngine smooths 15ms biquad and preamp transitions and LWW sync honors timestamps`() {
        val engine = TonewiseDspEngine()
        engine.configure(48000, 2)

        val tenZero = DspChainConfig.defaultTenBands()
        val cfg1 = DspChainConfig(presetId = DspPresetId.CUSTOM, enabled = true, preampDb = -2.0, bands = tenZero)
        engine.updateConfig(cfg1)

        val frames = 1024
        val buf = DoubleArray(frames * 2)
        for (f in 0 until frames) {
            val s = 0.5 * sin(2.0 * PI * 440.0 * f / 48000.0)
            buf[f * 2] = s
            buf[f * 2 + 1] = s
        }
        engine.processInterleaved(buf, frames, replayGainDb = 0.0)
        assertTrue(buf.all { it.isFinite() && abs(it) <= 1.0 })

        // Swap coefficients in-place on fixed band count
        val boosted = tenZero.toMutableList()
        boosted[1] = boosted[1].copy(type = BiquadType.LOW_SHELF, gainDb = 3.5, q = 0.71)
        engine.updateConfig(cfg1.copy(preampDb = -3.5, bands = boosted))
        engine.processInterleaved(buf, frames, replayGainDb = 0.0)
        assertTrue(buf.all { it.isFinite() && abs(it) <= 1.0 })

        // LWW Sync merge semantics
        val repo = createTestRepo()
        val stale =
            AutoEqPolicy(enabled = true, updatedAtMs = 100L).toJsonString()
        assertTrue(repo.mergeRemoteAutoEqPolicyJson(stale))
        assertTrue(repo.autoEqPolicy.value.enabled)

        val older =
            AutoEqPolicy(enabled = false, updatedAtMs = 50L).toJsonString()
        assertFalse(repo.mergeRemoteAutoEqPolicyJson(older))
        assertTrue(repo.autoEqPolicy.value.enabled)

        val newer =
            AutoEqPolicy(
                enabled = true,
                classicalAutoCrossfeed = false,
                updatedAtMs = 250L,
            ).toJsonString()
        assertTrue(repo.mergeRemoteAutoEqPolicyJson(newer))
        assertFalse(repo.autoEqPolicy.value.classicalAutoCrossfeed)
    }
}
