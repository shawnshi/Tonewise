/*
 * Copyright (c) 2026 Tonewise Project
 * TonewiseDspEngine.kt is part of Tonewise.
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

import kotlin.math.PI
import kotlin.math.abs
import kotlin.math.cos
import kotlin.math.exp
import kotlin.math.hypot
import kotlin.math.ln
import kotlin.math.log10
import kotlin.math.max
import kotlin.math.min
import kotlin.math.pow
import kotlin.math.roundToInt
import kotlin.math.sin
import kotlin.math.sqrt
import kotlin.math.tanh
import kotlinx.coroutines.flow.StateFlow
import org.json.JSONArray
import org.json.JSONObject

enum class BiquadType {
    PEAKING,
    LOW_SHELF,
    HIGH_SHELF,
    LOW_PASS,
    HIGH_PASS,
}

data class BiquadBand(
    val type: BiquadType = BiquadType.PEAKING,
    val freqHz: Double,
    val gainDb: Double,
    val q: Double = 1.41,
    val enabled: Boolean = true,
)

enum class DspPresetId {
    FLAT,
    OPEN_EAR_BLUETOOTH,
    FOCUS_NIGHT,
    CLASSICAL_CROSSFEED,
    CUSTOM,
}

data class DspChainConfig(
    val presetId: DspPresetId = DspPresetId.FLAT,
    val enabled: Boolean = false,
    val externalBridgeMode: Boolean = false,
    val gainMatchedBypass: Boolean = false,
    val preampDb: Double = 0.0,
    val bands: List<BiquadBand> = defaultTenBands(),
    val crossfeedEnabled: Boolean = false,
    val crossfeedCutoffHz: Double = 700.0,
    val crossfeedFeedDb: Double = 4.5,
    val crossfeedDelayMs: Double = 0.28,
    val balanceDb: Double = 0.0,
    val invertLeftPhase: Boolean = false,
    val invertRightPhase: Boolean = false,
    val monoFold: Boolean = false,
    val limiterEnabled: Boolean = true,
    val limiterCeilingDb: Double = -0.2,
) {
    fun hasNonIdentityProcessing(): Boolean {
        if (!enabled) return false
        if (externalBridgeMode) return false
        if (gainMatchedBypass) return true
        if (abs(preampDb) > 1e-4) return true
        if (bands.any { it.enabled && (abs(it.gainDb) > 1e-4 || it.type == BiquadType.LOW_PASS || it.type == BiquadType.HIGH_PASS) }) {
            return true
        }
        if (crossfeedEnabled) return true
        if (abs(balanceDb) > 1e-4 || invertLeftPhase || invertRightPhase || monoFold) return true
        return false
    }

    fun toJson(): JSONObject =
        JSONObject()
            .put("preset_id", presetId.name)
            .put("enabled", enabled)
            .put("external_bridge_mode", externalBridgeMode)
            .put("gain_matched_bypass", gainMatchedBypass)
            .put("preamp_db", preampDb)
            .put("crossfeed_enabled", crossfeedEnabled)
            .put("crossfeed_cutoff_hz", crossfeedCutoffHz)
            .put("crossfeed_feed_db", crossfeedFeedDb)
            .put("crossfeed_delay_ms", crossfeedDelayMs)
            .put("balance_db", balanceDb)
            .put("invert_left_phase", invertLeftPhase)
            .put("invert_right_phase", invertRightPhase)
            .put("mono_fold", monoFold)
            .put("limiter_enabled", limiterEnabled)
            .put("limiter_ceiling_db", limiterCeilingDb)
            .put(
                "bands",
                JSONArray().apply {
                    for (b in bands) {
                        put(
                            JSONObject()
                                .put("type", b.type.name)
                                .put("freq_hz", b.freqHz)
                                .put("gain_db", b.gainDb)
                                .put("q", b.q)
                                .put("enabled", b.enabled)
                        )
                    }
                },
            )

    companion object {
        fun defaultTenBands(): List<BiquadBand> =
            listOf(31.0, 62.0, 125.0, 250.0, 500.0, 1000.0, 2000.0, 4000.0, 8000.0, 16000.0)
                .map { BiquadBand(BiquadType.PEAKING, it, 0.0, 1.41, true) }

        fun preset(id: DspPresetId): DspChainConfig =
            when (id) {
                DspPresetId.FLAT ->
                    DspChainConfig(
                        presetId = DspPresetId.FLAT,
                        enabled = false,
                        bands = defaultTenBands(),
                    )
                DspPresetId.OPEN_EAR_BLUETOOTH ->
                    DspChainConfig(
                        presetId = DspPresetId.OPEN_EAR_BLUETOOTH,
                        enabled = true,
                        preampDb = -4.5,
                        bands =
                            listOf(
                                BiquadBand(BiquadType.LOW_SHELF, 85.0, 4.5, 0.71, true),
                                BiquadBand(BiquadType.PEAKING, 250.0, -1.0, 1.2, true),
                                BiquadBand(BiquadType.PEAKING, 2000.0, 1.0, 1.4, true),
                                BiquadBand(BiquadType.HIGH_SHELF, 8000.0, 1.5, 0.71, true),
                            ) +
                                listOf(31.0, 62.0, 500.0, 1000.0, 4000.0, 16000.0).map {
                                    BiquadBand(BiquadType.PEAKING, it, 0.0, 1.41, true)
                                },
                        limiterEnabled = true,
                    )
                DspPresetId.FOCUS_NIGHT ->
                    DspChainConfig(
                        presetId = DspPresetId.FOCUS_NIGHT,
                        enabled = true,
                        preampDb = -1.5,
                        bands =
                            listOf(
                                BiquadBand(BiquadType.LOW_SHELF, 85.0, 1.5, 0.71, true),
                                BiquadBand(BiquadType.HIGH_SHELF, 6000.0, -2.5, 0.71, true),
                            ) +
                                listOf(31.0, 62.0, 125.0, 250.0, 500.0, 1000.0, 2000.0, 4000.0).map {
                                    BiquadBand(BiquadType.PEAKING, it, 0.0, 1.41, true)
                                },
                        crossfeedEnabled = true,
                        crossfeedCutoffHz = 700.0,
                        crossfeedFeedDb = 4.5,
                        limiterEnabled = true,
                    )
                DspPresetId.CLASSICAL_CROSSFEED ->
                    DspChainConfig(
                        presetId = DspPresetId.CLASSICAL_CROSSFEED,
                        enabled = true,
                        preampDb = 0.0,
                        bands = defaultTenBands(),
                        crossfeedEnabled = true,
                        crossfeedCutoffHz = 700.0,
                        crossfeedFeedDb = 4.5,
                        crossfeedDelayMs = 0.28,
                        limiterEnabled = true,
                    )
                DspPresetId.CUSTOM -> DspChainConfig(presetId = DspPresetId.CUSTOM, enabled = true)
            }

        fun fromJson(obj: JSONObject): DspChainConfig {
            val bandsArr = obj.optJSONArray("bands")
            val parsedBands =
                if (bandsArr != null && bandsArr.length() > 0) {
                    List(bandsArr.length()) { idx ->
                        val b = bandsArr.getJSONObject(idx)
                        BiquadBand(
                            type =
                                runCatching { BiquadType.valueOf(b.optString("type", "PEAKING")) }
                                    .getOrDefault(BiquadType.PEAKING),
                            freqHz = b.optDouble("freq_hz", 1000.0),
                            gainDb = b.optDouble("gain_db", 0.0),
                            q = b.optDouble("q", 1.41),
                            enabled = b.optBoolean("enabled", true),
                        )
                    }
                } else {
                    defaultTenBands()
                }
            return DspChainConfig(
                presetId =
                    runCatching { DspPresetId.valueOf(obj.optString("preset_id", "FLAT")) }
                        .getOrDefault(DspPresetId.FLAT),
                enabled = obj.optBoolean("enabled", false),
                externalBridgeMode = obj.optBoolean("external_bridge_mode", false),
                gainMatchedBypass = obj.optBoolean("gain_matched_bypass", false),
                preampDb = obj.optDouble("preamp_db", 0.0),
                bands = parsedBands,
                crossfeedEnabled = obj.optBoolean("crossfeed_enabled", false),
                crossfeedCutoffHz = obj.optDouble("crossfeed_cutoff_hz", 700.0),
                crossfeedFeedDb = obj.optDouble("crossfeed_feed_db", 4.5),
                crossfeedDelayMs = obj.optDouble("crossfeed_delay_ms", 0.28),
                balanceDb = obj.optDouble("balance_db", 0.0),
                invertLeftPhase = obj.optBoolean("invert_left_phase", false),
                invertRightPhase = obj.optBoolean("invert_right_phase", false),
                monoFold = obj.optBoolean("mono_fold", false),
                limiterEnabled = obj.optBoolean("limiter_enabled", true),
                limiterCeilingDb = obj.optDouble("limiter_ceiling_db", -0.2),
            )
        }
    }
}

interface DspConfigSource {
    val config: StateFlow<DspChainConfig>
}

/**
 * 64-bit IEEE-754 double precision Direct Form II Transposed Biquad IIR filter matching
 * RBJ Audio EQ Cookbook and desktop `BiquadFilter64`.
 */
