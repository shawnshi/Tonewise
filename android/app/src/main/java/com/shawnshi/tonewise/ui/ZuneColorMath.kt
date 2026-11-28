/*
 * Copyright (c) 2026 Tonewise Project
 * ZuneColorMath.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.ui

import kotlin.math.abs
import kotlin.math.max
import kotlin.math.min
import kotlin.math.pow

/**
 * Pure Kotlin WCAG 2.1 contrast math and dynamic cover accent clamping for Zune/Metro surfaces.
 * Intentionally free of android.graphics.Color calls so JVM unit tests execute directly.
 */
object ZuneColorMath {
    const val DEFAULT_DARK_MAGENTA: Int = 0xFFE5006B.toInt()
    const val DEFAULT_DARK_ACCENT_INK: Int = 0xFFFF5C9E.toInt()
    const val DEFAULT_LIGHT_MAGENTA: Int = 0xFFC20052.toInt()
    const val BLACK_SURFACE: Int = 0xFF000000.toInt()
    const val ELEVATED_DARK_SURFACE: Int = 0xFF1C1C1C.toInt()
    const val WHITE_SURFACE: Int = 0xFFFFFFFF.toInt()

    fun red(argb: Int): Int = (argb ushr 16) and 0xFF

    fun green(argb: Int): Int = (argb ushr 8) and 0xFF

    fun blue(argb: Int): Int = argb and 0xFF

    fun packRgb(r: Int, g: Int, b: Int, a: Int = 0xFF): Int =
        ((a and 0xFF) shl 24) or ((r.coerceIn(0, 255)) shl 16) or ((g.coerceIn(0, 255)) shl 8) or (b.coerceIn(0, 255))

    /** Computes WCAG 2.1 relative luminance in [0.0, 1.0]. */
    fun relativeLuminance(argb: Int): Double {
        fun linearize(channel: Int): Double {
            val s = channel / 255.0
            return if (s <= 0.03928) s / 12.92 else ((s + 0.055) / 1.055).pow(2.4)
        }
        val r = linearize(red(argb))
        val g = linearize(green(argb))
        val b = linearize(blue(argb))
        return 0.2126 * r + 0.7152 * g + 0.0722 * b
    }

    /** Computes WCAG 2.1 contrast ratio in [1.0, 21.0]. */
    fun contrastRatio(argb1: Int, argb2: Int): Double {
        val l1 = relativeLuminance(argb1)
        val l2 = relativeLuminance(argb2)
        val lighter = max(l1, l2)
        val darker = min(l1, l2)
        return (lighter + 0.05) / (darker + 0.05)
    }

    /** Picks ideal foreground (#FFFFFF or #000000) against [backgroundArgb]. */
    fun contrastingOnColor(backgroundArgb: Int): Int {
        val onWhite = contrastRatio(WHITE_SURFACE, backgroundArgb)
        val onBlack = contrastRatio(BLACK_SURFACE, backgroundArgb)
        return if (onWhite >= onBlack) WHITE_SURFACE else BLACK_SURFACE
    }

    /**
     * Converts [argb] into [h (0..360), s (0..1), l (0..1)].
     */
    fun rgbToHsl(argb: Int): FloatArray {
        val r = red(argb) / 255f
        val g = green(argb) / 255f
        val b = blue(argb) / 255f

        val maxC = max(r, max(g, b))
        val minC = min(r, min(g, b))
        val delta = maxC - minC
        val l = (maxC + minC) / 2f

        val s =
            if (delta == 0f) {
                0f
            } else {
                delta / (1f - abs(2f * l - 1f)).coerceAtLeast(1e-5f)
            }

        val h =
            when {
                delta == 0f -> 0f
                maxC == r -> 60f * (((g - b) / delta) % 6f)
                maxC == g -> 60f * (((b - r) / delta) + 2f)
                else -> 60f * (((r - g) / delta) + 4f)
            }
        val normalizedH = if (h < 0f) h + 360f else h
        return floatArrayOf(normalizedH, s.coerceIn(0f, 1f), l.coerceIn(0f, 1f))
    }

