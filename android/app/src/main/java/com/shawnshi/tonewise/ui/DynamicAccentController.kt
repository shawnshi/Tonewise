/*
 * Copyright (c) 2026 Tonewise Project
 * DynamicAccentController.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.ui

import javax.inject.Inject
import javax.inject.Singleton
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/**
 * Holds the optional WCAG-AA clamped dynamic cover accent tint when
 * [UISettings.enableDynamicCoverAccent] is active.
 */
@Singleton
class DynamicAccentController @Inject constructor() {
    private val _activeAccentArgb = MutableStateFlow<Int?>(null)
    val activeAccentArgb: StateFlow<Int?> = _activeAccentArgb.asStateFlow()

    fun updateFromPixels(argbSamples: IntArray, isDarkSurface: Boolean) {
        val seed = ZuneColorMath.selectVibrantSeed(argbSamples)
        _activeAccentArgb.value =
            seed?.let { ZuneColorMath.clampAccentForContrast(it, isDarkSurface) }
    }

    fun clear() {
        _activeAccentArgb.value = null
    }
}
