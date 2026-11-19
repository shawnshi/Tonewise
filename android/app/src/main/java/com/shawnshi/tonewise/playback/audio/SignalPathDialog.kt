/*
 * Copyright (c) 2026 Tonewise Project
 * SignalPathDialog.kt is part of Tonewise.
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

import android.os.Build
import android.os.Bundle
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.ElevatedCard
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.ComposeView
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.ViewCompositionStrategy
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.fragment.app.activityViewModels
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.media3.common.C
import com.google.android.material.bottomsheet.BackportBottomSheetDialogFragment
import dagger.hilt.android.AndroidEntryPoint
import java.util.Locale
import javax.inject.Inject
import com.shawnshi.tonewise.R
import com.shawnshi.tonewise.playback.PlaybackViewModel
import com.shawnshi.tonewise.playback.dsp.DspControlDialog
import com.shawnshi.tonewise.playback.dsp.DspRepository
import com.shawnshi.tonewise.ui.TonewiseComposeTheme

@AndroidEntryPoint
class SignalPathDialog : BackportBottomSheetDialogFragment() {
    private val playbackModel: PlaybackViewModel by activityViewModels()
    @Inject lateinit var dspRepository: DspRepository
    @Inject lateinit var audioOutputManager: AudioOutputManager

    override fun onCreateView(
        inflater: LayoutInflater,
        container: ViewGroup?,
        savedInstanceState: Bundle?,
    ): View {
        return ComposeView(requireContext()).apply {
            setViewCompositionStrategy(ViewCompositionStrategy.DisposeOnViewTreeLifecycleDestroyed)
            setContent {
                val pathState by playbackModel.signalPath.collectAsStateWithLifecycle()
                TonewiseComposeTheme {
                    SignalPathSheet(
                        state = pathState,
                        onOpenDsp = {
                            dismiss()
                            DspControlDialog.newInstance()
                                .show(parentFragmentManager, DspControlDialog.TAG)
                        },
                        onConfirmUsbVolume = {
                            dspRepository.confirmUsbBitPerfectVolume()
                            audioOutputManager.refreshRoute()
                        },
                    )
                }
            }
        }
    }

    companion object {
        const val TAG = "SignalPathDialog"

        fun newInstance() = SignalPathDialog()
    }
}

@Composable
private fun SignalPathSheet(
    state: SignalPathState,
    onOpenDsp: () -> Unit,
    onConfirmUsbVolume: () -> Unit,
) {
    Surface(
        modifier = Modifier.fillMaxWidth(),
        color = MaterialTheme.colorScheme.surface,
        tonalElevation = 2.dp,
    ) {
        Column(
            modifier =
                Modifier.fillMaxWidth()
                    .navigationBarsPadding()
                    .padding(horizontal = 20.dp, vertical = 16.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(
                    text = stringResource(R.string.lbl_signal_path),
                    style = MaterialTheme.typography.titleLarge,
                    fontWeight = FontWeight.SemiBold,
                )
                QualityBadge(tier = state.qualityTier)
            }

            SignalStageCard(
                step = "1",
                title = stringResource(R.string.lbl_signal_stage_source),
                headline = state.sourceCodec ?: "--",
                detail =
                    buildString {
                        if (state.sourceSampleRateHz > 0) {
                            append(formatSampleRate(state.sourceSampleRateHz))
                        } else {
                            append("--")
                        }
                        if (state.sourceBitrateKbps > 0) {
                            append(" · ")
                            append("${state.sourceBitrateKbps} kbps")
                        }
                    },
            )

            SignalStageCard(
                step = "2",
                title = stringResource(R.string.lbl_signal_stage_dsp),
                headline =
                    buildString {
                        append(state.decoderName ?: "Media3 AudioDecoder")
                        append(" → ")
                        append(formatPcmEncoding(state.pcmEncoding, state.floatPipelineEnabled))
                    },
                detail =
                    if (state.dspBypassed) {
                        stringResource(R.string.lbl_signal_dsp_bypassed)
                    } else {
                        String.format(
                            Locale.US,
                            "%s · ReplayGain %+.1f dB",
                            state.dspSummaryLabel,
                            state.replayGainDb,
                        )
                    },
            )

            SignalStageCard(
                step = "3",
                title = stringResource(R.string.lbl_signal_stage_mixer),
                headline = state.mixerBehaviorLabel,
                detail =
                    if (state.qualityTier == SignalQualityTier.BIT_PERFECT) {
                        stringResource(R.string.lbl_signal_mixer_direct)
                    } else {
                        stringResource(R.string.lbl_signal_mixer_system)
                    },
            )

            SignalStageCard(
                step = "4",
                title = stringResource(R.string.lbl_signal_stage_output),
                headline = state.outputDeviceName,
                detail =
                    buildString {
                        append(routeLabel(state.outputRouteType))
                        state.outputSampleRateHz?.let { hz ->
                            append(" · ")
                            append(formatSampleRate(hz))
                        }
                        append(" · ")
                        append(state.activeProfileName)
                    },
            )

            if (state.usbVolumeProtectionNeeded) {
                ElevatedCard(modifier = Modifier.fillMaxWidth(), shape = RoundedCornerShape(14.dp)) {
                    Column(
                        modifier = Modifier.fillMaxWidth().padding(14.dp),
                        verticalArrangement = Arrangement.spacedBy(8.dp),
                    ) {
                        Text(
                            text = stringResource(R.string.lbl_usb_ear_protection),
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.tertiary,
                        )
                        Button(onClick = onConfirmUsbVolume, modifier = Modifier.fillMaxWidth()) {
                            Text(stringResource(R.string.lbl_usb_ear_confirm))
                        }
                    }
                }
            }

            state.romConflictHint?.let { hint ->
                ElevatedCard(modifier = Modifier.fillMaxWidth(), shape = RoundedCornerShape(14.dp)) {
                    Text(
                        text = hint,
                        modifier = Modifier.padding(14.dp),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }

            OutlinedButton(onClick = onOpenDsp, modifier = Modifier.fillMaxWidth()) {
                Text(stringResource(R.string.lbl_dsp_open_console))
            }

            Spacer(modifier = Modifier.height(4.dp))
        }
    }
}

@Composable
private fun QualityBadge(tier: SignalQualityTier) {
    val (label, containerColor, contentColor) =
        when (tier) {
            SignalQualityTier.BIT_PERFECT ->
                Triple("BIT-PERFECT", Color(0xFF006B45), Color(0xFFFFFFFF))
            SignalQualityTier.HIGH_RES_FLOAT ->
                Triple("32-BIT FLOAT", Color(0xFF005EB8), Color(0xFFFFFFFF))
            SignalQualityTier.SYSTEM_MIXER ->
                Triple("SYSTEM MIXER", Color(0xFF805500), Color(0xFFFFFFFF))
        }
    Box(
        modifier =
            Modifier.clip(RoundedCornerShape(999.dp))
                .background(containerColor)
                .padding(horizontal = 12.dp, vertical = 5.dp)
    ) {
        Text(
            text = label,
            color = contentColor,
            style = MaterialTheme.typography.labelMedium,
            fontFamily = FontFamily.Monospace,
            fontWeight = FontWeight.Bold,
        )
    }
}

@Composable
private fun SignalStageCard(
    step: String,
    title: String,
    headline: String,
    detail: String,
) {
    ElevatedCard(
        modifier = Modifier.fillMaxWidth(),
        shape = RoundedCornerShape(14.dp),
    ) {
        Row(
            modifier = Modifier.fillMaxWidth().padding(horizontal = 14.dp, vertical = 12.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Box(
                modifier =
                    Modifier.size(28.dp)
                        .clip(CircleShape)
                        .background(MaterialTheme.colorScheme.primaryContainer),
                contentAlignment = Alignment.Center,
            ) {
                Text(
                    text = step,
                    style = MaterialTheme.typography.labelLarge,
                    color = MaterialTheme.colorScheme.onPrimaryContainer,
                    fontFamily = FontFamily.Monospace,
                    fontWeight = FontWeight.Bold,
                )
            }
            Spacer(modifier = Modifier.width(12.dp))
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    text = title,
                    style = MaterialTheme.typography.labelMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                Text(
                    text = headline,
                    style = MaterialTheme.typography.bodyLarge,
                    fontWeight = FontWeight.Medium,
                    fontFamily = FontFamily.Monospace,
                )
                Text(
                    text = detail,
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
    }
}

private fun formatSampleRate(hz: Int): String =
    if (hz % 1000 == 0) "${hz / 1000} kHz" else String.format(Locale.US, "%.1f kHz", hz / 1000.0)

private fun formatPcmEncoding(encoding: Int, floatEnabled: Boolean): String =
    when (encoding) {
        C.ENCODING_PCM_FLOAT -> "32-bit Float PCM"
        C.ENCODING_PCM_32BIT -> "32-bit PCM"
        C.ENCODING_PCM_24BIT -> "24-bit PCM"
        C.ENCODING_PCM_16BIT -> "16-bit PCM"
        else -> "Idle / Standby"
    }

@Composable
private fun routeLabel(type: OutputRouteType): String =
    when (type) {
        OutputRouteType.USB_DAC -> "USB Audio Class"
        OutputRouteType.BLUETOOTH -> stringResource(R.string.lbl_route_bluetooth)
        OutputRouteType.WIRED -> stringResource(R.string.lbl_route_wired)
        OutputRouteType.SPEAKER -> stringResource(R.string.lbl_route_speaker)
        OutputRouteType.UNKNOWN -> stringResource(R.string.lbl_route_unknown)
    }