    /**
     * Converts [h (0..360), s (0..1), l (0..1)] back to opaque ARGB int.
     */
    fun hslToRgb(h: Float, s: Float, l: Float): Int {
        val c = (1f - abs(2f * l - 1f)) * s
        val hh = ((h % 360f) + 360f) % 360f / 60f
        val x = c * (1f - abs((hh % 2f) - 1f))
        val (r1, g1, b1) =
            when {
                hh < 1f -> Triple(c, x, 0f)
                hh < 2f -> Triple(x, c, 0f)
                hh < 3f -> Triple(0f, c, x)
                hh < 4f -> Triple(0f, x, c)
                hh < 5f -> Triple(x, 0f, c)
                else -> Triple(c, 0f, x)
            }
        val m = l - c / 2f
        val r = ((r1 + m) * 255f).toInt().coerceIn(0, 255)
        val g = ((g1 + m) * 255f).toInt().coerceIn(0, 255)
        val b = ((b1 + m) * 255f).toInt().coerceIn(0, 255)
        return packRgb(r, g, b)
    }

    /**
     * Clamps [seedArgb] lightness while preserving hue so that contrast against pure black
     * (in dark mode) or pure white (in light mode) reaches at least [minContrast].
     * Near-neutral grays fall back to Zune Magenta.
     */
    fun clampAccentForContrast(
        seedArgb: Int,
        isDarkSurface: Boolean,
        minContrast: Double = 4.5,
    ): Int {
        val surface = if (isDarkSurface) BLACK_SURFACE else WHITE_SURFACE
        val fallback = if (isDarkSurface) DEFAULT_DARK_MAGENTA else DEFAULT_LIGHT_MAGENTA
        return clampAccentAgainstSurface(seedArgb, surface, fallback, isDarkSurface, minContrast)
    }

    /**
     * Clamps [seedArgb] for readable accent text/icon ink against elevated dark surfaces
     * (#1C1C1C) or light surfaces (#FFFFFF) at >= 4.55:1 contrast.
     */
    fun clampAccentInkForTheme(
        seedArgb: Int,
        isDarkSurface: Boolean,
        minContrast: Double = 4.55,
    ): Int {
        val surface = if (isDarkSurface) ELEVATED_DARK_SURFACE else WHITE_SURFACE
        val fallback = if (isDarkSurface) DEFAULT_DARK_ACCENT_INK else DEFAULT_LIGHT_MAGENTA
        return clampAccentAgainstSurface(seedArgb, surface, fallback, isDarkSurface, minContrast)
    }

    private fun clampAccentAgainstSurface(
        seedArgb: Int,
        surface: Int,
        fallback: Int,
        isDarkSurface: Boolean,
        minContrast: Double,
    ): Int {
        val hsl = rgbToHsl(seedArgb)
        if (hsl[1] < 0.14f) {
            return fallback
        }

        val hue = hsl[0]
        val sat = hsl[1].coerceIn(0.45f, 0.95f)
        var candidateL = hsl[2].coerceIn(0.20f, 0.85f)
        var candidate = hslToRgb(hue, sat, candidateL)

        if (contrastRatio(candidate, surface) >= minContrast) {
            return candidate
        }

        val step = if (isDarkSurface) 0.025f else -0.025f
        for (i in 0 until 36) {
            candidateL = (candidateL + step).coerceIn(0.08f, 0.94f)
            candidate = hslToRgb(hue, sat, candidateL)
            if (contrastRatio(candidate, surface) >= minContrast) {
                return candidate
            }
            if ((isDarkSurface && candidateL >= 0.94f) || (!isDarkSurface && candidateL <= 0.08f)) {
                break
            }
        }
        return if (contrastRatio(candidate, surface) >= minContrast) candidate else fallback
    }

    /**
     * Selects the most vibrant chromatic pixel from a downsampled cover pixel array, ignoring
     * near-black, near-white, and desaturated pixels.
     */
    fun selectVibrantSeed(argbSamples: IntArray): Int? {
        var bestArgb: Int? = null
        var bestScore = -1f
        for (argb in argbSamples) {
            val alpha = (argb ushr 24) and 0xFF
            if (alpha < 128) continue
            val hsl = rgbToHsl(argb)
            val s = hsl[1]
            val l = hsl[2]
            if (s < 0.22f || l < 0.12f || l > 0.88f) continue
            // Prefer vibrant chromatic midtones
            val midtoneBias = 1f - abs(l - 0.52f)
            val score = s * 1.8f + midtoneBias * 0.6f
            if (score > bestScore) {
                bestScore = score
                bestArgb = argb or 0xFF000000.toInt()
            }
        }
        return bestArgb
    }
}
