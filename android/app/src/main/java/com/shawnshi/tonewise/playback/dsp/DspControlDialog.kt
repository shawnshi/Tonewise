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
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.gestures.detectVerticalDragGestures
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
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
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.PathEffect
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.ComposeView
import androidx.compose.ui.platform.ViewCompositionStrategy
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.stateDescription
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.fragment.app.activityViewModels
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.google.android.material.bottomsheet.BackportBottomSheetDialogFragment
import dagger.hilt.android.AndroidEntryPoint
import java.util.Locale
import javax.inject.Inject
import com.shawnshi.tonewise.R
import com.shawnshi.tonewise.playback.PlaybackSettings
import com.shawnshi.tonewise.playback.PlaybackViewModel
import com.shawnshi.tonewise.ui.DynamicAccentController
import com.shawnshi.tonewise.ui.LocalTonewiseAccentInk
import com.shawnshi.tonewise.ui.TonewiseComposeTheme
import com.shawnshi.tonewise.ui.UISettings
import com.shawnshi.tonewise.util.showToast
import kotlin.math.PI
import kotlin.math.abs
import kotlin.math.cos
import kotlin.math.ln
import kotlin.math.log10
import kotlin.math.pow
import kotlin.math.roundToInt
import kotlin.math.sin
import kotlin.math.sqrt

@AndroidEntryPoint
class DspControlDialog : BackportBottomSheetDialogFragment() {
    private val playbackModel: PlaybackViewModel by activityViewModels()
    @Inject lateinit var dspRepository: DspRepository
    @Inject lateinit var playbackSettings: PlaybackSettings
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
                val config by dspRepository.deviceConfig.collectAsStateWithLifecycle()
                val effectiveConfig by dspRepository.config.collectAsStateWithLifecycle()
                val autoEqPolicy by dspRepository.autoEqPolicy.collectAsStateWithLifecycle()
                val activeAutoEq by dspRepository.activeContentResolution.collectAsStateWithLifecycle()
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
                        effectiveConfig = effectiveConfig,
                        autoEqPolicy = autoEqPolicy,
                        activeAutoEq = activeAutoEq,
                        profileLabel = profileLabel,
                        isBitPerfect = signalPath.dspBypassed,
                        romHint = romHint,
                        onDisableBitPerfect = { playbackSettings.setUsbBitPerfect(false) },
                        onUpdateConfig = { transform -> dspRepository.updateConfig(transform) },
                        onApplyPreset = { preset -> dspRepository.applyPreset(preset) },
                        onImportApo = { raw ->
                            val count = dspRepository.importEqualizerApo(raw)
                            requireContext().showToast(R.string.fmt_dsp_apo_imported)
                            count
                        },
                        onUpdateAutoEqPolicy = { transform -> dspRepository.updateAutoEqPolicy(transform) },
                        onSaveAutoEqRule = { slotKey, rule -> dspRepository.saveAutoEqRule(slotKey, rule) },
                        onResetAutoEqRule = { slotKey -> dspRepository.resetAutoEqRule(slotKey) },
                        onImportAutoEqRuleApo = { slotKey, raw, crossOverride ->
                            val count = dspRepository.importApoToAutoEqRule(slotKey, raw, crossOverride)
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
    effectiveConfig: DspChainConfig,
    autoEqPolicy: AutoEqPolicy,
    activeAutoEq: ResolvedAutoEq,
    profileLabel: String,
    isBitPerfect: Boolean,
    romHint: String?,
    onDisableBitPerfect: () -> Unit,
    onUpdateConfig: ((DspChainConfig) -> DspChainConfig) -> Unit,
    onApplyPreset: (DspPresetId) -> Unit,
    onImportApo: (String) -> Int,
    onUpdateAutoEqPolicy: ((AutoEqPolicy) -> AutoEqPolicy) -> Unit,
    onSaveAutoEqRule: (String, AutoEqRuleOverride) -> Unit,
    onResetAutoEqRule: (String) -> Unit,
    onImportAutoEqRuleApo: (String, String, Boolean?) -> Int,
    onLaunchExternalEq: () -> Unit,
) {
    val controlsEnabled = !isBitPerfect
    val accentInk = LocalTonewiseAccentInk.current
    var showApoBox by remember { mutableStateOf(false) }
    var apoDraft by remember {
        mutableStateOf("Preamp: -4.5 dB\nFilter: ON LSC Fc 85 Hz Gain 4.5 dB Q 0.71")
    }
    var showRuleEditor by remember { mutableStateOf(false) }
    var selectedGenre by remember { mutableStateOf(AutoEqGenreFamily.CLASSICAL) }
    var selectedVocal by remember { mutableStateOf(AutoEqVocalClass.INSTRUMENTAL) }
    var showRuleApoBox by remember { mutableStateOf(false) }
    var ruleApoDraft by remember {
        mutableStateOf("Preamp: 0.0 dB\nFilter: ON PK Fc 2000 Hz Gain 1.5 dB Q 1.41")
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
                color = accentInk,
                fontFamily = FontFamily.Monospace,
            )

            if (isBitPerfect) {
                ElevatedCard(modifier = Modifier.fillMaxWidth(), shape = MaterialTheme.shapes.medium) {
                    Column(
                        modifier = Modifier.fillMaxWidth().padding(12.dp),
                        verticalArrangement = Arrangement.spacedBy(8.dp),
                    ) {
                        Text(
                            text = stringResource(R.string.lbl_dsp_usb_bypass_notice),
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.error,
                        )
                        OutlinedButton(
                            onClick = onDisableBitPerfect,
                            modifier = Modifier.fillMaxWidth(),
                        ) {
                            Text(stringResource(R.string.lbl_dsp_disable_bit_perfect))
                        }
                    }
                }
            }

            if (romHint != null && config.enabled && !config.externalBridgeMode && controlsEnabled) {
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
                    enabled = controlsEnabled,
                    onClick = { if (controlsEnabled) onUpdateConfig { it.copy(externalBridgeMode = false) } },
                    label = { Text(stringResource(R.string.lbl_dsp_mode_internal)) },
                )
                FilterChip(
                    selected = config.externalBridgeMode,
                    enabled = controlsEnabled,
                    onClick = { if (controlsEnabled) onUpdateConfig { it.copy(externalBridgeMode = true) } },
                    label = { Text(stringResource(R.string.lbl_dsp_mode_external)) },
                )
            }

