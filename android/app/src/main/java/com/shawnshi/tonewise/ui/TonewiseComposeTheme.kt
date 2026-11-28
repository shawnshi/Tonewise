/*
 * Copyright (c) 2026 Tonewise Project
 * TonewiseComposeTheme.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.ui

import android.content.Context
import androidx.annotation.AttrRes
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.ColorScheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Shapes
import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.remember
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.Font
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.em
import androidx.compose.ui.unit.sp
import androidx.core.graphics.ColorUtils
import androidx.appcompat.R as AppCompatR
import com.google.android.material.R as MaterialR
import com.google.android.material.color.MaterialColors
import com.shawnshi.tonewise.R

val LocalTonewiseAccentInk =
    staticCompositionLocalOf { Color(ZuneColorMath.DEFAULT_DARK_ACCENT_INK) }

private val TonewiseInterFontFamily =
    FontFamily(
        Font(R.font.inter_regular, FontWeight.Light),
        Font(R.font.inter_regular, FontWeight.Normal),
        Font(R.font.inter_semibold, FontWeight.Medium),
        Font(R.font.inter_semibold, FontWeight.SemiBold),
        Font(R.font.inter_semibold, FontWeight.Bold),
    )

private val BaseMaterialTypography = Typography()

private val TonewiseComposeTypography =
    Typography(
        displayLarge =
            BaseMaterialTypography.displayLarge.copy(
                fontFamily = TonewiseInterFontFamily,
                fontSize = 46.sp,
                lineHeight = 50.sp,
                fontWeight = FontWeight.Light,
                letterSpacing = (-1.5).sp,
            ),
        displayMedium =
            BaseMaterialTypography.displayMedium.copy(
                fontFamily = TonewiseInterFontFamily,
                fontSize = 36.sp,
                lineHeight = 40.sp,
                fontWeight = FontWeight.Light,
                letterSpacing = (-1.0).sp,
            ),
        displaySmall =
            BaseMaterialTypography.displaySmall.copy(fontFamily = TonewiseInterFontFamily),
        headlineLarge =
            BaseMaterialTypography.headlineLarge.copy(
                fontFamily = TonewiseInterFontFamily,
                fontSize = 30.sp,
                lineHeight = 34.sp,
                fontWeight = FontWeight.Light,
                letterSpacing = (-0.5).sp,
            ),
        headlineMedium =
            BaseMaterialTypography.headlineMedium.copy(
                fontFamily = TonewiseInterFontFamily,
                fontSize = 24.sp,
                lineHeight = 28.sp,
                fontWeight = FontWeight.Light,
            ),
        headlineSmall =
            BaseMaterialTypography.headlineSmall.copy(fontFamily = TonewiseInterFontFamily),
        titleLarge =
            BaseMaterialTypography.titleLarge.copy(
                fontFamily = TonewiseInterFontFamily,
                fontWeight = FontWeight.Normal,
                letterSpacing = (-0.01818181818).em,
            ),
        titleMedium =
            BaseMaterialTypography.titleMedium.copy(
                fontFamily = TonewiseInterFontFamily,
                fontWeight = FontWeight.Normal,
                letterSpacing = (-0.01125).em,
            ),
        titleSmall =
            BaseMaterialTypography.titleSmall.copy(
                fontFamily = TonewiseInterFontFamily,
                fontWeight = FontWeight.Normal,
                letterSpacing = 0.em,
            ),
        bodyLarge =
            BaseMaterialTypography.bodyLarge.copy(
                fontFamily = TonewiseInterFontFamily,
                fontWeight = FontWeight.Normal,
                letterSpacing = (-0.01125).em,
            ),
        bodyMedium =
            BaseMaterialTypography.bodyMedium.copy(
                fontFamily = TonewiseInterFontFamily,
                fontWeight = FontWeight.Normal,
                letterSpacing = (-0.0064285714).em,
            ),
        bodySmall =
            BaseMaterialTypography.bodySmall.copy(
                fontFamily = TonewiseInterFontFamily,
                fontWeight = FontWeight.Normal,
                letterSpacing = 0.0008333333.em,
            ),
        labelLarge =
            BaseMaterialTypography.labelLarge.copy(
                fontFamily = TonewiseInterFontFamily,
                fontWeight = FontWeight.SemiBold,
                letterSpacing = (-0.003).em,
            ),
        labelMedium =
            BaseMaterialTypography.labelMedium.copy(
                fontFamily = TonewiseInterFontFamily,
                fontWeight = FontWeight.SemiBold,
                letterSpacing = 0.003.em,
            ),
        labelSmall =
            BaseMaterialTypography.labelSmall.copy(
                fontFamily = TonewiseInterFontFamily,
                fontWeight = FontWeight.SemiBold,
                letterSpacing = 0.0075454545.em,
            ),
    )

private val FallbackLightColorScheme =
    lightColorScheme(
        primary = Color(0xFFC20052),
        onPrimary = Color(0xFFFFFFFF),
        primaryContainer = Color(0xFFFFD9E3),
        onPrimaryContainer = Color(0xFF3E001A),
        secondary = Color(0xFF444444),
        onSecondary = Color(0xFFFFFFFF),
        secondaryContainer = Color(0xFFEAEAEA),
        onSecondaryContainer = Color(0xFF111111),
        background = Color(0xFFFFFFFF),
        onBackground = Color(0xFF0A0A0A),
        surface = Color(0xFFFFFFFF),
        onSurface = Color(0xFF0A0A0A),
        surfaceVariant = Color(0xFFF2F2F2),
        onSurfaceVariant = Color(0xFF525252),
        surfaceContainerLowest = Color(0xFFFFFFFF),
        surfaceContainerLow = Color(0xFFFAFAFA),
        surfaceContainer = Color(0xFFF4F4F4),
        surfaceContainerHigh = Color(0xFFEEEEEE),
        outline = Color(0xFFD4D4D4),
        outlineVariant = Color(0xFFE5E5E5),
    )

private val FallbackDarkColorScheme =
    darkColorScheme(
        primary = Color(0xFFE5006B),
        onPrimary = Color(0xFFFFFFFF),
        primaryContainer = Color(0xFF4A0023),
        onPrimaryContainer = Color(0xFFFFD9E3),
        secondary = Color(0xFFD0D0D0),
        onSecondary = Color(0xFF111111),
        secondaryContainer = Color(0xFF1F1F1F),
        onSecondaryContainer = Color(0xFFFFFFFF),
        background = Color(0xFF000000),
        onBackground = Color(0xFFFFFFFF),
        surface = Color(0xFF000000),
        onSurface = Color(0xFFFFFFFF),
        surfaceVariant = Color(0xFF242424),
        onSurfaceVariant = Color(0xFFA6A6A6),
        surfaceContainerLowest = Color(0xFF050505),
        surfaceContainerLow = Color(0xFF101010),
        surfaceContainer = Color(0xFF141414),
        surfaceContainerHigh = Color(0xFF1C1C1C),
        outline = Color(0xFF737373),
        outlineVariant = Color(0xFF292929),
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

private val RectilinearZuneShapes =
    Shapes(
        extraSmall = RoundedCornerShape(0.dp),
        small = RoundedCornerShape(0.dp),
        medium = RoundedCornerShape(0.dp),
        large = RoundedCornerShape(0.dp),
        extraLarge = RoundedCornerShape(0.dp),
    )

private val RoundedTonewiseShapes =
    Shapes(
        extraSmall = RoundedCornerShape(2.dp),
        small = RoundedCornerShape(6.dp),
        medium = RoundedCornerShape(10.dp),
        large = RoundedCornerShape(14.dp),
        extraLarge = RoundedCornerShape(20.dp),
    )

/**
 * Derives Material3 Compose colors directly from the active host Material3 XML theme attributes
 * so light, dark, AMOLED black, Zune 0dp geometry, and optional dynamic cover accent match
 * consistently across Compose and View hierarchies.
 */