class BiquadFilter64(band: BiquadBand, sampleRateHz: Int, channels: Int) {
    private val b0: Double
    private val b1: Double
    private val b2: Double
    private val a1: Double
    private val a2: Double
    private val z1 = DoubleArray(max(channels, 1))
    private val z2 = DoubleArray(max(channels, 1))

    init {
        val fs = max(sampleRateHz, 8000).toDouble()
        val fc = band.freqHz.coerceIn(10.0, fs * 0.49)
        val w0 = 2.0 * PI * fc / fs
        val cosW0 = cos(w0)
        val sinW0 = sin(w0)
        val q = band.q.coerceIn(0.05, 30.0)
        val alpha = sinW0 / (2.0 * q)
        val a = 10.0.pow(band.gainDb.coerceIn(-24.0, 24.0) / 40.0)

        var rb0 = 1.0
        var rb1 = 0.0
        var rb2 = 0.0
        var ra0 = 1.0
        var ra1 = 0.0
        var ra2 = 0.0

        when (band.type) {
            BiquadType.PEAKING -> {
                rb0 = 1.0 + alpha * a
                rb1 = -2.0 * cosW0
                rb2 = 1.0 - alpha * a
                ra0 = 1.0 + alpha / a
                ra1 = -2.0 * cosW0
                ra2 = 1.0 - alpha / a
            }
            BiquadType.LOW_SHELF -> {
                val twoSqrtAAlpha = 2.0 * sqrt(a) * alpha
                rb0 = a * ((a + 1.0) - (a - 1.0) * cosW0 + twoSqrtAAlpha)
                rb1 = 2.0 * a * ((a - 1.0) - (a + 1.0) * cosW0)
                rb2 = a * ((a + 1.0) - (a - 1.0) * cosW0 - twoSqrtAAlpha)
                ra0 = (a + 1.0) + (a - 1.0) * cosW0 + twoSqrtAAlpha
                ra1 = -2.0 * ((a - 1.0) + (a + 1.0) * cosW0)
                ra2 = (a + 1.0) + (a - 1.0) * cosW0 - twoSqrtAAlpha
            }
            BiquadType.HIGH_SHELF -> {
                val twoSqrtAAlpha = 2.0 * sqrt(a) * alpha
                rb0 = a * ((a + 1.0) + (a - 1.0) * cosW0 + twoSqrtAAlpha)
                rb1 = -2.0 * a * ((a - 1.0) + (a + 1.0) * cosW0)
                rb2 = a * ((a + 1.0) + (a - 1.0) * cosW0 - twoSqrtAAlpha)
                ra0 = (a + 1.0) - (a - 1.0) * cosW0 + twoSqrtAAlpha
                ra1 = 2.0 * ((a - 1.0) - (a + 1.0) * cosW0)
                ra2 = (a + 1.0) - (a - 1.0) * cosW0 - twoSqrtAAlpha
            }
            BiquadType.LOW_PASS -> {
                rb0 = (1.0 - cosW0) / 2.0
                rb1 = 1.0 - cosW0
                rb2 = (1.0 - cosW0) / 2.0
                ra0 = 1.0 + alpha
                ra1 = -2.0 * cosW0
                ra2 = 1.0 - alpha
            }
            BiquadType.HIGH_PASS -> {
                rb0 = (1.0 + cosW0) / 2.0
                rb1 = -(1.0 + cosW0)
                rb2 = (1.0 + cosW0) / 2.0
                ra0 = 1.0 + alpha
                ra1 = -2.0 * cosW0
                ra2 = 1.0 - alpha
            }
        }

        b0 = rb0 / ra0
        b1 = rb1 / ra0
        b2 = rb2 / ra0
        a1 = ra1 / ra0
        a2 = ra2 / ra0
    }

