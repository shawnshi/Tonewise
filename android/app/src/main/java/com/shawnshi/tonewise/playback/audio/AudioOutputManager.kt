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

    @Volatile private var activeFloatPipeline: Boolean = shouldUseFloatPipeline()
    private var activeUsbDevice: AudioDeviceInfo? = null
    private var lastUsbSampleRateHz: Int? = null
    private var lastUsbEncoding: Int? = null
    private var lastSong: Song? = null
    private var lastDecoderName: String? = null
    private var lastPcmEncoding: Int = C.ENCODING_INVALID
    private var lastReplayGainDb: Float = 0f
    private var routeListener: ((Boolean) -> Unit)? = null

    fun setActiveFloatPipeline(enabled: Boolean) {
        activeFloatPipeline = enabled
        refreshRoute()
    }

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

    fun shouldUseFloatPipeline(): Boolean =
        resolveEnableFloatOutput(
            floatOutput = playbackSettings.floatOutput,
            usbBitPerfect = playbackSettings.usbBitPerfect,
            apiLevel = Build.VERSION.SDK_INT,
        )

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
        val routedDevice =
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                try {
                    manager.getAudioDevicesForAttributes(mediaAttributes).firstOrNull()
                } catch (_: Exception) {
                    null
                }
            } else {
                null
            }
        val primaryDevice = routedDevice ?: selectPrimaryDevice(outputs)
        val routeType = primaryDevice?.let(::classifyDevice) ?: OutputRouteType.UNKNOWN
        val deviceName =
            primaryDevice?.productName?.toString()?.takeIf { it.isNotBlank() }
                ?: defaultDeviceLabel(routeType)

        var isBitPerfectActive = false
        var mixerLabel = "AudioFlinger"
        var outputSampleRateHz: Int? = null
        val actualTrackEncoding =
            if (activeFloatPipeline) C.ENCODING_PCM_FLOAT else C.ENCODING_PCM_16BIT

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
                if ((desiredRate != null && lastUsbSampleRateHz != null && lastUsbSampleRateHz != desiredRate) ||
                    (lastUsbEncoding != null &&
                        lastUsbEncoding != media3ToAudioFormatEncoding(actualTrackEncoding))
                ) {
                    clearBitPerfectMixer(usbDevice)
                }
                val applied = applyBitPerfectMixer(usbDevice, desiredRate, actualTrackEncoding)
                if (applied != null) {
                    isBitPerfectActive = true
                    mixerLabel = "MIXER_BEHAVIOR_BIT_PERFECT"
                    outputSampleRateHz = applied.format.sampleRate
                    lastUsbSampleRateHz = outputSampleRateHz
                    lastUsbEncoding = applied.format.encoding
                } else {
                    clearBitPerfectMixer(usbDevice)
                    lastUsbSampleRateHz = null
                    lastUsbEncoding = null
                    mixerLabel = "AudioFlinger (USB DSP mode)"
                }
            } else if (usbDevice != null) {
                clearBitPerfectMixer(usbDevice)
                lastUsbSampleRateHz = null
                lastUsbEncoding = null
                mixerLabel = "AudioFlinger (USB DSP mode)"
            } else {
                lastUsbSampleRateHz = null
                lastUsbEncoding = null
            }
        } else if (routeType == OutputRouteType.USB_DAC) {
            mixerLabel = "AudioFlinger (API < 34)"
        }

        val floatEnabled = activeFloatPipeline
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
        sourceMedia3PcmEncoding: Int,
    ): AudioMixerAttributes? {
        val manager = audioManager ?: return null
        return try {
            val supported =
                manager.getSupportedMixerAttributes(device).filter {
                    it.mixerBehavior == AudioMixerAttributes.MIXER_BEHAVIOR_BIT_PERFECT
                }
            if (supported.isEmpty()) return null

            val candidates =
                supported.map { BitPerfectCandidate(it.format.sampleRate, it.format.encoding) }
            val chosenCandidate =
                selectBestBitPerfectCandidate(
                    candidates = candidates,
                    targetSampleRateHz = targetSampleRateHz,
                    sourceMedia3PcmEncoding = sourceMedia3PcmEncoding,
                    preferIntegerPcm = !activeFloatPipeline,
                ) ?: return null
            if (!isExactBitPerfectMatch(chosenCandidate, targetSampleRateHz, sourceMedia3PcmEncoding)) {
                L.w(
                    "Rejecting USB BIT_PERFECT mixer mismatch: candidate=${chosenCandidate.sampleRateHz}Hz enc=${chosenCandidate.audioFormatEncoding} vs trackEnc=$sourceMedia3PcmEncoding targetRate=$targetSampleRateHz"
                )
                return null
            }
            val chosen =
                supported.firstOrNull {
                    it.format.sampleRate == chosenCandidate.sampleRateHz &&
                        it.format.encoding == chosenCandidate.audioFormatEncoding
                } ?: supported.first()

            val ok = manager.setPreferredMixerAttributes(mediaAttributes, device, chosen)
            if (ok) {
                L.d(
                    "Activated USB BIT_PERFECT mixer: ${chosen.format.sampleRate}Hz enc=${chosen.format.encoding} on ${device.productName}"
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

    data class BitPerfectCandidate(val sampleRateHz: Int, val audioFormatEncoding: Int)

    companion object {
        const val PCM_ENCODING_INVALID = 0
        const val PCM_ENCODING_16BIT = 2 // AudioFormat.ENCODING_PCM_16BIT
        const val PCM_ENCODING_FLOAT = 4 // AudioFormat.ENCODING_PCM_FLOAT
        const val PCM_ENCODING_24BIT_PACKED = 21 // AudioFormat.ENCODING_PCM_24BIT_PACKED
        const val PCM_ENCODING_32BIT = 22 // AudioFormat.ENCODING_PCM_32BIT

        fun resolveEnableFloatOutput(
            floatOutput: Boolean,
            usbBitPerfect: Boolean,
            apiLevel: Int,
        ): Boolean = floatOutput && !(usbBitPerfect && apiLevel >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE)

        fun media3ToAudioFormatEncoding(media3Encoding: Int): Int =
            when (media3Encoding) {
                C.ENCODING_PCM_16BIT -> PCM_ENCODING_16BIT
                C.ENCODING_PCM_24BIT -> PCM_ENCODING_24BIT_PACKED
                C.ENCODING_PCM_32BIT -> PCM_ENCODING_32BIT
                C.ENCODING_PCM_FLOAT -> PCM_ENCODING_FLOAT
                else -> PCM_ENCODING_INVALID
            }

        private fun integerBitDepthRank(audioFormatEncoding: Int): Int =
            when (audioFormatEncoding) {
                PCM_ENCODING_16BIT -> 16
                PCM_ENCODING_24BIT_PACKED -> 24
                PCM_ENCODING_32BIT -> 32
                else -> -1
            }

        fun isExactBitPerfectMatch(
            candidate: BitPerfectCandidate,
            targetSampleRateHz: Int?,
            actualTrackMedia3Encoding: Int,
        ): Boolean {
            val expectedEncoding = media3ToAudioFormatEncoding(actualTrackMedia3Encoding)
            if (expectedEncoding == PCM_ENCODING_INVALID) return false
            if (candidate.audioFormatEncoding != expectedEncoding) return false
            if (targetSampleRateHz != null && targetSampleRateHz > 0 && candidate.sampleRateHz != targetSampleRateHz) {
                return false
            }
            return true
        }

        fun selectBestBitPerfectCandidate(
            candidates: List<BitPerfectCandidate>,
            targetSampleRateHz: Int?,
            sourceMedia3PcmEncoding: Int,
            preferIntegerPcm: Boolean = true,
        ): BitPerfectCandidate? {
            if (candidates.isEmpty()) return null
            val sameRate =
                if (targetSampleRateHz != null && targetSampleRateHz > 0) {
                    candidates.filter { it.sampleRateHz == targetSampleRateHz }
                } else {
                    emptyList()
                }
            val ratePool =
                if (sameRate.isNotEmpty()) {
                    sameRate
                } else {
                    val maxRate = candidates.maxOf { it.sampleRateHz }
                    candidates.filter { it.sampleRateHz == maxRate }
                }

            val encodingPool =
                if (preferIntegerPcm) {
                    val integerOnly = ratePool.filter { it.audioFormatEncoding != PCM_ENCODING_FLOAT }
                    integerOnly.ifEmpty { ratePool }
                } else {
                    ratePool
                }

            val desiredEncoding = media3ToAudioFormatEncoding(sourceMedia3PcmEncoding)
            if (desiredEncoding != PCM_ENCODING_INVALID) {
                encodingPool.firstOrNull { it.audioFormatEncoding == desiredEncoding }?.let {
                    return it
                }
            }

            val desiredRank = integerBitDepthRank(desiredEncoding)
            val integerRanked =
                encodingPool
                    .map { it to integerBitDepthRank(it.audioFormatEncoding) }
                    .filter { it.second > 0 }
            if (integerRanked.isNotEmpty()) {
                if (desiredRank > 0) {
                    integerRanked
                        .filter { it.second >= desiredRank }
                        .minByOrNull { it.second }
                        ?.first
                        ?.let { return it }
                }
                return integerRanked.maxByOrNull { it.second }?.first
            }

            return encodingPool.firstOrNull()
        }
    }
}