            if (config.externalBridgeMode) {
                Text(
                    text = stringResource(R.string.lbl_dsp_external_desc),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                Button(
                    onClick = { if (controlsEnabled) onLaunchExternalEq() },
                    enabled = controlsEnabled,
                    modifier = Modifier.fillMaxWidth(),
                ) {
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
                    enabled = controlsEnabled,
                    onCheckedChange = { checked ->
                        if (controlsEnabled) {
                            onUpdateConfig { it.copy(enabled = checked) }
                        }
                    },
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
                    enabled = controlsEnabled && config.enabled,
                    onCheckedChange = { checked ->
                        if (controlsEnabled) {
                            onUpdateConfig { it.copy(gainMatchedBypass = checked) }
                        }
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
                        enabled = controlsEnabled,
                        onClick = { if (controlsEnabled) onApplyPreset(id) },
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
                enabled = controlsEnabled,
                onValueChange = { v ->
                    if (controlsEnabled) {
                        onUpdateConfig {
                            it.copy(
                                presetId = DspPresetId.CUSTOM,
                                preampDb = ((v * 2f).roundToInt() / 2.0),
                            )
                        }
                    }
                },
                valueRange = -15f..6f,
            )

            HorizontalDivider()

            // Stage 2: 64-bit Biquad PEQ Console (Option B: Composite Curve + 10 Vertical Faders)
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Column(modifier = Modifier.weight(1f)) {
                    Text(
                        text = stringResource(R.string.lbl_dsp_peq_title),
                        style = MaterialTheme.typography.titleSmall,
                        color = accentInk,
                    )
                    Text(
                        text = stringResource(R.string.lbl_dsp_double_tap_reset),
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
                OutlinedButton(
                    onClick = { if (controlsEnabled) showApoBox = !showApoBox },
                    enabled = controlsEnabled,
                ) {
                    Text(stringResource(R.string.lbl_dsp_import_apo))
                }
            }

            FrequencyResponseCanvas(
                layerAConfig = config,
                effectiveConfig = effectiveConfig,
                accentInk = accentInk,
                active = controlsEnabled && config.enabled && !config.gainMatchedBypass,
                modifier = Modifier.fillMaxWidth().height(132.dp),
            )

            if (showApoBox) {
                OutlinedTextField(
                    value = apoDraft,
                    onValueChange = { if (controlsEnabled) apoDraft = it },
                    enabled = controlsEnabled,
                    label = { Text("EqualizerAPO .txt") },
                    minLines = 3,
                    maxLines = 6,
                    modifier = Modifier.fillMaxWidth(),
                )
                Button(
                    onClick = {
                        if (controlsEnabled) {
                            onImportApo(apoDraft)
                            showApoBox = false
                        }
                    },
                    enabled = controlsEnabled,
                    modifier = Modifier.fillMaxWidth(),
                ) {
                    Text(stringResource(R.string.lbl_dsp_apply_apo))
                }
            }

            // 10-band vertical console
            Row(
                modifier =
                    Modifier.fillMaxWidth()
                        .height(188.dp)
                        .alpha(if (controlsEnabled) 1f else 0.42f),
                horizontalArrangement = Arrangement.spacedBy(4.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                config.bands.take(10).forEachIndexed { idx, band ->
                    VerticalEqFader(
                        band = band,
                        enabled = controlsEnabled,
                        accentInk = accentInk,
                        onGainChange = { snapped ->
                            if (controlsEnabled) {
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
                            }
                        },
                        modifier = Modifier.weight(1f).fillMaxHeight(),
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
                    enabled = controlsEnabled,
                    onCheckedChange = { checked ->
                        if (controlsEnabled) {
                            onUpdateConfig {
                                it.copy(
                                    presetId = DspPresetId.CUSTOM,
                                    enabled = it.enabled || checked,
                                    crossfeedEnabled = checked,
                                )
                            }
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
                    enabled = controlsEnabled,
                    onValueChange = { v ->
                        if (controlsEnabled) {
                            onUpdateConfig { it.copy(crossfeedCutoffHz = ((v / 25f).roundToInt() * 25.0)) }
                        }
                    },
                    valueRange = 500f..900f,
                )
                Slider(
                    value = config.crossfeedFeedDb.toFloat(),
                    enabled = controlsEnabled,
                    onValueChange = { v ->
                        if (controlsEnabled) {
                            onUpdateConfig { it.copy(crossfeedFeedDb = ((v * 2f).roundToInt() / 2.0)) }
                        }
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
                enabled = controlsEnabled,
                onValueChange = { v ->
                    if (controlsEnabled) {
                        onUpdateConfig {
                            it.copy(
                                enabled = it.enabled || abs(v) > 0.05f,
                                balanceDb = (v * 10f).roundToInt() / 10.0,
                            )
                        }
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
                    enabled = controlsEnabled,
                    onClick = {
                        if (controlsEnabled) {
                            onUpdateConfig { it.copy(enabled = true, invertLeftPhase = !it.invertLeftPhase) }
                        }
                    },
                    label = { Text("L 180°") },
                )
                FilterChip(
                    selected = config.invertRightPhase,
                    enabled = controlsEnabled,
                    onClick = {
                        if (controlsEnabled) {
                            onUpdateConfig { it.copy(enabled = true, invertRightPhase = !it.invertRightPhase) }
                        }
                    },
                    label = { Text("R 180°") },
                )
                FilterChip(
                    selected = config.monoFold,
                    enabled = controlsEnabled,
                    onClick = {
                        if (controlsEnabled) {
                            onUpdateConfig { it.copy(enabled = true, monoFold = !it.monoFold) }
                        }
                    },
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
                    enabled = controlsEnabled,
                    onCheckedChange = { checked ->
                        if (controlsEnabled) {
                            onUpdateConfig { it.copy(limiterEnabled = checked) }
                        }
                    },
                )
            }

            HorizontalDivider()

            // Layer B: Genre + Vocal Auto-EQ (Cascade Overlay)
            ElevatedCard(modifier = Modifier.fillMaxWidth(), shape = MaterialTheme.shapes.medium) {
                Column(
                    modifier = Modifier.fillMaxWidth().padding(12.dp),
                    verticalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.SpaceBetween,
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Column(modifier = Modifier.weight(1f)) {
                            Text(
                                text = stringResource(R.string.lbl_auto_eq_title),
                                style = MaterialTheme.typography.titleSmall,
                                color = accentInk,
                            )
                            val activeBadgeText =
                                when {
                                    isBitPerfect -> stringResource(R.string.lbl_dsp_usb_bypass_notice)
                                    !autoEqPolicy.enabled -> stringResource(R.string.lbl_dsp_auto_eq_disabled)
                                    !config.enabled -> stringResource(R.string.lbl_dsp_auto_eq_bypassed)
                                    !activeAutoEq.applied -> stringResource(R.string.lbl_dsp_auto_eq_ready)
                                    !activeAutoEq.hasNonZeroEq && activeAutoEq.enableCrossfeed ->
                                        "${activeAutoEq.displayLabel} · Flat 0 dB + Crossfeed"
                                    !activeAutoEq.hasNonZeroEq -> "${activeAutoEq.displayLabel} · Flat 0 dB"
                                    activeAutoEq.enableCrossfeed ->
                                        stringResource(R.string.fmt_dsp_auto_eq_eq_crossfeed, activeAutoEq.displayLabel)
                                    else ->
                                        stringResource(R.string.fmt_dsp_auto_eq_eq, activeAutoEq.displayLabel)
                                }
                            Text(
                                text = activeBadgeText,
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        }
                        Switch(
                            checked = autoEqPolicy.enabled,
                            enabled = controlsEnabled,
                            onCheckedChange = { checked ->
                                if (controlsEnabled) {
                                    onUpdateAutoEqPolicy { it.copy(enabled = checked) }
                                }
                            },
                        )
                    }

                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.SpaceBetween,
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Text(
                            text = stringResource(R.string.lbl_auto_eq_classical_crossfeed),
                            modifier = Modifier.weight(1f),
                            style = MaterialTheme.typography.bodySmall,
                        )
                        Switch(
                            checked = autoEqPolicy.classicalAutoCrossfeed,
                            enabled = controlsEnabled,
                            onCheckedChange = { checked ->
                                if (controlsEnabled) {
                                    onUpdateAutoEqPolicy { it.copy(classicalAutoCrossfeed = checked) }
                                }
                            },
                        )
                    }

                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.SpaceBetween,
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Text(
                            text = stringResource(R.string.lbl_auto_eq_lock_work),
                            modifier = Modifier.weight(1f),
                            style = MaterialTheme.typography.bodySmall,
                        )
                        Switch(
                            checked = autoEqPolicy.lockWithinClassicalWork,
                            enabled = controlsEnabled,
                            onCheckedChange = { checked ->
                                if (controlsEnabled) {
                                    onUpdateAutoEqPolicy { it.copy(lockWithinClassicalWork = checked) }
                                }
                            },
                        )
                    }

                    OutlinedButton(
                        onClick = { if (controlsEnabled) showRuleEditor = !showRuleEditor },
                        enabled = controlsEnabled,
                        modifier = Modifier.fillMaxWidth(),
                    ) {
                        Text(stringResource(R.string.lbl_auto_eq_customize_rules))
                    }

                    if (showRuleEditor) {
                        Row(
                            modifier = Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()),
                            horizontalArrangement = Arrangement.spacedBy(6.dp),
                        ) {
                            for (g in AutoEqGenreFamily.entries) {
                                FilterChip(
                                    selected = selectedGenre == g,
                                    enabled = controlsEnabled,
                                    onClick = { if (controlsEnabled) selectedGenre = g },
                                    label = { Text(g.displayName) },
                                )
                            }
                        }

                        Row(
                            modifier = Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()),
                            horizontalArrangement = Arrangement.spacedBy(6.dp),
                        ) {
                            for (v in AutoEqVocalClass.entries) {
                                FilterChip(
                                    selected = selectedVocal == v,
                                    enabled = controlsEnabled,
                                    onClick = { if (controlsEnabled) selectedVocal = v },
                                    label = { Text(v.displayName) },
                                )
                            }
                        }

                        val slotKey = "${selectedGenre.slotKey}:${selectedVocal.slotKey}"
                        val catalogEntry =
                            remember(slotKey) {
                                AutoEqResolverBridge.defaultCatalog().firstOrNull { it.slotKey == slotKey }
                            }
                        val customRule = autoEqPolicy.customRules[slotKey]
                        val slotBands =
                            remember(slotKey, customRule, catalogEntry) {
                                (customRule?.bands ?: catalogEntry?.defaultBands ?: DspChainConfig.defaultTenBands())
                                    .take(10)
                            }
                        val crossOverride = customRule?.crossfeedOverride

                        Text(
                            text =
                                if (customRule != null) {
                                    stringResource(R.string.fmt_dsp_auto_eq_slot_custom, slotKey)
                                } else {
                                    stringResource(R.string.fmt_dsp_auto_eq_slot_default, slotKey)
                                },
                            style = MaterialTheme.typography.labelMedium,
                            color = accentInk,
                        )

                        Row(
                            modifier = Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()),
                            horizontalArrangement = Arrangement.spacedBy(6.dp),
                        ) {
                            FilterChip(
                                selected = crossOverride == null,
                                enabled = controlsEnabled,
                                onClick = {
                                    if (controlsEnabled) {
                                        onSaveAutoEqRule(
                                            slotKey,
                                            AutoEqRuleOverride(
                                                preampDb = customRule?.preampDb ?: 0.0,
                                                bands = slotBands,
                                                crossfeedOverride = null,
                                            ),
                                        )
                                    }
                                },
                                label = { Text(stringResource(R.string.lbl_dsp_crossfeed_auto)) },
                            )
                            FilterChip(
                                selected = crossOverride == true,
                                enabled = controlsEnabled,
                                onClick = {
                                    if (controlsEnabled) {
                                        onSaveAutoEqRule(
                                            slotKey,
                                            AutoEqRuleOverride(
                                                preampDb = customRule?.preampDb ?: 0.0,
                                                bands = slotBands,
                                                crossfeedOverride = true,
                                            ),
                                        )
                                    }
                                },
                                label = { Text(stringResource(R.string.lbl_dsp_crossfeed_force_on)) },
                            )
                            FilterChip(
                                selected = crossOverride == false,
                                enabled = controlsEnabled,
                                onClick = {
                                    if (controlsEnabled) {
                                        onSaveAutoEqRule(
                                            slotKey,
                                            AutoEqRuleOverride(
                                                preampDb = customRule?.preampDb ?: 0.0,
                                                bands = slotBands,
                                                crossfeedOverride = false,
                                            ),
                                        )
                                    }
                                },
                                label = { Text(stringResource(R.string.lbl_dsp_crossfeed_force_off)) },
                            )
                        }

                        slotBands.forEachIndexed { idx, band ->
                            val freqLabel = formatEqBandFreq(band.freqHz)
                            Row(
                                modifier = Modifier.fillMaxWidth(),
                                verticalAlignment = Alignment.CenterVertically,
                                horizontalArrangement = Arrangement.spacedBy(8.dp),
                            ) {
                                Text(
                                    text = freqLabel,
                                    modifier = Modifier.weight(0.28f),
                                    style = MaterialTheme.typography.labelSmall,
                                    fontFamily = FontFamily.Monospace,
                                )
                                Slider(
                                    value = band.gainDb.toFloat(),
                                    enabled = controlsEnabled,
                                    onValueChange = { v ->
                                        if (controlsEnabled) {
                                            val snapped = (v * 2f).roundToInt() / 2.0
                                            val mutable = slotBands.toMutableList()
                                            if (idx in mutable.indices) {
                                                mutable[idx] = mutable[idx].copy(gainDb = snapped, enabled = true)
                                            }
                                            onSaveAutoEqRule(
                                                slotKey,
                                                AutoEqRuleOverride(
                                                    preampDb = customRule?.preampDb ?: 0.0,
                                                    bands = mutable,
                                                    crossfeedOverride = crossOverride,
                                                ),
                                            )
                                        }
                                    },
                                    valueRange = -12f..12f,
                                    modifier = Modifier.weight(0.54f),
                                )
                                Text(
                                    text = String.format(Locale.US, "%+.1f", band.gainDb),
                                    modifier = Modifier.weight(0.18f),
                                    style = MaterialTheme.typography.labelSmall,
                                    fontFamily = FontFamily.Monospace,
                                )
                            }
                        }

                        Row(
                            modifier = Modifier.fillMaxWidth(),
                            horizontalArrangement = Arrangement.spacedBy(8.dp),
                        ) {
                            OutlinedButton(
                                onClick = { if (controlsEnabled) showRuleApoBox = !showRuleApoBox },
                                enabled = controlsEnabled,
                                modifier = Modifier.weight(1f),
                            ) {
                                Text(stringResource(R.string.lbl_dsp_import_apo))
                            }
                            OutlinedButton(
                                onClick = { if (controlsEnabled) onResetAutoEqRule(slotKey) },
                                enabled = controlsEnabled,
                                modifier = Modifier.weight(1f),
                            ) {
                                Text(stringResource(R.string.lbl_auto_eq_reset_slot))
                            }
                        }

                        if (showRuleApoBox) {
                            OutlinedTextField(
                                value = ruleApoDraft,
                                onValueChange = { if (controlsEnabled) ruleApoDraft = it },
                                enabled = controlsEnabled,
                                label = { Text("EqualizerAPO .txt ($slotKey)") },
                                minLines = 3,
                                maxLines = 6,
                                modifier = Modifier.fillMaxWidth(),
                            )
                            Button(
                                onClick = {
                                    if (controlsEnabled) {
                                        onImportAutoEqRuleApo(slotKey, ruleApoDraft, crossOverride)
                                        showRuleApoBox = false
                                    }
                                },
                                enabled = controlsEnabled,
                                modifier = Modifier.fillMaxWidth(),
                            ) {
                                Text(stringResource(R.string.lbl_dsp_apply_apo))
                            }
                        }
                    }
                }
            }

            Spacer(modifier = Modifier.height(6.dp))
        }
    }
}

private fun formatEqBandFreq(freqHz: Double): String =
    when {
        freqHz >= 1000.0 && freqHz % 1000.0 == 0.0 -> "${(freqHz / 1000.0).toInt()}k"
        freqHz >= 1000.0 -> String.format(Locale.US, "%.1fk", freqHz / 1000.0)
        else -> String.format(Locale.US, "%.0f", freqHz)
    }

@Composable
private fun VerticalEqFader(
    band: BiquadBand,
    enabled: Boolean,
    accentInk: Color,
    onGainChange: (Double) -> Unit,
    modifier: Modifier = Modifier,
) {
    var trackHeightPx by remember { mutableIntStateOf(1) }
    var currentGain by remember(band.gainDb) { mutableFloatStateOf(band.gainDb.toFloat()) }
    val freqShort = remember(band.freqHz) { formatEqBandFreq(band.freqHz) }
    val gainLabel = String.format(Locale.US, "%+.0f", band.gainDb)
    val trackBg = MaterialTheme.colorScheme.surfaceContainerHigh
    val borderColor = MaterialTheme.colorScheme.outlineVariant
    val thumbColor = if (enabled && abs(band.gainDb) > 0.05) accentInk else MaterialTheme.colorScheme.onSurfaceVariant

    Column(
        modifier = modifier,
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(4.dp),
    ) {
        Text(
            text = gainLabel,
            style = TextStyle(fontSize = 10.sp, fontFeatureSettings = "tnum"),
            fontFamily = FontFamily.Monospace,
            color = thumbColor,
            maxLines = 1,
        )

        Box(
            modifier =
                Modifier.weight(1f)
                    .fillMaxWidth()
                    .background(trackBg)
                    .border(1.dp, borderColor)
                    .onSizeChanged { trackHeightPx = it.height.coerceAtLeast(1) }
                    .semantics {
                        contentDescription = "$freqShort Hz"
                        stateDescription = String.format(Locale.US, "%+.1f dB", band.gainDb)
                    }
                    .pointerInput(enabled, band.freqHz) {
                        if (!enabled) return@pointerInput
                        detectTapGestures(
                            onDoubleTap = { onGainChange(0.0) },
                        )
                    }
                    .pointerInput(enabled, band.freqHz) {
                        if (!enabled) return@pointerInput
                        detectVerticalDragGestures(
                            onDragStart = { currentGain = band.gainDb.toFloat() },
                            onVerticalDrag = { change, dragAmount ->
                                change.consume()
                                val deltaDb = (-dragAmount / trackHeightPx.toFloat()) * 24f
                                currentGain = (currentGain + deltaDb).coerceIn(-12f, 12f)
                                val snapped = ((currentGain * 2f).roundToInt() / 2.0).coerceIn(-12.0, 12.0)
                                onGainChange(snapped)
                            },
                        )
                    },
        ) {
            Canvas(modifier = Modifier.fillMaxSize()) {
                val centerY = size.height / 2f
                drawLine(
                    color = borderColor,
                    start = Offset(0f, centerY),
                    end = Offset(size.width, centerY),
                    strokeWidth = 1.dp.toPx(),
                )
                val fraction = (band.gainDb.toFloat().coerceIn(-12f, 12f) / 12f)
                val valueY = centerY - (fraction * (size.height * 0.44f))
                val barTop = minOf(centerY, valueY)
                val barHeight = abs(valueY - centerY).coerceAtLeast(1.dp.toPx())
                drawRect(
                    color = accentInk.copy(alpha = if (enabled) 0.34f else 0.14f),
                    topLeft = Offset(2.dp.toPx(), barTop),
                    size = androidx.compose.ui.geometry.Size(size.width - 4.dp.toPx(), barHeight),
                )
                drawLine(
                    color = thumbColor,
                    start = Offset(2.dp.toPx(), valueY),
                    end = Offset(size.width - 2.dp.toPx(), valueY),
                    strokeWidth = 2.5.dp.toPx(),
                )
            }
        }

        Text(
            text = freqShort,
            style = TextStyle(fontSize = 10.sp, fontFeatureSettings = "tnum"),
            fontFamily = FontFamily.Monospace,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            maxLines = 1,
        )
    }
}

@Composable
private fun FrequencyResponseCanvas(
    layerAConfig: DspChainConfig,
    effectiveConfig: DspChainConfig,
    accentInk: Color,
    active: Boolean,
    modifier: Modifier = Modifier,
) {
    val surfaceColor = MaterialTheme.colorScheme.surfaceContainerLow
    val gridColor = MaterialTheme.colorScheme.outlineVariant
    val layerAColor = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.72f)
    val curveColor = if (active) accentInk else MaterialTheme.colorScheme.onSurfaceVariant

    val layerAPoints =
        remember(layerAConfig.preampDb, layerAConfig.bands) {
            evaluateCompositeResponseDb(layerAConfig.preampDb, layerAConfig.bands)
        }
    val combinedPoints =
        remember(effectiveConfig.preampDb, effectiveConfig.bands) {
            evaluateCompositeResponseDb(effectiveConfig.preampDb, effectiveConfig.bands)
        }

    Column(
        modifier =
            modifier
                .background(surfaceColor)
                .border(1.dp, gridColor)
                .padding(8.dp),
        verticalArrangement = Arrangement.spacedBy(4.dp),
    ) {
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text(
                text = stringResource(R.string.lbl_dsp_eq_curve_title),
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                maxLines = 1,
            )
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Text(
                    text = stringResource(R.string.lbl_dsp_eq_layer_a),
                    style = MaterialTheme.typography.labelSmall,
                    color = layerAColor,
                )
                Text(
                    text = stringResource(R.string.lbl_dsp_eq_layer_ab),
                    style = MaterialTheme.typography.labelSmall,
                    color = curveColor,
                )
            }
        }

        Canvas(modifier = Modifier.fillMaxWidth().weight(1f)) {
            val w = size.width
            val h = size.height
            if (w <= 0f || h <= 0f) return@Canvas

            val maxDb = 15f
            fun dbToY(db: Float): Float =
                ((maxDb - db.coerceIn(-maxDb, maxDb)) / (2f * maxDb)) * h

            // Horizontal dB lines: +12, +6, 0, -6, -12
            for (dbGrid in listOf(12f, 6f, 0f, -6f, -12f)) {
                val y = dbToY(dbGrid)
                drawLine(
                    color = if (dbGrid == 0f) gridColor else gridColor.copy(alpha = 0.45f),
                    start = Offset(0f, y),
                    end = Offset(w, y),
                    strokeWidth = if (dbGrid == 0f) 1.2.dp.toPx() else 0.7.dp.toPx(),
                )
            }

            // Vertical octave markers
            val logMin = ln(20.0)
            val logSpan = ln(20000.0) - logMin
            for (hz in listOf(31.0, 62.0, 125.0, 250.0, 500.0, 1000.0, 2000.0, 4000.0, 8000.0, 16000.0)) {
                val x = (((ln(hz) - logMin) / logSpan) * w).toFloat()
                drawLine(
                    color = gridColor.copy(alpha = 0.35f),
                    start = Offset(x, 0f),
                    end = Offset(x, h),
                    strokeWidth = 0.6.dp.toPx(),
                )
            }

            fun buildPath(points: FloatArray): Path {
                val p = Path()
                val last = (points.size - 1).coerceAtLeast(1)
                for (i in points.indices) {
                    val x = (i.toFloat() / last) * w
                    val y = dbToY(points[i])
                    if (i == 0) p.moveTo(x, y) else p.lineTo(x, y)
                }
                return p
            }

            // Layer A dashed baseline
            val layerAPath = buildPath(layerAPoints)
            drawPath(
                path = layerAPath,
                color = layerAColor,
                style =
                    Stroke(
                        width = 1.3.dp.toPx(),
                        pathEffect = PathEffect.dashPathEffect(floatArrayOf(6f, 6f), 0f),
                    ),
            )

            // Combined Layer A + B solid response + fill toward 0 dB
            val combinedPath = buildPath(combinedPoints)
            val fillPath =
                Path().apply {
                    addPath(combinedPath)
                    lineTo(w, dbToY(0f))
                    lineTo(0f, dbToY(0f))
                    close()
                }
            drawPath(
                path = fillPath,
                brush =
                    Brush.verticalGradient(
                        colors = listOf(curveColor.copy(alpha = 0.24f), curveColor.copy(alpha = 0.03f)),
                    ),
            )
            drawPath(
                path = combinedPath,
                color = curveColor,
                style = Stroke(width = 2.2.dp.toPx(), cap = StrokeCap.Round, join = StrokeJoin.Round),
            )
        }
    }
}

private fun evaluateCompositeResponseDb(
    preampDb: Double,
    bands: List<BiquadBand>,
    bins: Int = 96,
    sampleRateHz: Double = 48000.0,
): FloatArray {
    val activeCoeffs =
        bands.mapNotNull { b ->
            if (!b.enabled) return@mapNotNull null
            if (b.type != BiquadType.LOW_PASS && b.type != BiquadType.HIGH_PASS && abs(b.gainDb) <= 1e-4) {
                return@mapNotNull null
            }
            computeRbjNormalizedCoeffs(b, sampleRateHz)
        }

    val out = FloatArray(bins)
    val logMin = ln(20.0)
    val logSpan = ln(20000.0) - logMin
    for (i in 0 until bins) {
        val freq = kotlin.math.exp(logMin + (i.toDouble() / (bins - 1).coerceAtLeast(1)) * logSpan)
        val w = 2.0 * PI * freq.coerceIn(10.0, sampleRateHz * 0.49) / sampleRateHz
        val cos1 = cos(w)
        val sin1 = sin(w)
        val cos2 = cos(2.0 * w)
        val sin2 = sin(2.0 * w)
        var totalDb = preampDb
        for (c in activeCoeffs) {
            val nr = c.b0 + c.b1 * cos1 + c.b2 * cos2
            val ni = -(c.b1 * sin1 + c.b2 * sin2)
            val dr = 1.0 + c.a1 * cos1 + c.a2 * cos2
            val di = -(c.a1 * sin1 + c.a2 * sin2)
            val magSq = ((nr * nr + ni * ni) / (dr * dr + di * di).coerceAtLeast(1e-24)).coerceAtLeast(1e-12)
            totalDb += 10.0 * log10(magSq)
        }
        out[i] = totalDb.toFloat()
    }
    return out
}

private data class NormalizedBiquad(val b0: Double, val b1: Double, val b2: Double, val a1: Double, val a2: Double)

private fun computeRbjNormalizedCoeffs(band: BiquadBand, sampleRateHz: Double): NormalizedBiquad {
    val fc = band.freqHz.coerceIn(10.0, sampleRateHz * 0.49)
    val w0 = 2.0 * PI * fc / sampleRateHz
    val cosW0 = cos(w0)
    val sinW0 = sin(w0)
    val q = band.q.coerceIn(0.05, 30.0)
    val alpha = sinW0 / (2.0 * q)
    val a = 10.0.pow(band.gainDb.coerceIn(-24.0, 24.0) / 40.0)
    val rb0: Double
    val rb1: Double
    val rb2: Double
    val ra0: Double
    val ra1: Double
    val ra2: Double
    when (band.type) {
        BiquadType.PEAKING -> {
            rb0 = 1.0 + alpha * a
            rb1 = -2.0 * cosW0
            rb2 = 1.0 - alpha * a
            ra0 = 1.0 + alpha / a
            ra1 = -2.0 * cosW0
            ra2 = 1.0 - alpha / a
        }
        BiquadType.LOW_SHELF -> {
            val t = 2.0 * sqrt(a) * alpha
            rb0 = a * ((a + 1.0) - (a - 1.0) * cosW0 + t)
            rb1 = 2.0 * a * ((a - 1.0) - (a + 1.0) * cosW0)
            rb2 = a * ((a + 1.0) - (a - 1.0) * cosW0 - t)
            ra0 = (a + 1.0) + (a - 1.0) * cosW0 + t
            ra1 = -2.0 * ((a - 1.0) + (a + 1.0) * cosW0)
            ra2 = (a + 1.0) + (a - 1.0) * cosW0 - t
        }
        BiquadType.HIGH_SHELF -> {
            val t = 2.0 * sqrt(a) * alpha
            rb0 = a * ((a + 1.0) + (a - 1.0) * cosW0 + t)
            rb1 = -2.0 * a * ((a - 1.0) + (a + 1.0) * cosW0)
            rb2 = a * ((a + 1.0) + (a - 1.0) * cosW0 - t)
            ra0 = (a + 1.0) - (a - 1.0) * cosW0 + t
            ra1 = 2.0 * ((a - 1.0) - (a + 1.0) * cosW0)
            ra2 = (a + 1.0) - (a - 1.0) * cosW0 - t
        }
        BiquadType.LOW_PASS -> {
            rb0 = (1.0 - cosW0) / 2.0
            rb1 = 1.0 - cosW0
            rb2 = (1.0 - cosW0) / 2.0
            ra0 = 1.0 + alpha
            ra1 = -2.0 * cosW0
            ra2 = 1.0 - alpha
        }
        BiquadType.HIGH_PASS -> {
            rb0 = (1.0 + cosW0) / 2.0
            rb1 = -(1.0 + cosW0)
            rb2 = (1.0 + cosW0) / 2.0
            ra0 = 1.0 + alpha
            ra1 = -2.0 * cosW0
            ra2 = 1.0 - alpha
        }
    }
    return NormalizedBiquad(rb0 / ra0, rb1 / ra0, rb2 / ra0, ra1 / ra0, ra2 / ra0)
}