    fun processSample(x: Double, channel: Int): Double {
        val y = b0 * x + z1[channel]
        z1[channel] = b1 * x - a1 * y + z2[channel]
        z2[channel] = b2 * x - a2 * y
        return y
    }

    fun magnitudeAtHz(freqHz: Double, sampleRateHz: Int): Double {
        val w = 2.0 * PI * freqHz / max(sampleRateHz, 8000).toDouble()
        val cw = cos(w)
        val sw = sin(w)
        val c2w = cos(2.0 * w)
        val s2w = sin(2.0 * w)
        val numRe = b0 + b1 * cw + b2 * c2w
        val numIm = -(b1 * sw + b2 * s2w)
        val denRe = 1.0 + a1 * cw + a2 * c2w
        val denIm = -(a1 * sw + a2 * s2w)
        val den = hypot(denRe, denIm)
        return if (den > 1e-12) hypot(numRe, numIm) / den else 1.0
    }

    fun reset() {
        z1.fill(0.0)
        z2.fill(0.0)
    }
}

/**
 * 5-stage 64-bit (`Double`) serial audio processor engine:
 * 1. Preamp + ReplayGain safe envelope (with Gain-Matched A/B compensation)
 * 2. Up to 64 cascaded 64-bit RBJ Biquad PEQ filters
 * 3. Stereo Bauer Crossfeed network
 * 4. L/R Balance, 180-deg phase inversion & mono fold
 * 5. Lookahead -0.2 dBFS soft-knee limiter
 */
