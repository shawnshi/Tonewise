/*
 * Copyright (c) 2026 Tonewise Project
 * VinylStyle.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.playback.ui

import com.shawnshi.tonewise.ui.ZuneColorMath

/**
 * Available platter surface finishes for [VinylTurntableView].
 * Persisted via integer [intCode] in [com.shawnshi.tonewise.ui.UISettings].
 */
enum class VinylStyle(val intCode: Int) {
    CLASSIC_BLACK(0),
    COVER_PEARL(1),
    GROOVE_DIFFRACTION(2);

    /** Cycles cleanly through all finishes in order. */
    fun next(): VinylStyle = entries[(ordinal + 1) % entries.size]

    companion object {
        val DEFAULT: VinylStyle = CLASSIC_BLACK

        fun fromIntCode(code: Int): VinylStyle =
            entries.firstOrNull { it.intCode == code } ?: DEFAULT
    }
}

/**
 * Precomputed ARGB stops and accents for a cover-derived pearlescent vinyl disc.
 * Pure Kotlin container so color bounds can be tested on the JVM without Android Color APIs.
 */
data class PearlVinylPalette(
    val bodyStops: IntArray,
    val swirlWarmArgb: Int,
    val swirlCoolArgb: Int,
    val grooveDarkArgb: Int,
    val grooveLightArgb: Int,
    val outerRimArgb: Int,
    val dropShadowArgb: Int,
) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is PearlVinylPalette) return false
        return bodyStops.contentEquals(other.bodyStops) &&
            swirlWarmArgb == other.swirlWarmArgb &&
            swirlCoolArgb == other.swirlCoolArgb &&
            grooveDarkArgb == other.grooveDarkArgb &&
            grooveLightArgb == other.grooveLightArgb &&
            outerRimArgb == other.outerRimArgb &&
            dropShadowArgb == other.dropShadowArgb
    }

    override fun hashCode(): Int {
        var result = bodyStops.contentHashCode()
        result = 31 * result + swirlWarmArgb
        result = 31 * result + swirlCoolArgb
        result = 31 * result + grooveDarkArgb
        result = 31 * result + grooveLightArgb
        result = 31 * result + outerRimArgb
        result = 31 * result + dropShadowArgb
        return result
    }
}

/**
 * Pure-Kotlin material shader math for [VinylStyle.COVER_PEARL] and [VinylStyle.GROOVE_DIFFRACTION].
 */
object VinylMaterialMath {
    private val DIFFRACTION_POSITIONS =
        floatArrayOf(
            0f,
            0.036f,
            0.072f,
            0.088f,
            0.460f,
            0.496f,
            0.532f,
            0.568f,
            0.584f,
            0.960f,
            1f,
        )

    private val DIFFRACTION_STOPS =
        intArrayOf(
            ZuneColorMath.packRgb(138, 198, 255, a = 64),
            ZuneColorMath.packRgb(186, 152, 245, a = 78),
            ZuneColorMath.packRgb(242, 168, 198, a = 62),
            0x00000000,
            0x00000000,
            ZuneColorMath.packRgb(126, 224, 212, a = 58),
            ZuneColorMath.packRgb(152, 186, 255, a = 76),
            ZuneColorMath.packRgb(238, 174, 156, a = 60),
            0x00000000,
            0x00000000,
            ZuneColorMath.packRgb(138, 198, 255, a = 64),
        )

    /**
     * Derives a low-saturation pearlescent nacre palette from [coverSeedArgb], falling back to a
     * warm champagne-ivory pearl when the seed is null or near-achromatic, and guaranteeing crisp
     * bevel separation on both light paper and dark OLED surfaces.
     */
    fun derivePearlPalette(coverSeedArgb: Int?, isDarkSurface: Boolean): PearlVinylPalette {
        val rawHsl = coverSeedArgb?.let { ZuneColorMath.rgbToHsl(it) }
        val isChromatic = rawHsl != null && rawHsl[1] >= 0.10f
        val hue = if (isChromatic) rawHsl!![0] else 36f
        val sat =
            if (isChromatic) {
                (rawHsl!![1] * 0.48f).coerceIn(0.18f, 0.38f)
            } else {
                0.22f
            }

        val innerL = if (isDarkSurface) 0.77f else 0.87f
        val bodyL = if (isDarkSurface) 0.68f else 0.78f
        val outerL = if (isDarkSurface) 0.59f else 0.70f
        val rimBaseL = if (isDarkSurface) 0.44f else 0.56f

        val innerArgb = ZuneColorMath.hslToRgb(hue, sat * 0.88f, innerL)
        val midArgb = ZuneColorMath.hslToRgb((hue + 10f) % 360f, sat, bodyL)
        val outerArgb = ZuneColorMath.hslToRgb((hue + 346f) % 360f, (sat * 1.04f).coerceAtMost(0.40f), outerL)
        val rimArgb = ZuneColorMath.hslToRgb(hue, (sat * 0.92f).coerceAtMost(0.38f), rimBaseL)

        val warmAlpha = if (isDarkSurface) 48 else 52
        val coolAlpha = if (isDarkSurface) 44 else 48
        val warmBase = ZuneColorMath.hslToRgb((hue + 28f) % 360f, (sat * 1.1f).coerceAtMost(0.40f), if (isDarkSurface) 0.78f else 0.84f)
        val coolBase = ZuneColorMath.hslToRgb((hue + 328f) % 360f, (sat * 1.05f).coerceAtMost(0.40f), if (isDarkSurface) 0.75f else 0.82f)
        val swirlWarmArgb = (warmBase and 0x00FFFFFF) or (warmAlpha shl 24)
        val swirlCoolArgb = (coolBase and 0x00FFFFFF) or (coolAlpha shl 24)

        val grooveDarkBase = ZuneColorMath.hslToRgb(hue, (sat * 0.55f).coerceIn(0.08f, 0.24f), 0.28f)
        val grooveDarkAlpha = if (isDarkSurface) 40 else 34
        val grooveDarkArgb = (grooveDarkBase and 0x00FFFFFF) or (grooveDarkAlpha shl 24)

        val grooveLightAlpha = if (isDarkSurface) 42 else 54
        val grooveLightArgb = ZuneColorMath.packRgb(255, 255, 255, a = grooveLightAlpha)

        val outerRimArgb =
            if (isDarkSurface) {
                ZuneColorMath.packRgb(248, 245, 240, a = 95)
            } else {
                val edgeBase = ZuneColorMath.hslToRgb(hue, (sat * 0.55f).coerceIn(0.10f, 0.24f), 0.34f)
                (edgeBase and 0x00FFFFFF) or (115 shl 24)
            }

        val dropShadowArgb =
            ZuneColorMath.packRgb(0, 0, 0, a = if (isDarkSurface) 64 else 42)

        return PearlVinylPalette(
            bodyStops = intArrayOf(innerArgb, midArgb, outerArgb, rimArgb),
            swirlWarmArgb = swirlWarmArgb,
            swirlCoolArgb = swirlCoolArgb,
            grooveDarkArgb = grooveDarkArgb,
            grooveLightArgb = grooveLightArgb,
            outerRimArgb = outerRimArgb,
            dropShadowArgb = dropShadowArgb,
        )
    }

    /** Dual narrow radial spectral dispersion stops for [VinylStyle.GROOVE_DIFFRACTION]. */
    fun diffractionSpectrumStops(): IntArray = DIFFRACTION_STOPS.copyOf()

    /** Normalized angular stop positions matching [diffractionSpectrumStops]. */
    fun diffractionSpectrumPositions(): FloatArray = DIFFRACTION_POSITIONS.copyOf()
}
