/*
 * Copyright (c) 2026 Tonewise Project
 * DspRepository.kt is part of Tonewise.
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

import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import androidx.core.content.edit
import dagger.hilt.android.qualifiers.ApplicationContext
import javax.inject.Inject
import javax.inject.Singleton
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import org.json.JSONObject

@Singleton
class DspRepository @Inject constructor(@ApplicationContext private val context: Context) :
    DspConfigSource {
    private val prefs = context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)

    private val _activeDeviceFingerprint = MutableStateFlow("UNKNOWN:system output")
    val activeDeviceFingerprint: StateFlow<String> = _activeDeviceFingerprint

    private val _activeProfileLabel = MutableStateFlow("System Output")
    val activeProfileLabel: StateFlow<String> = _activeProfileLabel

    private val _config = MutableStateFlow(loadConfigForFingerprint(_activeDeviceFingerprint.value))
    override val config: StateFlow<DspChainConfig> = _config

    private val _romConflictHint = MutableStateFlow(detectRomAudioEffectConflict())
    val romConflictHint: StateFlow<String?> = _romConflictHint

    private val _usbBitPerfectConfirmed =
        MutableStateFlow(prefs.getBoolean(KEY_USB_VOLUME_CONFIRMED, false))
    val usbBitPerfectConfirmed: StateFlow<Boolean> = _usbBitPerfectConfirmed

    fun onDeviceRouteChanged(routeTypeName: String, deviceName: String) {
        val cleanName = deviceName.trim().ifEmpty { "System Output" }
        val fp = "$routeTypeName:${cleanName.lowercase()}"
        if (fp == _activeDeviceFingerprint.value) return
        _activeDeviceFingerprint.value = fp
        _activeProfileLabel.value = "$cleanName ($routeTypeName)"
        _config.value = loadConfigForFingerprint(fp)
    }

    fun updateConfig(transform: (DspChainConfig) -> DspChainConfig) {
        val updated = transform(_config.value)
        _config.value = updated
        saveConfigForFingerprint(_activeDeviceFingerprint.value, updated)
    }

    fun applyPreset(presetId: DspPresetId) {
        val currentExternal = _config.value.externalBridgeMode
        val next = DspChainConfig.preset(presetId).copy(externalBridgeMode = currentExternal)
        _config.value = next
        saveConfigForFingerprint(_activeDeviceFingerprint.value, next)
    }

    fun importEqualizerApo(text: String): Int {
        val parsed = TonewiseDspEngine.parseEqualizerApo(text)
        val updated =
            parsed.copy(
                externalBridgeMode = false,
                crossfeedEnabled = _config.value.crossfeedEnabled,
                crossfeedCutoffHz = _config.value.crossfeedCutoffHz,
                crossfeedFeedDb = _config.value.crossfeedFeedDb,
                balanceDb = _config.value.balanceDb,
                invertLeftPhase = _config.value.invertLeftPhase,
                invertRightPhase = _config.value.invertRightPhase,
                monoFold = _config.value.monoFold,
                limiterEnabled = _config.value.limiterEnabled,
            )
        _config.value = updated
        saveConfigForFingerprint(_activeDeviceFingerprint.value, updated)
        return updated.bands.size
    }

    fun confirmUsbBitPerfectVolume() {
        prefs.edit { putBoolean(KEY_USB_VOLUME_CONFIRMED, true) }
        _usbBitPerfectConfirmed.value = true
    }

    private fun loadConfigForFingerprint(fp: String): DspChainConfig {
        val raw = prefs.getString("$KEY_PROFILE_PREFIX$fp", null) ?: return DspChainConfig()
        return runCatching { DspChainConfig.fromJson(JSONObject(raw)) }
            .getOrDefault(DspChainConfig())
    }

    private fun saveConfigForFingerprint(fp: String, config: DspChainConfig) {
        prefs.edit { putString("$KEY_PROFILE_PREFIX$fp", config.toJson().toString()) }
    }

    private fun detectRomAudioEffectConflict(): String? {
        val brand = "${Build.MANUFACTURER} ${Build.BRAND}".lowercase()
        val hasDolby =
            isPackageInstalled("com.dolby.daxservice") ||
                isPackageInstalled("com.dolby.daxappui")
        val hasMiSound =
            brand.contains("xiaomi") ||
                brand.contains("redmi") ||
                brand.contains("poco") ||
                isPackageInstalled("com.miui.misound")
        return when {
            hasMiSound ->
                "HyperOS / MiSound / Dolby Atmos detected: disable system sound effects in OS Settings when using Internal 64-bit DSP to avoid double DRC/EQ coloration."
            hasDolby ->
                "System Dolby Atmos effect service detected: disable OS-level Dolby/DRC when using Internal 64-bit DSP or USB BIT-PERFECT."
            else -> null
        }
    }

    private fun isPackageInstalled(pkg: String): Boolean =
        runCatching {
                @Suppress("DEPRECATION")
                context.packageManager.getPackageInfo(pkg, PackageManager.PackageInfoFlags.of(0))
                true
            }
            .recoverCatching {
                @Suppress("DEPRECATION")
                context.packageManager.getPackageInfo(pkg, 0)
                true
            }
            .getOrDefault(false)

    private companion object {
        const val PREFS_NAME = "tonewise_dsp_profiles"
        const val KEY_PROFILE_PREFIX = "profile_"
        const val KEY_USB_VOLUME_CONFIRMED = "usb_bit_perfect_volume_confirmed"
    }
}