class TonewiseDspEngine {
    private var sampleRateHz: Int = 48000
    private var channels: Int = 2
    private var config: DspChainConfig = DspChainConfig()

    private var filters: List<BiquadFilter64> = emptyList()

    // Stage 3: Bauer Crossfeed states (stereo only)
    private var crossLpL = 0.0
    private var crossLpR = 0.0
    private var crossAlphaLp = 0.0
    private var crossDirectGain = 1.0
    private var crossFeedGain = 0.0
    private var crossDelaySamples = 12
    private var crossDelayBufL = DoubleArray(64)
    private var crossDelayBufR = DoubleArray(64)
    private var crossDelayPos = 0

    // Stage 5: 3ms Lookahead Limiter states
    private var limiterDelaySamples = 144
    private var limiterDelayBuf = Array(2) { DoubleArray(160) }
    private var limiterDelayPos = 0
    private var limiterEnvelope = 1.0
    private var limiterReleaseCoeff = 0.999
    private var limiterCeiling = 10.0.pow(-0.2 / 20.0)

    fun configure(sampleRateHz: Int, channels: Int) {
        this.sampleRateHz = max(sampleRateHz, 8000)
        this.channels = max(channels, 1)
        rebuildStages()
        resetState()
    }

    fun updateConfig(newConfig: DspChainConfig) {
        if (this.config != newConfig) {
            this.config = newConfig
            rebuildStages()
        }
    }

    fun shouldProcessStages(replayGainLinear: Double): Boolean {
        if (config.hasNonIdentityProcessing()) return true
        return abs(replayGainLinear - 1.0) > 1e-6
    }

