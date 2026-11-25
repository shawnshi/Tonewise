/*
 * Copyright (c) 2026 Tonewise Project
 * DspControlDialog.kt is part of Tonewise.
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

import android.content.ActivityNotFoundException
import android.content.Intent
import android.media.audiofx.AudioEffect
import android.os.Build
import android.os.Bundle
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import androidx.activity.result.ActivityResultLauncher
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.ElevatedCard
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Slider
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.ComposeView
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.ViewCompositionStrategy
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.fragment.app.activityViewModels
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.google.android.material.bottomsheet.BackportBottomSheetDialogFragment
import dagger.hilt.android.AndroidEntryPoint
import java.util.Locale
import javax.inject.Inject
import com.shawnshi.tonewise.R
import com.shawnshi.tonewise.playback.PlaybackViewModel
import com.shawnshi.tonewise.ui.DynamicAccentController
import com.shawnshi.tonewise.ui.TonewiseComposeTheme
import com.shawnshi.tonewise.ui.UISettings
import com.shawnshi.tonewise.util.showToast
import kotlin.math.roundToInt

@AndroidEntryPoint
class DspControlDialog : BackportBottomSheetDialogFragment() {
    private val playbackModel: PlaybackViewModel by activityViewModels()
    @Inject lateinit var dspRepository: DspRepository
    @Inject lateinit var uiSettings: UISettings
    @Inject lateinit var dynamicAccentController: DynamicAccentController
    private var equalizerLauncher: ActivityResultLauncher<Intent>? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        equalizerLauncher =
            registerForActivityResult(ActivityResultContracts.StartActivityForResult()) {}
    }

    override fun onDestroy() {
        equalizerLauncher = null
        super.onDestroy()
    }

    override fun onCreateView(
        inflater: LayoutInflater,
        container: ViewGroup?,
        savedInstanceState: Bundle?,
    ): View {
        return ComposeView(requireContext()).apply {
            setViewCompositionStrategy(ViewCompositionStrategy.DisposeOnViewTreeLifecycleDestroyed)
            setContent {
                val config by dspRepository.config.collectAsStateWithLifecycle()
                val profileLabel by dspRepository.activeProfileLabel.collectAsStateWithLifecycle()
                val signalPath by playbackModel.signalPath.collectAsStateWithLifecycle()
                val romHint by dspRepository.romConflictHint.collectAsStateWithLifecycle()
                val activeAccent by dynamicAccentController.activeAccentArgb.collectAsStateWithLifecycle()

                TonewiseComposeTheme(
                    roundMode = uiSettings.roundMode,
                    dynamicAccentArgb = if (uiSettings.enableDynamicCoverAccent) activeAccent else null,
                ) {
                    DspControlSheet(
                        config = config,
                        profileLabel = profileLabel,
                        isBitPerfect = signalPath.dspBypassed,
                        romHint = romHint,
                        onUpdateConfig = { transform -> dspRepository.updateConfig(transform) },
                        onApplyPreset = { preset -> dspRepository.applyPreset(preset) },
                        onImportApo = { raw ->
                            val count = dspRepository.importEqualizerApo(raw)
                            requireContext().showToast(R.string.fmt_dsp_apo_imported)
                            count
                        },
                        onLaunchExternalEq = { launchSystemEqualizer() },
                    )
                }
            }
        }
    }

    private fun launchSystemEqualizer() {
        val intent =
            Intent(AudioEffect.ACTION_DISPLAY_AUDIO_EFFECT_CONTROL_PANEL)
                .putExtra(AudioEffect.EXTRA_AUDIO_SESSION, playbackModel.currentAudioSessionId)
                .putExtra(AudioEffect.EXTRA_CONTENT_TYPE, AudioEffect.CONTENT_TYPE_MUSIC)
        try {
            equalizerLauncher?.launch(intent)
        } catch (_: ActivityNotFoundException) {
            requireContext().showToast(R.string.err_no_app)
        }
    }

    companion object {
        const val TAG = "DspControlDialog"

        fun newInstance() = DspControlDialog()
    }
}

@Composable
private fun DspControlSheet(
    config: DspChainConfig,
    profileLabel: String,
    isBitPerfect: Boolean,
    romHint: String?,
    onUpdateConfig: ((DspChainConfig) -> DspChainConfig) -> Unit,
    onApplyPreset: (DspPresetId) -> Unit,
    onImportApo: (String) -> Int,
    onLaunchExternalEq: () -> Unit,
) {
    var showApoBox by remember { mutableStateOf(false) }
    var apoDraft by remember {
        mutableStateOf("Preamp: -4.5 dB\nFilter: ON LSC Fc 85 Hz Gain 4.5 dB Q 0.71")
    }

    Surface(modifier = Modifier.fillMaxWidth(), color = MaterialTheme.colorScheme.surface) {
        Column(
            modifier =
                Modifier.fillMaxWidth()
                    .verticalScroll(rememberScrollState())
                    .navigationBarsPadding()
                    .padding(20.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            Text(
                text = stringResource(R.string.lbl_dsp_title),
                style = MaterialTheme.typography.titleLarge,
                fontWeight = FontWeight.SemiBold,
            )
            Text(
                text = stringResource(R.string.fmt_dsp_device_profile, profileLabel),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.primary,
                fontFamily = FontFamily.Monospace,
            )

            if (isBitPerfect) {
                ElevatedCard(modifier = Modifier.fillMaxWidth(), shape = MaterialTheme.shapes.medium) {
                    Text(
                        text = stringResource(R.string.lbl_dsp_usb_bypass_notice),
                        modifier = Modifier.padding(12.dp),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.tertiary,
                    )
                }
            }

            if (romHint != null && config.enabled && !config.externalBridgeMode && !isBitPerfect) {
                ElevatedCard(modifier = Modifier.fillMaxWidth(), shape = MaterialTheme.shapes.medium) {
                    Text(
                        text = romHint,
                        modifier = Modifier.padding(12.dp),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }

            // Mutual Exclusion Engine Mode: Internal 64-bit vs External AudioSession
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                FilterChip(
                    selected = !config.externalBridgeMode,
                    onClick = { onUpdateConfig { it.copy(externalBridgeMode = false) } },
                    label = { Text(stringResource(R.string.lbl_dsp_mode_internal)) },
                )
                FilterChip(
                    selected = config.externalBridgeMode,
                    onClick = { onUpdateConfig { it.copy(externalBridgeMode = true) } },
                    label = { Text(stringResource(R.string.lbl_dsp_mode_external)) },
                )
            }

            if (config.externalBridgeMode) {
                Text(
                    text = stringResource(R.string.lbl_dsp_external_desc),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                Button(onClick = onLaunchExternalEq, modifier = Modifier.fillMaxWidth()) {
                    Text(stringResource(R.string.lbl_dsp_open_external_eq))
                }
                return@Column
            }

            // Master Enable + Gain-Matched A/B Bypass
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(stringResource(R.string.lbl_dsp_master_enable), style = MaterialTheme.typography.bodyMedium)
                Switch(
                    checked = config.enabled,
                    onCheckedChange = { checked -> onUpdateConfig { it.copy(enabled = checked) } },
                )
            }

            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(stringResource(R.string.lbl_dsp_gain_matched_ab), style = MaterialTheme.typography.bodyMedium)
                Switch(
                    checked = config.gainMatchedBypass,
                    enabled = config.enabled,
                    onCheckedChange = { checked ->
                        onUpdateConfig { it.copy(gainMatchedBypass = checked) }
                    },
                )
            }

            // Built-in Acoustic Presets
            Row(
                modifier = Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                val presets =
                    listOf(
                        DspPresetId.FLAT to stringResource(R.string.lbl_dsp_preset_flat),
                        DspPresetId.OPEN_EAR_BLUETOOTH to stringResource(R.string.lbl_dsp_preset_open_ear),
                        DspPresetId.FOCUS_NIGHT to stringResource(R.string.lbl_dsp_preset_focus),
                        DspPresetId.CLASSICAL_CROSSFEED to stringResource(R.string.lbl_dsp_preset_crossfeed),
                    )
                for ((id, label) in presets) {
                    FilterChip(
                        selected = config.presetId == id,
                        onClick = { onApplyPreset(id) },
                        label = { Text(label) },
                    )
                }
            }

            // Stage 1: Preamp
            Text(
                text =
                    String.format(
                        Locale.US,
                        "%s: %+.1f dB",
                        stringResource(R.string.lbl_dsp_preamp),
                        config.preampDb,
                    ),
                style = MaterialTheme.typography.labelLarge,
                fontFamily = FontFamily.Monospace,
            )
            Slider(
                value = config.preampDb.toFloat(),
                onValueChange = { v ->
                    onUpdateConfig {
                        it.copy(
                            presetId = DspPresetId.CUSTOM,
                            preampDb = ((v * 2f).roundToInt() / 2.0),
                        )
                    }
                },
                valueRange = -15f..6f,
            )

            HorizontalDivider()

            // Stage 2: 64-bit Biquad PEQ Bands
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(
                    text = stringResource(R.string.lbl_dsp_peq_title),
                    style = MaterialTheme.typography.titleSmall,
                    color = MaterialTheme.colorScheme.primary,
                )
                OutlinedButton(onClick = { showApoBox = !showApoBox }) {
                    Text(stringResource(R.string.lbl_dsp_import_apo))
                }
            }

            if (showApoBox) {
                OutlinedTextField(
                    value = apoDraft,
                    onValueChange = { apoDraft = it },
                    label = { Text("EqualizerAPO .txt") },
                    minLines = 3,
                    maxLines = 6,
                    modifier = Modifier.fillMaxWidth(),
                )
                Button(
                    onClick = {
                        onImportApo(apoDraft)
                        showApoBox = false
                    },
                    modifier = Modifier.fillMaxWidth(),
                ) {
                    Text(stringResource(R.string.lbl_dsp_apply_apo))
                }
            }

            config.bands.take(10).forEachIndexed { idx, band ->
                val freqLabel =
                    if (band.freqHz >= 1000.0) {
                        String.format(Locale.US, "%.1fk Hz", band.freqHz / 1000.0)
                    } else {
                        String.format(Locale.US, "%.0f Hz", band.freqHz)
                    }
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    Text(
                        text = "$freqLabel (${band.type.name.take(2)})",
                        modifier = Modifier.weight(0.32f),
                        style = MaterialTheme.typography.labelSmall,
                        fontFamily = FontFamily.Monospace,
                    )
                    Slider(
                        value = band.gainDb.toFloat(),
                        onValueChange = { v ->
                            val snapped = (v * 2f).roundToInt() / 2.0
                            onUpdateConfig { cur ->
                                val mutable = cur.bands.toMutableList()
                                if (idx in mutable.indices) {
                                    mutable[idx] = mutable[idx].copy(gainDb = snapped, enabled = true)
                                }
                                cur.copy(
                                    presetId = DspPresetId.CUSTOM,
                                    enabled = true,
                                    bands = mutable,
                                )
                            }
                        },
                        valueRange = -12f..12f,
                        modifier = Modifier.weight(0.50f),
                    )
                    Text(
                        text = String.format(Locale.US, "%+.1f", band.gainDb),
                        modifier = Modifier.weight(0.18f),
                        style = MaterialTheme.typography.labelSmall,
                        fontFamily = FontFamily.Monospace,
                    )
                }
            }

            HorizontalDivider()

            // Stage 3: Bauer Crossfeed
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(stringResource(R.string.lbl_dsp_crossfeed), style = MaterialTheme.typography.bodyMedium)
                Switch(
                    checked = config.crossfeedEnabled,
                    onCheckedChange = { checked ->
                        onUpdateConfig {
                            it.copy(
                                presetId = DspPresetId.CUSTOM,
                                enabled = it.enabled || checked,
                                crossfeedEnabled = checked,
                            )
                        }
                    },
                )
            }
            if (config.crossfeedEnabled) {
                Text(
                    text =
                        String.format(
                            Locale.US,
                            "fc = %.0f Hz · feed = %.1f dB",
                            config.crossfeedCutoffHz,
                            config.crossfeedFeedDb,
                        ),
                    style = MaterialTheme.typography.labelSmall,
                    fontFamily = FontFamily.Monospace,
                )
                Slider(
                    value = config.crossfeedCutoffHz.toFloat(),
                    onValueChange = { v ->
                        onUpdateConfig { it.copy(crossfeedCutoffHz = ((v / 25f).roundToInt() * 25.0)) }
                    },
                    valueRange = 500f..900f,
                )
                Slider(
                    value = config.crossfeedFeedDb.toFloat(),
                    onValueChange = { v ->
                        onUpdateConfig { it.copy(crossfeedFeedDb = ((v * 2f).roundToInt() / 2.0)) }
                    },
                    valueRange = 1.5f..9.5f,
                )
            }

            // Stage 4: Balance, Polarity & Mono Fold
            Text(
                text =
                    String.format(
                        Locale.US,
                        "%s: %+.1f dB",
                        stringResource(R.string.lbl_dsp_balance),
                        config.balanceDb,
                    ),
                style = MaterialTheme.typography.labelLarge,
                fontFamily = FontFamily.Monospace,
            )
            Slider(
                value = config.balanceDb.toFloat(),
                onValueChange = { v ->
                    onUpdateConfig {
                        it.copy(
                            enabled = it.enabled || kotlin.math.abs(v) > 0.05f,
                            balanceDb = (v * 10f).roundToInt() / 10.0,
                        )
                    }
                },
                valueRange = -6f..6f,
            )
            Row(
                modifier = Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                FilterChip(
                    selected = config.invertLeftPhase,
                    onClick = { onUpdateConfig { it.copy(enabled = true, invertLeftPhase = !it.invertLeftPhase) } },
                    label = { Text("L 180°") },
                )
                FilterChip(
                    selected = config.invertRightPhase,
                    onClick = { onUpdateConfig { it.copy(enabled = true, invertRightPhase = !it.invertRightPhase) } },
                    label = { Text("R 180°") },
                )
                FilterChip(
                    selected = config.monoFold,
                    onClick = { onUpdateConfig { it.copy(enabled = true, monoFold = !it.monoFold) } },
                    label = { Text("Mono Fold") },
                )
            }

            // Stage 5: Lookahead Limiter
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(stringResource(R.string.lbl_dsp_limiter), style = MaterialTheme.typography.bodyMedium)
                Switch(
                    checked = config.limiterEnabled,
                    onCheckedChange = { checked ->
                        onUpdateConfig { it.copy(limiterEnabled = checked) }
                    },
                )
            }

            Spacer(modifier = Modifier.height(6.dp))
        }
    }
}
