/*
 * Copyright (c) 2026 Tonewise Project
 * Phase3ScalingAndAudioTest.kt is part of Tonewise.
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

package com.shawnshi.tonewise

import androidx.media3.common.C
import kotlin.math.pow
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.oxycblt.musikr.core.TonewiseCore
import com.shawnshi.tonewise.playback.audio.AudioOutputManager
import com.shawnshi.tonewise.playback.audio.AudioOutputManager.BitPerfectCandidate
import com.shawnshi.tonewise.playback.dsp.BiquadBand
import com.shawnshi.tonewise.playback.dsp.BiquadType
import com.shawnshi.tonewise.playback.dsp.DspChainConfig
import com.shawnshi.tonewise.playback.dsp.DspPresetId
import com.shawnshi.tonewise.playback.dsp.TonewiseDspEngine
import com.shawnshi.tonewise.sync.FocusFilterSpec
import com.shawnshi.tonewise.sync.FocusPillState
import com.shawnshi.tonewise.sync.ManagedSyncStorage
import com.shawnshi.tonewise.sync.SceneRuleEvaluator

class Phase3ScalingAndAudioTest {
    @Test
    fun safPathCache_supportsLookupUpdateEvictionAndInvalidation() {
        val cache = ManagedSyncStorage.SafPathCache()
        val tree = "content://com.android.externalstorage.documents/tree/primary%3AMusic"
        val dir = "primary:Music/Artist"

        assertEquals(false to null, cache.lookup(tree, dir, "track.flac"))

        val entry1 = ManagedSyncStorage.SafChildInfo("doc_1", false, 1234L, 100L)
        cache.putDir(tree, dir, mapOf("Track.FLAC" to entry1))

        val (hit1, info1) = cache.lookup(tree, dir, "track.flac")
        assertTrue(hit1)
        assertEquals(entry1, info1)

        val (hitMissing, missingInfo) = cache.lookup(tree, dir, "absent.flac")
        assertTrue(hitMissing)
        assertNull(missingInfo)

        val entry2 = ManagedSyncStorage.SafChildInfo("doc_2", false, 5678L, 200L)
        cache.putEntry(tree, dir, "track2.flac", entry2)
        assertEquals(true to entry2, cache.lookup(tree, dir, "TRACK2.FLAC"))

        cache.removeEntry(tree, dir, "track.flac")
        assertEquals(true to null, cache.lookup(tree, dir, "track.flac"))

        cache.invalidateDir(tree, dir)
        assertEquals(false to null, cache.lookup(tree, dir, "track2.flac"))

        cache.putEntry(tree, "other", "x.flac", entry1)
        cache.clear()
        assertEquals(false to null, cache.lookup(tree, "other", "x.flac"))
    }

    @Test
    fun tonewiseDspEngine_cachedGainMatchedBypass_matchesRmsShiftAcrossUpdates() {
        val engine = TonewiseDspEngine()
        engine.configure(48000, 2)

        val cfg =
            DspChainConfig(
                presetId = DspPresetId.CUSTOM,
                enabled = true,
                gainMatchedBypass = true,
                preampDb = -2.0,
                bands =
                    listOf(
                        BiquadBand(BiquadType.PEAKING, 1000.0, 4.0, 1.2, enabled = true),
                        BiquadBand(BiquadType.LOW_SHELF, 90.0, 3.0, 0.71, enabled = true),
                    ),
            )
        engine.updateConfig(cfg)

        val expectedShiftDb = TonewiseDspEngine.estimateRmsLoudnessShiftDb(cfg, 48000)
        val expectedGain1 = 10.0.pow((-3.5 + expectedShiftDb).coerceIn(-24.0, 12.0) / 20.0)
        assertEquals(expectedGain1, engine.computeStage1LinearGain(-3.5), 1e-9)
        // Cached hit on identical replayGainDb
        assertEquals(expectedGain1, engine.computeStage1LinearGain(-3.5), 1e-9)

        val expectedGain2 = 10.0.pow((-1.0 + expectedShiftDb).coerceIn(-24.0, 12.0) / 20.0)
        assertEquals(expectedGain2, engine.computeStage1LinearGain(-1.0), 1e-9)
    }

    @Test
    fun audioOutputManager_selectBestBitPerfectCandidate_prefersIntegerPcmAndMatchingRate() {
        val candidates =
            listOf(
                BitPerfectCandidate(44100, AudioOutputManager.PCM_ENCODING_16BIT),
                BitPerfectCandidate(44100, AudioOutputManager.PCM_ENCODING_24BIT_PACKED),
                BitPerfectCandidate(44100, AudioOutputManager.PCM_ENCODING_FLOAT),
                BitPerfectCandidate(96000, AudioOutputManager.PCM_ENCODING_32BIT),
                BitPerfectCandidate(96000, AudioOutputManager.PCM_ENCODING_FLOAT),
            )

        // 44.1kHz 24-bit source should choose 44.1kHz integer 24-bit packed over float/16-bit
        val picked24 =
            AudioOutputManager.selectBestBitPerfectCandidate(
                candidates = candidates,
                targetSampleRateHz = 44100,
                sourceMedia3PcmEncoding = C.ENCODING_PCM_24BIT,
            )
        assertEquals(
            BitPerfectCandidate(44100, AudioOutputManager.PCM_ENCODING_24BIT_PACKED),
            picked24,
        )

        // 96kHz 24-bit source without 24-bit at 96kHz should promote cleanly to 96kHz 32-bit integer (not float)
        val picked96 =
            AudioOutputManager.selectBestBitPerfectCandidate(
                candidates = candidates,
                targetSampleRateHz = 96000,
                sourceMedia3PcmEncoding = C.ENCODING_PCM_24BIT,
            )
        assertEquals(
            BitPerfectCandidate(96000, AudioOutputManager.PCM_ENCODING_32BIT),
            picked96,
        )

        assertFalse(
            AudioOutputManager.resolveEnableFloatOutput(
                floatOutput = true,
                usbBitPerfect = true,
                apiLevel = 34,
            )
        )
        assertTrue(
            AudioOutputManager.resolveEnableFloatOutput(
                floatOutput = true,
                usbBitPerfect = true,
                apiLevel = 33,
            )
        )
        assertTrue(
            AudioOutputManager.resolveEnableFloatOutput(
                floatOutput = true,
                usbBitPerfect = false,
                apiLevel = 35,
            )
        )
    }

    @Test
    fun sceneRuleEvaluator_escapesSpecialCharsAndResolvesSsotMood() {
        assertEquals("calm_relaxed", SceneRuleEvaluator.resolveMoodClusterId("Peaceful"))
        assertEquals("high_tension", SceneRuleEvaluator.resolveMoodClusterId("DRAMATIC"))

        val spec =
            FocusFilterSpec(
                scenes = mapOf("focus\"\\test\nline" to FocusPillState.INCLUDE),
                moods = mapOf("calm_relaxed" to FocusPillState.INCLUDE),
                sortArc = "flat",
            )
        val json = SceneRuleEvaluator.buildFocusFilterSceneJson(spec)
        assertNotNull(json)
        @Suppress("UNCHECKED_CAST")
        val parsed = TonewiseCore.parseJson(json!!) as Map<String, Any?>
        assertEquals("Focus Boolean Funnel", parsed["title"])

        val defaultScenes = SceneRuleEvaluator.parseScenesJson(SceneRuleEvaluator.DEFAULT_SCENES_JSON)
        assertEquals(6, defaultScenes.size)
    }
}