    fun computeStage1LinearGain(replayGainDb: Double): Double {
        if (!config.enabled || config.externalBridgeMode) {
            return 10.0.pow(replayGainDb / 20.0)
        }
        if (config.gainMatchedBypass) {
            val eqRmsShiftDb = estimateRmsLoudnessShiftDb(config, sampleRateHz)
            return 10.0.pow((replayGainDb + eqRmsShiftDb).coerceIn(-24.0, 12.0) / 20.0)
        }
        val effectiveDb = computeEffectivePreampDb(config, replayGainDb)
        return 10.0.pow(effectiveDb.coerceIn(-24.0, 12.0) / 20.0)
    }

    fun processInterleaved(samples: DoubleArray, frameCount: Int, replayGainDb: Double) {
        val stage1Gain = computeStage1LinearGain(replayGainDb)
        val chCount = channels
        val activeInternal =
            config.enabled && !config.externalBridgeMode && !config.gainMatchedBypass

        val runFilters = activeInternal && filters.isNotEmpty()
        val runCrossfeed = activeInternal && config.crossfeedEnabled && chCount == 2
        val balanceDb = config.balanceDb.coerceIn(-6.0, 6.0)
        val leftGain =
            (if (activeInternal && balanceDb > 0.0) 10.0.pow(-balanceDb / 20.0) else 1.0) *
                (if (activeInternal && config.invertLeftPhase) -1.0 else 1.0)
        val rightGain =
            (if (activeInternal && balanceDb < 0.0) 10.0.pow(balanceDb / 20.0) else 1.0) *
                (if (activeInternal && config.invertRightPhase) -1.0 else 1.0)
        val runBalancePhase =
            activeInternal &&
                (abs(balanceDb) > 1e-4 ||
                    config.invertLeftPhase ||
                    config.invertRightPhase ||
                    config.monoFold)
        val runLimiter =
            config.limiterEnabled && (!config.externalBridgeMode || config.enabled || abs(replayGainDb) > 1e-4)

        val frameWork = DoubleArray(chCount)
        for (frame in 0 until frameCount) {
            val base = frame * chCount
            // Stage 1 + Stage 2: Preamp + ReplayGain + 64-bit Biquad PEQ
            for (c in 0 until chCount) {
                var x = samples[base + c] * stage1Gain
                if (runFilters) {
                    for (idx in filters.indices) {
                        x = filters[idx].processSample(x, c)
                    }
                }
                frameWork[c] = x
            }

            // Stage 3: Bauer Stereo-to-Binaural Crossfeed
            if (runCrossfeed) {
                val inL = frameWork[0]
                val inR = frameWork[1]
                crossLpL += crossAlphaLp * (inL - crossLpL)
                crossLpR += crossAlphaLp * (inR - crossLpR)
                val delayedR = crossDelayBufR[crossDelayPos]
                val delayedL = crossDelayBufL[crossDelayPos]
                crossDelayBufL[crossDelayPos] = crossLpL
                crossDelayBufR[crossDelayPos] = crossLpR
                crossDelayPos = (crossDelayPos + 1) % crossDelaySamples
                frameWork[0] = inL * crossDirectGain + delayedR * crossFeedGain
                frameWork[1] = inR * crossDirectGain + delayedL * crossFeedGain
            }

            // Stage 4: Balance, Polarity Invert & Mono Fold
            if (runBalancePhase) {
                if (chCount >= 2) {
                    var l = frameWork[0] * leftGain
                    var r = frameWork[1] * rightGain
                    if (config.monoFold) {
                        val m = 0.5 * (l + r)
                        l = m
                        r = m
                    }
                    frameWork[0] = l
                    frameWork[1] = r
                } else {
                    frameWork[0] *= leftGain
                }
            }

            // Stage 5: 3ms Lookahead Soft-Knee Limiter (-0.2 dBFS default)
            if (runLimiter) {
                var futurePeak = 0.0
                for (c in 0 until chCount) {
                    val sample = frameWork[c]
                    val delayed = limiterDelayBuf[c][limiterDelayPos]
                    limiterDelayBuf[c][limiterDelayPos] = sample
                    futurePeak = max(futurePeak, abs(sample))
                    frameWork[c] = delayed
                }
                limiterDelayPos = (limiterDelayPos + 1) % limiterDelaySamples

                var instantPeak = futurePeak
                for (c in 0 until chCount) {
                    instantPeak = max(instantPeak, abs(frameWork[c]))
                }
                val targetGain =
                    if (instantPeak > limiterCeiling) limiterCeiling / instantPeak else 1.0
                limiterEnvelope =
                    if (targetGain < limiterEnvelope) {
                        targetGain
                    } else {
                        limiterEnvelope * limiterReleaseCoeff +
                            targetGain * (1.0 - limiterReleaseCoeff)
                    }
                for (c in 0 until chCount) {
                    val limited = frameWork[c] * limiterEnvelope
                    samples[base + c] =
                        if (abs(limited) > limiterCeiling) {
                            limiterCeiling * tanh(limited / limiterCeiling)
                        } else {
                            limited
                        }
                }
            } else {
                for (c in 0 until chCount) {
                    samples[base + c] = frameWork[c]
                }
            }
        }
    }

