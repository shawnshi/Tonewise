/*
 * Copyright (c) 2026 Tonewise Project
 * PlaybackAndClockP2Test.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.playback

import com.shawnshi.tonewise.playback.dsp.BiquadType
import com.shawnshi.tonewise.playback.dsp.DspChainConfig
import com.shawnshi.tonewise.playback.dsp.DspPresetId
import com.shawnshi.tonewise.playback.dsp.TonewiseDspEngine
import com.shawnshi.tonewise.playback.sleep.SleepTimerController
import com.shawnshi.tonewise.playback.sleep.SleepTimerMode
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class PlaybackAndClockP2Test {
    @Test
    fun sleepTimer_fadeGainAndEndOfWorkTransitionSemantics() {
        assertEquals(1f, SleepTimerController.computeFadeGain(15_000L), 1e-4f)
        assertEquals(1f, SleepTimerController.computeFadeGain(10_000L), 1e-4f)
        assertEquals(0.5f, SleepTimerController.computeFadeGain(5_000L), 1e-4f)
        assertEquals(0f, SleepTimerController.computeFadeGain(0L), 1e-4f)

        assertFalse(SleepTimerController.shouldStopAtTransition(SleepTimerMode.OFF, "a", "b"))
        assertTrue(SleepTimerController.shouldStopAtTransition(SleepTimerMode.END_OF_TRACK, "a", "a"))
        assertFalse(
            SleepTimerController.shouldStopAtTransition(
                SleepTimerMode.END_OF_WORK,
                "karajan|dg::symphony no. 5",
                "karajan|dg::symphony no. 5",
            )
        )
        assertTrue(
            SleepTimerController.shouldStopAtTransition(
                SleepTimerMode.END_OF_WORK,
                "karajan|dg::symphony no. 5",
                "kleiber|dg::symphony no. 5",
            )
        )
    }

    @Test
    fun equalizerApo_parsesNumberedFilterLinesAndMatchesDesktopBluetoothPreset() {
        val apo =
            """
            Preamp: -4.0 dB
            Filter 1: ON PK Fc 1000 Hz Gain 2.5 dB Q 1.41
            Filter 2: ON LSC Fc 80 Hz Gain 3.0 dB Q 0.71
            FilterBank: ON PK Fc 500 Hz Gain 1.0 dB Q 1.0
            """
                .trimIndent()
        val parsed = TonewiseDspEngine.parseEqualizerApo(apo)
        assertEquals(-4.0, parsed.preampDb, 1e-4)
        assertEquals(2, parsed.bands.size)
        assertEquals(BiquadType.PEAKING, parsed.bands[0].type)
        assertEquals(BiquadType.LOW_SHELF, parsed.bands[1].type)

        val expectedCenters =
            listOf(31.0, 62.0, 125.0, 250.0, 500.0, 1000.0, 2000.0, 4000.0, 8000.0, 16000.0)
        val btPreset = DspChainConfig.preset(DspPresetId.OPEN_EAR_BLUETOOTH)
        assertEquals(-4.5, btPreset.preampDb, 1e-4)
        assertEquals(expectedCenters, btPreset.bands.map { it.freqHz })

        val nightPreset = DspChainConfig.preset(DspPresetId.FOCUS_NIGHT)
        assertEquals(expectedCenters, nightPreset.bands.map { it.freqHz })
        assertTrue(nightPreset.crossfeedEnabled)
    }
}
