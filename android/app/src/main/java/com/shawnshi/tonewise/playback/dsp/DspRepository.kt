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
import android.content.SharedPreferences
import android.content.pm.PackageManager
import android.os.Build
import androidx.core.content.edit
import dagger.hilt.android.qualifiers.ApplicationContext
import javax.inject.Inject
import javax.inject.Singleton
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

@Singleton
class DspRepository
internal constructor(
    private val context: Context?,
    private val prefs: SharedPreferences,
    checkRomConflict: Boolean,
) : DspConfigSource {
    @Inject
    constructor(
        @ApplicationContext context: Context
    ) : this(
        context = context,
        prefs = context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE),
        checkRomConflict = true,
    )

    private val _activeDeviceFingerprint = MutableStateFlow("UNKNOWN:system output")
    val activeDeviceFingerprint: StateFlow<String> = _activeDeviceFingerprint

    private val _activeProfileLabel = MutableStateFlow("System Output")
    val activeProfileLabel: StateFlow<String> = _activeProfileLabel

    private val _deviceConfig =
        MutableStateFlow(loadConfigForFingerprint(_activeDeviceFingerprint.value))
    val deviceConfig: StateFlow<DspChainConfig> = _deviceConfig

    private val _autoEqPolicy = MutableStateFlow(loadAutoEqPolicy())
    val autoEqPolicy: StateFlow<AutoEqPolicy> = _autoEqPolicy

    private val _activeContentResolution = MutableStateFlow(ResolvedAutoEq())
    val activeContentResolution: StateFlow<ResolvedAutoEq> = _activeContentResolution

    private var lastTrackInput: TrackAutoEqInput? = null
    private var lastWorkKey: String? = null

    private val _config =
        MutableStateFlow(
            composeEffectiveConfig(
                _deviceConfig.value,
                _autoEqPolicy.value,
                _activeContentResolution.value,
            )
        )
    override val config: StateFlow<DspChainConfig> = _config

    private val _romConflictHint =
        MutableStateFlow(if (checkRomConflict) detectRomAudioEffectConflict() else null)
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
        _deviceConfig.value = loadConfigForFingerprint(fp)
        recomputeEffectiveConfig()
    }

    fun updateConfig(transform: (DspChainConfig) -> DspChainConfig) {
        val updated = transform(_deviceConfig.value)
        _deviceConfig.value = updated
        saveConfigForFingerprint(_activeDeviceFingerprint.value, updated)
        recomputeEffectiveConfig()
    }

    fun applyPreset(presetId: DspPresetId) {
        val currentExternal = _deviceConfig.value.externalBridgeMode
        val next = DspChainConfig.preset(presetId).copy(externalBridgeMode = currentExternal)
        _deviceConfig.value = next
        saveConfigForFingerprint(_activeDeviceFingerprint.value, next)
        recomputeEffectiveConfig()
    }

    fun importEqualizerApo(text: String): Int {
        val parsed = TonewiseDspEngine.parseEqualizerApo(text)
        val cur = _deviceConfig.value
        val updated =
            parsed.copy(
                externalBridgeMode = false,
                crossfeedEnabled = cur.crossfeedEnabled,
                crossfeedCutoffHz = cur.crossfeedCutoffHz,
                crossfeedFeedDb = cur.crossfeedFeedDb,
                balanceDb = cur.balanceDb,
                invertLeftPhase = cur.invertLeftPhase,
                invertRightPhase = cur.invertRightPhase,
                monoFold = cur.monoFold,
                limiterEnabled = cur.limiterEnabled,
            )
        _deviceConfig.value = updated
        saveConfigForFingerprint(_activeDeviceFingerprint.value, updated)
        recomputeEffectiveConfig()
        return updated.bands.size
    }

    fun onTrackChanged(
        genres: List<String>,
        djVocals: String?,
        workName: String?,
        workKey: String?,
    ) {
        val input =
            TrackAutoEqInput(
                genres = genres,
                djVocals = djVocals,
                workName = workName,
                workKey = workKey,
            )
        val policy = _autoEqPolicy.value
        val resolved =
            AutoEqResolverBridge.resolve(
                policy = policy,
                track = input,
                previousWorkKey = lastWorkKey,
                previousResolved = _activeContentResolution.value.takeIf { lastWorkKey != null },
            )
        lastTrackInput = input
        lastWorkKey = workKey
        _activeContentResolution.value = resolved
        recomputeEffectiveConfig()
    }

    fun updateAutoEqPolicy(transform: (AutoEqPolicy) -> AutoEqPolicy) {
        val next =
            transform(_autoEqPolicy.value)
                .copy(updatedAtMs = System.currentTimeMillis())
        _autoEqPolicy.value = next
        saveAutoEqPolicy(next)
        if (next.enabled && !_deviceConfig.value.enabled && !_deviceConfig.value.externalBridgeMode) {
            val enabledLayerA = _deviceConfig.value.copy(enabled = true)
            _deviceConfig.value = enabledLayerA
            saveConfigForFingerprint(_activeDeviceFingerprint.value, enabledLayerA)
        }
        reResolveLastTrackAndCompose()
    }

    fun saveAutoEqRule(slotKey: String, rule: AutoEqRuleOverride) {
        updateAutoEqPolicy { cur ->
            val mutable = cur.customRules.toMutableMap()
            mutable[slotKey] = rule
            cur.copy(customRules = mutable)
        }
    }

    fun resetAutoEqRule(slotKey: String) {
        updateAutoEqPolicy { cur ->
            val mutable = cur.customRules.toMutableMap()
            mutable.remove(slotKey)
            cur.copy(customRules = mutable)
        }
    }

    fun importApoToAutoEqRule(
        slotKey: String,
        apoText: String,
        crossfeedOverride: Boolean? = null,
    ): Int {
        val parsed = TonewiseDspEngine.parseEqualizerApo(apoText)
        saveAutoEqRule(
            slotKey = slotKey,
            rule =
                AutoEqRuleOverride(
                    preampDb = parsed.preampDb,
                    bands = parsed.bands,
                    crossfeedOverride = crossfeedOverride,
                ),
        )
        return parsed.bands.size
    }

    fun exportAutoEqPolicyJson(): String = _autoEqPolicy.value.toJsonString()

    fun mergeRemoteAutoEqPolicyJson(remoteJson: String): Boolean {
        val remote = AutoEqPolicy.fromJsonString(remoteJson)
        val local = _autoEqPolicy.value
        if (remote.updatedAtMs <= local.updatedAtMs) {
            return false
        }
        _autoEqPolicy.value = remote
        saveAutoEqPolicy(remote)
        if (remote.enabled && !_deviceConfig.value.enabled && !_deviceConfig.value.externalBridgeMode) {
            val enabledLayerA = _deviceConfig.value.copy(enabled = true)
            _deviceConfig.value = enabledLayerA
            saveConfigForFingerprint(_activeDeviceFingerprint.value, enabledLayerA)
        }
        reResolveLastTrackAndCompose()
        return true
    }

    private fun reResolveLastTrackAndCompose() {
        val input = lastTrackInput
        if (input != null) {
            _activeContentResolution.value =
                AutoEqResolverBridge.resolve(
                    policy = _autoEqPolicy.value,
                    track = input,
                    previousWorkKey = null,
                    previousResolved = null,
                )
        } else {
            _activeContentResolution.value =
                ResolvedAutoEq(applied = false)
        }
        recomputeEffectiveConfig()
    }

    private fun recomputeEffectiveConfig() {
        _config.value =
            composeEffectiveConfig(
                _deviceConfig.value,
                _autoEqPolicy.value,
                _activeContentResolution.value,
            )
    }

    private fun composeEffectiveConfig(
        layerA: DspChainConfig,
        policy: AutoEqPolicy,
        layerB: ResolvedAutoEq,
    ): DspChainConfig {
        if (!layerA.enabled || layerA.externalBridgeMode || !policy.enabled || !layerB.applied) {
            return layerA
        }
        val (composedPreamp, composedBands) =
            AutoEqResolverBridge.composeDsp(
                devicePreampDb = layerA.preampDb,
                deviceBands = layerA.bands,
                contentPreampDb = layerB.preampDb,
                contentBands = layerB.bands,
            )
        return layerA.copy(
            preampDb = composedPreamp,
            bands = composedBands,
            crossfeedEnabled = layerA.crossfeedEnabled || layerB.enableCrossfeed,
        )
    }

    private fun loadAutoEqPolicy(): AutoEqPolicy {
        val raw = prefs.getString(KEY_AUTO_EQ_POLICY, null)
        return AutoEqPolicy.fromJsonString(raw)
    }

    private fun saveAutoEqPolicy(policy: AutoEqPolicy) {
        prefs.edit { putString(KEY_AUTO_EQ_POLICY, policy.toJsonString()) }
    }

    fun confirmUsbBitPerfectVolume() {
        prefs.edit { putBoolean(KEY_USB_VOLUME_CONFIRMED, true) }
        _usbBitPerfectConfirmed.value = true
    }

    private fun loadConfigForFingerprint(fp: String): DspChainConfig {
        val raw = prefs.getString("$KEY_PROFILE_PREFIX$fp", null) ?: return DspChainConfig()
        return runCatching { DspChainConfig.fromJsonString(raw) }
            .getOrDefault(DspChainConfig())
    }

    private fun saveConfigForFingerprint(fp: String, config: DspChainConfig) {
        prefs.edit { putString("$KEY_PROFILE_PREFIX$fp", config.toJsonString()) }
    }

    private fun detectRomAudioEffectConflict(): String? {
        if (context == null) return null
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

    private fun isPackageInstalled(pkg: String): Boolean {
        val ctx = context ?: return false
        return runCatching {
                @Suppress("DEPRECATION")
                ctx.packageManager.getPackageInfo(pkg, PackageManager.PackageInfoFlags.of(0))
                true
            }
            .recoverCatching {
                @Suppress("DEPRECATION")
                ctx.packageManager.getPackageInfo(pkg, 0)
                true
            }
            .getOrDefault(false)
    }

    private companion object {
        const val PREFS_NAME = "tonewise_dsp_profiles"
        const val KEY_PROFILE_PREFIX = "profile_"
        const val KEY_AUTO_EQ_POLICY = "auto_eq_policy_v1"
        const val KEY_USB_VOLUME_CONFIRMED = "usb_bit_perfect_volume_confirmed"
    }
}