@Composable
fun TonewiseComposeTheme(
    roundMode: Boolean = false,
    dynamicAccentArgb: Int? = null,
    content: @Composable () -> Unit,
) {
    val context = LocalContext.current
    val configuration = LocalConfiguration.current
    val systemDark = isSystemInDarkTheme()
    val scheme =
        remember(context, configuration, systemDark, dynamicAccentArgb) {
            val baseScheme = resolveColorSchemeFromTheme(context, systemDark)
            if (dynamicAccentArgb != null) {
                val isDark = ColorUtils.calculateLuminance(baseScheme.surface.toArgb()) < 0.5
                val clamped = ZuneColorMath.clampAccentForContrast(dynamicAccentArgb, isDark)
                val onClamped = ZuneColorMath.contrastingOnColor(clamped)
                baseScheme.copy(
                    primary = Color(clamped),
                    onPrimary = Color(onClamped),
                )
            } else {
                baseScheme
            }
        }
    val isDark = ColorUtils.calculateLuminance(scheme.surface.toArgb()) < 0.5
    val accentInk =
        remember(scheme.primary, dynamicAccentArgb, isDark) {
            val seedArgb = dynamicAccentArgb ?: scheme.primary.toArgb()
            Color(ZuneColorMath.clampAccentInkForTheme(seedArgb, isDark))
        }
    val shapes = if (roundMode) RoundedTonewiseShapes else RectilinearZuneShapes
    CompositionLocalProvider(LocalTonewiseAccentInk provides accentInk) {
        MaterialTheme(
            colorScheme = scheme,
            typography = TonewiseComposeTypography,
            shapes = shapes,
            content = content,
        )
    }
}
