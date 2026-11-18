/*
 * Copyright (c) 2026 Tonewise Project
 * TonewiseComposeTheme.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.ui

import android.content.Context
import androidx.annotation.AttrRes
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.ColorScheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalContext
import androidx.core.graphics.ColorUtils
import androidx.appcompat.R as AppCompatR
import com.google.android.material.R as MaterialR
import com.google.android.material.color.MaterialColors

private val FallbackLightColorScheme =
    lightColorScheme(
        primary = Color(0xFF1F4E79),
        onPrimary = Color(0xFFFFFFFF),
        primaryContainer = Color(0xFFD9E6F4),
        onPrimaryContainer = Color(0xFF102B46),
        secondary = Color(0xFF8C6A2B),
        onSecondary = Color(0xFFFFFFFF),
        secondaryContainer = Color(0xFFF3EAD8),
        onSecondaryContainer = Color(0xFF3E2D0D),
        background = Color(0xFFF6F4EF),
        onBackground = Color(0xFF1C2430),
        surface = Color(0xFFF6F4EF),
        onSurface = Color(0xFF1C2430),
        surfaceVariant = Color(0xFFEAE7DF),
        onSurfaceVariant = Color(0xFF5A6577),
        surfaceContainerLowest = Color(0xFFFFFFFF),
        surfaceContainerLow = Color(0xFFFFFFFF),
        surfaceContainer = Color(0xFFF0EDE6),
        surfaceContainerHigh = Color(0xFFEAE6DD),
        outline = Color(0xFFD4D8DF),
        outlineVariant = Color(0xFFE3E6EC),
    )

private val FallbackDarkColorScheme =
    darkColorScheme(
        primary = Color(0xFFD9B372),
        onPrimary = Color(0xFF131720),
        primaryContainer = Color(0xFF332A1A),
        onPrimaryContainer = Color(0xFFF4DCAD),
        secondary = Color(0xFF6BA5E7),
        onSecondary = Color(0xFF101824),
        secondaryContainer = Color(0xFF1B2B40),
        onSecondaryContainer = Color(0xFFD2E4FA),
        background = Color(0xFF131720),
        onBackground = Color(0xFFE8ECF4),
        surface = Color(0xFF131720),
        onSurface = Color(0xFFE8ECF4),
        surfaceVariant = Color(0xFF222938),
        onSurfaceVariant = Color(0xFF98A4B8),
        surfaceContainerLowest = Color(0xFF0F131B),
        surfaceContainerLow = Color(0xFF1A202C),
        surfaceContainer = Color(0xFF1E2534),
        surfaceContainerHigh = Color(0xFF252D3E),
        outline = Color(0xFF2E3749),
        outlineVariant = Color(0xFF232A39),
    )

private fun resolveAttrColor(
    context: Context,
    @AttrRes attrRes: Int,
    fallback: Color,
): Color = Color(MaterialColors.getColor(context, attrRes, fallback.toArgb()))

private fun resolveColorSchemeFromTheme(
    context: Context,
    systemDarkFallback: Boolean,
): ColorScheme {
    val defaultSurface =
        if (systemDarkFallback) FallbackDarkColorScheme.surface else FallbackLightColorScheme.surface
    val surfaceColor = resolveAttrColor(context, MaterialR.attr.colorSurface, defaultSurface)
    val isDark = ColorUtils.calculateLuminance(surfaceColor.toArgb()) < 0.5
    val base = if (isDark) FallbackDarkColorScheme else FallbackLightColorScheme

    val primary = resolveAttrColor(context, AppCompatR.attr.colorPrimary, base.primary)
    val onPrimary = resolveAttrColor(context, MaterialR.attr.colorOnPrimary, base.onPrimary)
    val primaryContainer =
        resolveAttrColor(context, MaterialR.attr.colorPrimaryContainer, base.primaryContainer)
    val onPrimaryContainer =
        resolveAttrColor(context, MaterialR.attr.colorOnPrimaryContainer, base.onPrimaryContainer)
    val secondary = resolveAttrColor(context, MaterialR.attr.colorSecondary, base.secondary)
    val onSecondary = resolveAttrColor(context, MaterialR.attr.colorOnSecondary, base.onSecondary)
    val secondaryContainer =
        resolveAttrColor(context, MaterialR.attr.colorSecondaryContainer, base.secondaryContainer)
    val onSecondaryContainer =
        resolveAttrColor(
            context,
            MaterialR.attr.colorOnSecondaryContainer,
            base.onSecondaryContainer,
        )
    val background = resolveAttrColor(context, android.R.attr.colorBackground, surfaceColor)
    val onBackground = resolveAttrColor(context, MaterialR.attr.colorOnBackground, base.onBackground)
    val onSurface = resolveAttrColor(context, MaterialR.attr.colorOnSurface, base.onSurface)
    val surfaceVariant =
        resolveAttrColor(context, MaterialR.attr.colorSurfaceVariant, base.surfaceVariant)
    val onSurfaceVariant =
        resolveAttrColor(context, MaterialR.attr.colorOnSurfaceVariant, base.onSurfaceVariant)
    val surfaceContainerLowest =
        resolveAttrColor(
            context,
            MaterialR.attr.colorSurfaceContainerLowest,
            base.surfaceContainerLowest,
        )
    val surfaceContainerLow =
        resolveAttrColor(context, MaterialR.attr.colorSurfaceContainerLow, base.surfaceContainerLow)
    val surfaceContainer =
        resolveAttrColor(context, MaterialR.attr.colorSurfaceContainer, base.surfaceContainer)
    val surfaceContainerHigh =
        resolveAttrColor(
            context,
            MaterialR.attr.colorSurfaceContainerHigh,
            base.surfaceContainerHigh,
        )
    val surfaceContainerHighest =
        resolveAttrColor(
            context,
            MaterialR.attr.colorSurfaceContainerHighest,
            base.surfaceContainerHigh,
        )
    val outline = resolveAttrColor(context, MaterialR.attr.colorOutline, base.outline)
    val outlineVariant =
        resolveAttrColor(context, MaterialR.attr.colorOutlineVariant, base.outlineVariant)

    return if (isDark) {
        darkColorScheme(
            primary = primary,
            onPrimary = onPrimary,
            primaryContainer = primaryContainer,
            onPrimaryContainer = onPrimaryContainer,
            secondary = secondary,
            onSecondary = onSecondary,
            secondaryContainer = secondaryContainer,
            onSecondaryContainer = onSecondaryContainer,
            background = background,
            onBackground = onBackground,
            surface = surfaceColor,
            onSurface = onSurface,
            surfaceVariant = surfaceVariant,
            onSurfaceVariant = onSurfaceVariant,
            surfaceContainerLowest = surfaceContainerLowest,
            surfaceContainerLow = surfaceContainerLow,
            surfaceContainer = surfaceContainer,
            surfaceContainerHigh = surfaceContainerHigh,
            surfaceContainerHighest = surfaceContainerHighest,
            outline = outline,
            outlineVariant = outlineVariant,
        )
    } else {
        lightColorScheme(
            primary = primary,
            onPrimary = onPrimary,
            primaryContainer = primaryContainer,
            onPrimaryContainer = onPrimaryContainer,
            secondary = secondary,
            onSecondary = onSecondary,
            secondaryContainer = secondaryContainer,
            onSecondaryContainer = onSecondaryContainer,
            background = background,
            onBackground = onBackground,
            surface = surfaceColor,
            onSurface = onSurface,
            surfaceVariant = surfaceVariant,
            onSurfaceVariant = onSurfaceVariant,
            surfaceContainerLowest = surfaceContainerLowest,
            surfaceContainerLow = surfaceContainerLow,
            surfaceContainer = surfaceContainer,
            surfaceContainerHigh = surfaceContainerHigh,
            surfaceContainerHighest = surfaceContainerHighest,
            outline = outline,
            outlineVariant = outlineVariant,
        )
    }
}

/**
 * Derives Material3 Compose colors directly from the active host Material3 XML theme attributes
 * so light, dark, AMOLED black, and accent choices match native views consistently.
 */
@Composable
fun TonewiseComposeTheme(content: @Composable () -> Unit) {
    val context = LocalContext.current
    val configuration = LocalConfiguration.current
    val systemDark = isSystemInDarkTheme()
    val scheme =
        remember(context, configuration, systemDark) {
            resolveColorSchemeFromTheme(context, systemDark)
        }
    MaterialTheme(colorScheme = scheme, content = content)
}