    fun resetState() {
        for (f in filters) f.reset()
        crossLpL = 0.0
        crossLpR = 0.0
        crossDelayBufL.fill(0.0)
        crossDelayBufR.fill(0.0)
        crossDelayPos = 0
        for (buf in limiterDelayBuf) buf.fill(0.0)
        limiterDelayPos = 0
        limiterEnvelope = 1.0
    }

    private fun rebuildStages() {
        filters =
            config.bands
                .asSequence()
                .filter {
                    it.enabled &&
                        (abs(it.gainDb) > 1e-4 ||
                            it.type == BiquadType.LOW_PASS ||
                            it.type == BiquadType.HIGH_PASS)
                }
                .take(64)
                .map { BiquadFilter64(it, sampleRateHz, channels) }
                .toList()

        val cutoff = config.crossfeedCutoffHz.coerceIn(450.0, 950.0)
        crossAlphaLp = (1.0 - exp(-2.0 * PI * cutoff / sampleRateHz)).coerceIn(0.001, 0.999)
        val feedDb = config.crossfeedFeedDb.coerceIn(1.5, 9.5)
        val rawFeed = 10.0.pow(-feedDb / 20.0)
        val norm = 1.0 / sqrt(1.0 + rawFeed * rawFeed)
        crossDirectGain = norm
        crossFeedGain = rawFeed * norm
        crossDelaySamples =
            ((config.crossfeedDelayMs.coerceIn(0.15, 0.45) / 1000.0) * sampleRateHz)
                .roundToInt()
                .coerceIn(1, 128)
        if (crossDelayBufL.size < crossDelaySamples) {
            crossDelayBufL = DoubleArray(crossDelaySamples)
            crossDelayBufR = DoubleArray(crossDelaySamples)
        }
        crossDelayPos = 0

        limiterDelaySamples = ((0.003) * sampleRateHz).roundToInt().coerceIn(8, 1024)
        if (limiterDelayBuf.size != channels || limiterDelayBuf[0].size < limiterDelaySamples) {
            limiterDelayBuf = Array(channels) { DoubleArray(limiterDelaySamples) }
        }
        limiterDelayPos = 0
        limiterReleaseCoeff = exp(-1.0 / (0.08 * sampleRateHz))
        limiterCeiling = 10.0.pow(config.limiterCeilingDb.coerceIn(-3.0, -0.05) / 20.0)
    }

