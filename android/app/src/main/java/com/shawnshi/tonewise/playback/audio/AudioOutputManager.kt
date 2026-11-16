/*
 * Copyright (c) 2026 Tonewise Project
 * AudioOutputManager.kt is part of Tonewise.
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
 
package com.shawnshi.tonewise.playback.audio

import android.content.Context
import android.media.AudioAttributes
import android.media.AudioDeviceCallback
import android.media.AudioDeviceInfo
import android.media.AudioManager
import android.media.AudioMixerAttributes
import android.os.Build
import android.os.Handler
import android.os.Looper
import androidx.annotation.RequiresApi
import androidx.media3.common.C
import dagger.hilt.android.qualifiers.ApplicationContext
import javax.inject.Inject
import javax.inject.Singleton
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import com.shawnshi.tonewise.music.resolve
import com.shawnshi.tonewise.playback.PlaybackSettings
import com.shawnshi.tonewise.playback.dsp.DspRepository
import org.oxycblt.musikr.Song
import timber.log.Timber as L

enum class OutputRouteType {
    USB_DAC,
    BLUETOOTH,
    WIRED,
    SPEAKER,
    UNKNOWN,
}

enum class SignalQualityTier {
    BIT_PERFECT,
    HIGH_RES_FLOAT,
    SYSTEM_MIXER,
}

data class SignalPathState(
    val qualityTier: SignalQualityTier = SignalQualityTier.HIGH_RES_FLOAT,
    val sourceCodec: String? = null,
    val sourceSampleRateHz: Int = 0,
    val sourceBitrateKbps: Int = 0,
    val decoderName: String? = null,
    val pcmEncoding: Int = C.ENCODING_INVALID,
    val floatPipelineEnabled: Boolean = true,
    val dspBypassed: Boolean = false,
    val replayGainDb: Float = 0f,
    val mixerBehaviorLabel: String = "AudioFlinger",
    val outputDeviceName: String = "System Output",
    val outputRouteType: OutputRouteType = OutputRouteType.UNKNOWN,
    val outputSampleRateHz: Int? = null,
    val activeProfileName: String = "Default",
    val dspSummaryLabel: String = "Flat",
    val romConflictHint: String? = null,
    val usbVolumeProtectionNeeded: Boolean = false,
)

@Singleton
class AudioOutputManager
@Inject
constructor(
    @ApplicationContext private val context: Context,
    private val playbackSettings: PlaybackSettings,
    private val dspRepository: DspRepository,
) {
    private val audioManager = context.getSystemService(AudioManager::class.java)
    private val mainHandler = Handler(Looper.getMainLooper())
    private val mediaAttributes =
        AudioAttributes.Builder()
            .setUsage(AudioAttributes.USAGE_MEDIA)
            .setContentType(AudioAttributes.CONTENT_TYPE_MUSIC)
            .build()

    private var activeUsbDevice: AudioDeviceInfo? = null
    private var lastUsbSampleRateHz: Int? = null
    private var lastSong: Song? = null
    private var lastDecoderName: String? = null
    private var lastPcmEncoding: Int = C.ENCODING_INVALID
    private var lastReplayGainDb: Float = 0f
    private var routeListener: ((Boolean) -> Unit)? = null

    private val _signalPath = MutableStateFlow(SignalPathState())
    val signalPath: StateFlow<SignalPathState> = _signalPath

    private val deviceCallback =
        object : AudioDeviceCallback() {
            override fun onAudioDevicesAdded(addedDevices: Array<out AudioDeviceInfo>) {
                refreshRoute()
            }

            override fun onAudioDevicesRemoved(removedDevices: Array<out AudioDeviceInfo>) {
                refreshRoute()
            }
        }

    fun start(onBitPerfectChanged: (Boolean) -> Unit) {
        routeListener = onBitPerfectChanged
        audioManager?.registerAudioDeviceCallback(deviceCallback, mainHandler)
        refreshRoute()
    }

    fun stop() {
        routeListener = null
        audioManager?.unregisterAudioDeviceCallback(deviceCallback)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
            activeUsbDevice?.let { clearBitPerfectMixer(it) }
        }
        activeUsbDevice = null
    }

    fun updatePlaybackMetadata(
        song: Song?,
        decoderName: String?,
        pcmEncoding: Int,
        replayGainDb: Float,
    ): Boolean {
        lastSong = song
        lastDecoderName = decoderName ?: lastDecoderName
        if (pcmEncoding != C.ENCODING_INVALID) {
            lastPcmEncoding = pcmEncoding
        }
        lastReplayGainDb = replayGainDb
        return refreshRoute()
    }

    @Synchronized
    fun refreshRoute(): Boolean {
        val manager = audioManager ?: return false
        val outputs = manager.getDevices(AudioManager.GET_DEVICES_OUTPUTS)
        val primaryDevice = selectPrimaryDevice(outputs)
        val routeType = primaryDevice?.let(::classifyDevice) ?: OutputRouteType.UNKNOWN
        val deviceName =
            primaryDevice?.productName?.toString()?.takeIf { it.isNotBlank() }
                ?: defaultDeviceLabel(routeType)

        var isBitPerfectActive = false
        var mixerLabel = "AudioFlinger"
        var outputSampleRateHz: Int? = null

        dspRepository.onDeviceRouteChanged(routeType.name, deviceName)

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
            val usbDevice = if (routeType == OutputRouteType.USB_DAC) primaryDevice else null
            val previousUsb = activeUsbDevice
            if (previousUsb != null && previousUsb.id != usbDevice?.id) {
                clearBitPerfectMixer(previousUsb)
            }
            activeUsbDevice = usbDevice

            if (usbDevice != null && playbackSettings.usbBitPerfect) {
                val desiredRate = lastSong?.sampleRateHz
                if (desiredRate != null && lastUsbSampleRateHz != null && lastUsbSampleRateHz != desiredRate) {
                    clearBitPerfectMixer(usbDevice)
                }
                val applied = applyBitPerfectMixer(usbDevice, desiredRate)
                if (applied != null) {
                    isBitPerfectActive = true
                    mixerLabel = "MIXER_BEHAVIOR_BIT_PERFECT"
                    outputSampleRateHz = applied.format.sampleRate
                    lastUsbSampleRateHz = outputSampleRateHz
                } else {
                    clearBitPerfectMixer(usbDevice)
                    lastUsbSampleRateHz = null
                    mixerLabel = "AudioFlinger (USB fallback)"
                }
            } else if (usbDevice != null) {
                clearBitPerfectMixer(usbDevice)
                lastUsbSampleRateHz = null
                mixerLabel = "AudioFlinger (USB DSP mode)"
            } else {
                lastUsbSampleRateHz = null
            }
        } else if (routeType == OutputRouteType.USB_DAC) {
            mixerLabel = "AudioFlinger (API < 34)"
        }

        val floatEnabled = playbackSettings.floatOutput
        val tier =
            when {
                isBitPerfectActive -> SignalQualityTier.BIT_PERFECT
                floatEnabled &&
                    (routeType == OutputRouteType.WIRED ||
                        routeType == OutputRouteType.USB_DAC ||
                        routeType == OutputRouteType.SPEAKER) -> SignalQualityTier.HIGH_RES_FLOAT
                else -> SignalQualityTier.SYSTEM_MIXER
            }

        val dspCfg = dspRepository.config.value
        val dspSummary =
            when {
                isBitPerfectActive -> "Bypassed (BIT-PERFECT)"
                dspCfg.externalBridgeMode -> "External AudioSession Bridge"
                dspCfg.gainMatchedBypass -> "Gain-Matched A/B Bypass"
                dspCfg.hasNonIdentityProcessing() ->
                    buildString {
                        append("64-bit ${dspCfg.presetId.name}")
                        if (dspCfg.crossfeedEnabled) append(" + Crossfeed")
                        if (dspCfg.limiterEnabled) append(" + Limiter")
                    }
                else -> "64-bit Flat"
            }

        val song = lastSong
        _signalPath.value =
            SignalPathState(
                qualityTier = tier,
                sourceCodec = song?.format?.resolve(context),
                sourceSampleRateHz = song?.sampleRateHz ?: 0,
                sourceBitrateKbps = song?.bitrateKbps ?: 0,
                decoderName = lastDecoderName,
                pcmEncoding = lastPcmEncoding,
                floatPipelineEnabled = floatEnabled,
                dspBypassed = isBitPerfectActive,
                replayGainDb = lastReplayGainDb,
                mixerBehaviorLabel = mixerLabel,
                outputDeviceName = deviceName,
                outputRouteType = routeType,
                outputSampleRateHz = outputSampleRateHz,
                activeProfileName = dspRepository.activeProfileLabel.value,
                dspSummaryLabel = dspSummary,
                romConflictHint =
                    dspRepository.romConflictHint.value?.takeIf {
                        !isBitPerfectActive && !dspCfg.externalBridgeMode
                    },
                usbVolumeProtectionNeeded =
                    isBitPerfectActive && !dspRepository.usbBitPerfectConfirmed.value,
            )

        val listener = routeListener
        if (listener != null) {
            if (Looper.myLooper() == Looper.getMainLooper()) {
                listener.invoke(isBitPerfectActive)
            } else {
                mainHandler.post { listener.invoke(isBitPerfectActive) }
            }
        }
        return isBitPerfectActive
    }

    @RequiresApi(Build.VERSION_CODES.UPSIDE_DOWN_CAKE)
    private fun applyBitPerfectMixer(
        device: AudioDeviceInfo,
        targetSampleRateHz: Int?,
    ): AudioMixerAttributes? {
        val manager = audioManager ?: return null
        return try {
            val supported =
                manager.getSupportedMixerAttributes(device).filter {
                    it.mixerBehavior == AudioMixerAttributes.MIXER_BEHAVIOR_BIT_PERFECT
                }
            if (supported.isEmpty()) return null

            val chosen =
                if (targetSampleRateHz != null && targetSampleRateHz > 0) {
                    supported.firstOrNull { it.format.sampleRate == targetSampleRateHz }
                        ?: supported.maxByOrNull { it.format.sampleRate }
                } else {
                    supported.maxByOrNull { it.format.sampleRate }
                } ?: return null

            val ok = manager.setPreferredMixerAttributes(mediaAttributes, device, chosen)
            if (ok) {
                L.d(
                    "Activated USB BIT_PERFECT mixer: ${chosen.format.sampleRate}Hz on ${device.productName}"
                )
                chosen
            } else {
                L.w("setPreferredMixerAttributes returned false for ${device.productName}")
                null
            }
        } catch (e: Exception) {
            L.w(e, "Failed to apply USB BIT_PERFECT mixer attributes")
            null
        }
    }

    @RequiresApi(Build.VERSION_CODES.UPSIDE_DOWN_CAKE)
    private fun clearBitPerfectMixer(device: AudioDeviceInfo) {
        try {
            audioManager?.clearPreferredMixerAttributes(mediaAttributes, device)
        } catch (e: Exception) {
            L.w(e, "Failed to clear preferred mixer attributes")
        }
    }

    private fun selectPrimaryDevice(devices: Array<AudioDeviceInfo>): AudioDeviceInfo? {
        return devices.maxByOrNull { devicePriority(classifyDevice(it)) }
    }

    private fun classifyDevice(device: AudioDeviceInfo): OutputRouteType =
        when (device.type) {
            AudioDeviceInfo.TYPE_USB_DEVICE,
            AudioDeviceInfo.TYPE_USB_HEADSET -> OutputRouteType.USB_DAC
            AudioDeviceInfo.TYPE_BLUETOOTH_A2DP,
            AudioDeviceInfo.TYPE_BLE_HEADSET,
            AudioDeviceInfo.TYPE_BLE_SPEAKER -> OutputRouteType.BLUETOOTH
            AudioDeviceInfo.TYPE_WIRED_HEADSET,
            AudioDeviceInfo.TYPE_WIRED_HEADPHONES,
            AudioDeviceInfo.TYPE_LINE_ANALOG,
            AudioDeviceInfo.TYPE_LINE_DIGITAL -> OutputRouteType.WIRED
            AudioDeviceInfo.TYPE_BUILTIN_SPEAKER -> OutputRouteType.SPEAKER
            else -> OutputRouteType.UNKNOWN
        }

    private fun devicePriority(type: OutputRouteType): Int =
        when (type) {
            OutputRouteType.USB_DAC -> 5
            OutputRouteType.WIRED -> 4
            OutputRouteType.BLUETOOTH -> 3
            OutputRouteType.SPEAKER -> 2
            OutputRouteType.UNKNOWN -> 1
        }

    private fun defaultDeviceLabel(type: OutputRouteType): String =
        when (type) {
            OutputRouteType.USB_DAC -> "USB DAC"
            OutputRouteType.BLUETOOTH -> "Bluetooth Audio"
            OutputRouteType.WIRED -> "Wired Headphones"
            OutputRouteType.SPEAKER -> "Device Speaker"
            OutputRouteType.UNKNOWN -> "System Output"
        }
}