    companion object {
        fun computeEffectivePreampDb(config: DspChainConfig, replayGainDb: Double): Double {
            val maxBoost =
                config.bands
                    .asSequence()
                    .filter {
                        it.enabled &&
                            it.type != BiquadType.LOW_PASS &&
                            it.type != BiquadType.HIGH_PASS
                    }
                    .maxOfOrNull { max(0.0, it.gainDb) } ?: 0.0
            val rgEnvelope = replayGainDb - maxBoost
            return min(rgEnvelope, replayGainDb + config.preampDb)
        }

        fun estimateRmsLoudnessShiftDb(config: DspChainConfig, sampleRateHz: Int = 48000): Double {
            val activeFilters =
                config.bands
                    .filter { it.enabled && abs(it.gainDb) > 1e-4 }
                    .map { BiquadFilter64(it, sampleRateHz, 1) }
            if (activeFilters.isEmpty()) return config.preampDb.coerceIn(-15.0, 6.0)
            // Log-spaced 24-point perceptual power average from 40 Hz to 14 kHz
            val steps = 24
            val fMin = ln(40.0)
            val fMax = ln(min(14000.0, sampleRateHz * 0.45))
            var powerSum = 0.0
            for (i in 0 until steps) {
                val freq = exp(fMin + (fMax - fMin) * i / (steps - 1))
                var mag = 1.0
                for (f in activeFilters) {
                    mag *= f.magnitudeAtHz(freq, sampleRateHz)
                }
                powerSum += mag * mag
            }
            val rmsGainDb = 10.0 * log10(max(powerSum / steps, 1e-6))
            val preAmpShift = computeEffectivePreampDb(config, 0.0)
            return (preAmpShift + rmsGainDb).coerceIn(-15.0, 6.0)
        }

        fun parseEqualizerApo(text: String): DspChainConfig {
            var preampDb = 0.0
            val parsedBands = mutableListOf<BiquadBand>()
            for (rawLine in text.lines()) {
                val line = rawLine.trim().trimStart('\uFEFF')
                if (line.isEmpty() || line.startsWith('#')) continue
                if (line.startsWith("Preamp:", ignoreCase = true)) {
                    val value =
                        line.substringAfter(':')
                            .replace("dB", "", ignoreCase = true)
                            .trim()
                            .toDoubleOrNull()
                    if (value != null && value.isFinite()) {
                        preampDb = value.coerceIn(-20.0, 12.0)
                    }
                } else if (line.startsWith("Filter:", ignoreCase = true)) {
                    val tokens = line.substringAfter(':').trim().split(Regex("\\s+"))
                    if (tokens.size < 4 || !tokens[0].equals("ON", ignoreCase = true)) continue
                    val kind =
                        when (tokens[1].uppercase()) {
                            "PK" -> BiquadType.PEAKING
                            "LSC",
                            "LS" -> BiquadType.LOW_SHELF
                            "HSC",
                            "HS" -> BiquadType.HIGH_SHELF
                            "LP",
                            "LPQ" -> BiquadType.LOW_PASS
                            "HP",
                            "HPQ" -> BiquadType.HIGH_PASS
                            else -> null
                        } ?: continue
                    var fc: Double? = null
                    var gain = 0.0
                    var q = 0.707
                    var i = 2
                    while (i + 1 < tokens.size) {
                        when (tokens[i].uppercase()) {
                            "FC" -> {
                                fc = tokens[i + 1].toDoubleOrNull()
                                i += 2
                            }
                            "GAIN" -> {
                                gain = tokens[i + 1].toDoubleOrNull() ?: gain
                                i += 2
                            }
                            "Q" -> {
                                q = tokens[i + 1].toDoubleOrNull() ?: q
                                i += 2
                            }
                            else -> i++
                        }
                    }
                    if (fc != null && fc > 0.0) {
                        parsedBands.add(
                            BiquadBand(
                                type = kind,
                                freqHz = fc.coerceIn(20.0, 20000.0),
                                gainDb = gain.coerceIn(-12.0, 12.0),
                                q = q.coerceIn(0.1, 20.0),
                                enabled = true,
                            )
                        )
                    }
                }
            }
            return DspChainConfig(
                presetId = DspPresetId.CUSTOM,
                enabled = true,
                preampDb = preampDb,
                bands = parsedBands.ifEmpty { DspChainConfig.defaultTenBands() },
                limiterEnabled = true,
            )
        }
    }
}
