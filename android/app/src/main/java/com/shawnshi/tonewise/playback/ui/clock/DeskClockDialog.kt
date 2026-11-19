/*
 * Copyright (c) 2026 Tonewise Project
 * DeskClockDialog.kt is part of Tonewise.
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

package com.shawnshi.tonewise.playback.ui.clock

import android.content.Intent
import android.content.IntentFilter
import android.os.BatteryManager
import android.os.Build
import android.os.Bundle
import android.text.format.DateFormat
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.view.WindowManager
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectHorizontalDragGestures
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.PointerEventPass
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.ComposeView
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.ViewCompositionStrategy
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.TextUnit
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.fragment.app.DialogFragment
import androidx.fragment.app.activityViewModels
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import kotlinx.coroutines.flow.StateFlow
import com.shawnshi.tonewise.R
import com.shawnshi.tonewise.image.CoverView
import com.shawnshi.tonewise.music.resolve
import com.shawnshi.tonewise.music.resolveNames
import com.shawnshi.tonewise.playback.PlaybackViewModel
import com.shawnshi.tonewise.playback.audio.SignalPathState
import com.shawnshi.tonewise.playback.audio.SignalQualityTier
import com.shawnshi.tonewise.playback.formatDurationDs
import com.shawnshi.tonewise.playback.formatDurationMs
import com.shawnshi.tonewise.playback.sleep.SleepTimerMode
import com.shawnshi.tonewise.playback.sleep.SleepTimerState
import com.shawnshi.tonewise.playback.state.RepeatMode
import dagger.hilt.android.AndroidEntryPoint
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import kotlinx.coroutines.delay
import org.oxycblt.musikr.Song

/**
 * Pure-black (`#000000`) OLED Braun/Nagra studio chronometer & reference monitor with cyclic
 * 60-second `±12dp` pixel-shift burn-in protection, live hardware telemetry, and framed track deck.
 */
@AndroidEntryPoint
class DeskClockDialog : DialogFragment() {
    private val playbackModel: PlaybackViewModel by activityViewModels()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setStyle(STYLE_NO_FRAME, android.R.style.Theme_Black_NoTitleBar_Fullscreen)
    }

    override fun onStart() {
        super.onStart()
        dialog?.window?.let { window ->
            WindowCompat.setDecorFitsSystemWindows(window, false)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
                val attrs = window.attributes
                attrs.layoutInDisplayCutoutMode =
                    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                        WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_ALWAYS
                    } else {
                        WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES
                    }
                window.attributes = attrs
            }
            WindowInsetsControllerCompat(window, window.decorView).apply {
                systemBarsBehavior =
                    WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
                hide(WindowInsetsCompat.Type.systemBars())
            }
            window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        }
    }

    override fun onStop() {
        dialog?.window?.let { window ->
            window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
            val attrs = window.attributes
            if (attrs.screenBrightness >= 0f) {
                attrs.screenBrightness = WindowManager.LayoutParams.BRIGHTNESS_OVERRIDE_NONE
                window.attributes = attrs
            }
        }
        super.onStop()
    }

    private fun updateKeepScreenOn(keepOn: Boolean) {
        val window = dialog?.window ?: return
        if (keepOn) {
            window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        } else {
            window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        }
    }

    private fun updateScreenBrightness(level: Float) {
        val window = dialog?.window ?: return
        val attrs = window.attributes
        if (attrs.screenBrightness != level) {
            attrs.screenBrightness = level
            window.attributes = attrs
        }
    }

    override fun onCreateView(
        inflater: LayoutInflater,
        container: ViewGroup?,
        savedInstanceState: Bundle?,
    ): View {
        return ComposeView(requireContext()).apply {
            setViewCompositionStrategy(ViewCompositionStrategy.DisposeOnViewTreeLifecycleDestroyed)
            setContent {
                val song by playbackModel.song.collectAsStateWithLifecycle()
                val isPlaying by playbackModel.isPlaying.collectAsStateWithLifecycle()
                val signalPath by playbackModel.signalPath.collectAsStateWithLifecycle()
                val sleepTimerState by playbackModel.sleepTimerState.collectAsStateWithLifecycle()
                val repeatMode by playbackModel.repeatMode.collectAsStateWithLifecycle()
                val isShuffled by playbackModel.isShuffled.collectAsStateWithLifecycle()

                MaterialTheme(
                    colorScheme = darkColorScheme(background = Color.Black, surface = Color.Black)
                ) {
                    DeskClockScreen(
                        song = song,
                        isPlaying = isPlaying,
                        positionDsFlow = playbackModel.positionDs,
                        waveformFlow = playbackModel.waveformEnvelope,
                        repeatMode = repeatMode,
                        isShuffled = isShuffled,
                        signalPath = signalPath,
                        sleepTimerState = sleepTimerState,
                        onSetSleepMinutes = playbackModel::setSleepTimerMinutes,
                        onSetSleepEndOfTrack = playbackModel::setSleepTimerEndOfTrack,
                        onSetSleepEndOfWork = playbackModel::setSleepTimerEndOfWork,
                        onCancelSleepTimer = playbackModel::cancelSleepTimer,
                        onKeepScreenOnChanged = ::updateKeepScreenOn,
                        onBrightnessChanged = ::updateScreenBrightness,
                        onSeekTo = { positionDs -> playbackModel.seekTo(positionDs) },
                        onPrev = { playbackModel.prev() },
                        onPlayPause = { playbackModel.togglePlaying() },
                        onNext = { playbackModel.next() },
                        onToggleShuffle = { playbackModel.toggleShuffled() },
                        onToggleRepeat = { playbackModel.toggleRepeatMode() },
                        onDismiss = { dismiss() },
                    )
                }
            }
        }
    }

    companion object {
        const val TAG = "DeskClockDialog"

        fun newInstance() = DeskClockDialog()
    }
}

private enum class ClockBrightnessMode(val level: Float, val label: String) {
    SYSTEM(WindowManager.LayoutParams.BRIGHTNESS_OVERRIDE_NONE, "AUTO"),
    DIM(0.18f, "DIM"),
    NIGHT(0.04f, "NIGHT"),
}

private val OledInkPrimary = Color(0xFFF2F0EC)
private val OledInkSecondary = Color(0xFF8C929C)
private val OledInkMuted = Color(0xFF5A606B)
private val OledBrass = Color(0xFFD9B372)
private val OledDeckSurface = Color(0xFF090A0D)
private val OledDeckBorder = Color(0xFF1A1D24)
private val OledSleeveBorder = Color(0xFF262A34)

@Composable
private fun DeskClockScreen(
    song: Song?,
    isPlaying: Boolean,
    positionDsFlow: StateFlow<Long>,
    waveformFlow: StateFlow<FloatArray?>,
    repeatMode: RepeatMode,
    isShuffled: Boolean,
    signalPath: SignalPathState,
    sleepTimerState: SleepTimerState,
    onSetSleepMinutes: (Int) -> Unit,
    onSetSleepEndOfTrack: () -> Unit,
    onSetSleepEndOfWork: () -> Unit,
    onCancelSleepTimer: () -> Unit,
    onKeepScreenOnChanged: (Boolean) -> Unit,
    onBrightnessChanged: (Float) -> Unit,
    onSeekTo: (Long) -> Unit,
    onPrev: () -> Unit,
    onPlayPause: () -> Unit,
    onNext: () -> Unit,
    onToggleShuffle: () -> Unit,
    onToggleRepeat: () -> Unit,
    onDismiss: () -> Unit,
) {
    val context = LocalContext.current
    var brightnessMode by remember { mutableStateOf(ClockBrightnessMode.SYSTEM) }
    var lastInteractionMs by remember { mutableLongStateOf(System.currentTimeMillis()) }
    var chromeVisible by remember { mutableStateOf(true) }
    var showSleepMenu by remember { mutableStateOf(false) }
    var sleepExpiredLockReleased by remember { mutableStateOf(false) }
    var lastHandledExpiryMs by remember { mutableLongStateOf(sleepTimerState.expiredEpochMs) }

    LaunchedEffect(sleepTimerState.expiredEpochMs) {
        if (sleepTimerState.expiredEpochMs > lastHandledExpiryMs) {
            lastHandledExpiryMs = sleepTimerState.expiredEpochMs
            sleepExpiredLockReleased = true
            brightnessMode = ClockBrightnessMode.NIGHT
            onBrightnessChanged(0.02f)
            onKeepScreenOnChanged(false)
        }
    }

    LaunchedEffect(isPlaying, song != null, brightnessMode, sleepExpiredLockReleased) {
        if (isPlaying) {
            sleepExpiredLockReleased = false
        }
        if (sleepExpiredLockReleased) {
            onKeepScreenOnChanged(false)
            return@LaunchedEffect
        }
        onKeepScreenOnChanged(true)
        onBrightnessChanged(brightnessMode.level)
        if (!isPlaying || song == null) {
            delay(120_000L)
            if (brightnessMode != ClockBrightnessMode.NIGHT) {
                onBrightnessChanged(0.06f)
            }
            delay(480_000L)
            val batteryStatus =
                try {
                    context.registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED))
                } catch (_: Exception) {
                    null
                }
            val plugged = batteryStatus?.getIntExtra(BatteryManager.EXTRA_PLUGGED, 0) ?: 0
            if (plugged == 0) {
                onKeepScreenOnChanged(false)
            }
        }
    }

    LaunchedEffect(isPlaying, song != null, lastInteractionMs) {
        if (isPlaying && song != null) {
            chromeVisible = true
            delay(10_000L)
            chromeVisible = false
        } else {
            chromeVisible = true
        }
    }

    val chromeAlpha by
        animateFloatAsState(
            targetValue = if (chromeVisible) 1f else 0f,
            animationSpec = tween(durationMillis = 450),
            label = "deskClockChromeAlpha",
        )
    val transportAlpha by
        animateFloatAsState(
            targetValue = if (chromeVisible) 1f else 0.16f,
            animationSpec = tween(durationMillis = 450),
            label = "deskClockTransportAlpha",
        )

    var nowMs by remember { mutableLongStateOf(System.currentTimeMillis()) }
    LaunchedEffect(Unit) {
        while (true) {
            val current = System.currentTimeMillis()
            nowMs = current
            val delayMs = (1_000L - (current % 1_000L)).coerceIn(50L, 1_000L)
            delay(delayMs)
        }
    }

    val secTick = nowMs / 1_000L
    val minuteTick = (nowMs / 60_000L).toInt()
    val is24Hour = DateFormat.is24HourFormat(context)
    val locale = Locale.getDefault()

    val hourFormat = remember(is24Hour, locale) { SimpleDateFormat(if (is24Hour) "HH" else "h", locale) }
    val minuteFormat = remember(locale) { SimpleDateFormat("mm", locale) }
    val amPmFormat = remember(is24Hour, locale) { if (is24Hour) null else SimpleDateFormat("a", locale) }
    val dateFormat =
        remember(locale) {
            SimpleDateFormat(DateFormat.getBestDateTimePattern(locale, "EEEEMMMMd"), locale)
        }

    val hoursText = remember(minuteTick, hourFormat) { hourFormat.format(Date(nowMs)) }
    val minutesText = remember(minuteTick, minuteFormat) { minuteFormat.format(Date(nowMs)) }
    val amPmText = remember(minuteTick, amPmFormat) { amPmFormat?.format(Date(nowMs)) }
    val secondsNum = remember(secTick) { ((secTick % 60L) + 60L).toInt() % 60 }
    val secondsText = remember(secondsNum) { String.format(Locale.US, "%02d", secondsNum) }
    val dateText = remember(minuteTick, dateFormat) { dateFormat.format(Date(nowMs)).uppercase(locale) }

    // Rotate offset every 60 seconds across a 16-point ±12dp orbit to mitigate OLED burn-in.
    val (shiftX, shiftY) = computePixelShiftDp(minuteTick)
    val accent = signalTierAccent(signalPath.qualityTier)
    val sleepBadgeText =
        when (sleepTimerState.mode) {
            SleepTimerMode.OFF -> null
            SleepTimerMode.COUNTDOWN -> {
                val sec = ((sleepTimerState.remainingMs + 999L) / 1000L).coerceAtLeast(0L)
                val h = sec / 3600L
                val m = (sec % 3600L) / 60L
                val s = sec % 60L
                if (h > 0L) String.format(Locale.US, "%d:%02d:%02d", h, m, s)
                else String.format(Locale.US, "%02d:%02d", m, s)
            }
            SleepTimerMode.END_OF_TRACK -> "TRACK"
            SleepTimerMode.END_OF_WORK -> "WORK"
        }

    BoxWithConstraints(
        modifier =
            Modifier.fillMaxSize()
                .background(Color.Black)
                .pointerInput(Unit) {
                    awaitPointerEventScope {
                        while (true) {
                            awaitPointerEvent(PointerEventPass.Initial)
                            lastInteractionMs = System.currentTimeMillis()
                            chromeVisible = true
                        }
                    }
                }
                .padding(horizontal = 24.dp, vertical = 20.dp)
    ) {
        val isWide = maxWidth >= 560.dp
        val isCompactHeight = maxHeight < 440.dp
        val isCompactPortrait = maxHeight < 660.dp
        val isScrollable = isCompactHeight || (!isWide && isCompactPortrait)

        val wideDigitSp =
            (minOf(maxWidth.value * 0.145f, maxHeight.value * 0.25f)).coerceIn(48f, 132f).sp
        val portraitDigitSp =
            (minOf(maxWidth.value * 0.21f, maxHeight.value * 0.16f)).coerceIn(48f, 112f).sp

        val scrollModifier =
            if (isScrollable) {
                Modifier.verticalScroll(rememberScrollState())
            } else {
                Modifier
            }

        Column(
            modifier = Modifier.fillMaxSize().then(scrollModifier).offset(x = shiftX, y = shiftY)
        ) {
            val bodyWeightModifier =
                if (isScrollable) Modifier.fillMaxWidth() else Modifier.fillMaxWidth().weight(1f)
            Box(modifier = Modifier.graphicsLayer { alpha = chromeAlpha }) {
                ClockTopBar(
                    signalPath = signalPath,
                    accent = accent,
                    brightnessLabel = brightnessMode.label,
                    sleepBadgeText = sleepBadgeText,
                    onCycleBrightness = {
                        sleepExpiredLockReleased = false
                        val entries = ClockBrightnessMode.entries
                        brightnessMode = entries[(brightnessMode.ordinal + 1) % entries.size]
                    },
                    onOpenSleepMenu = { showSleepMenu = true },
                    onDismiss = onDismiss,
                )
            }

            if (isWide) {
                Row(
                    modifier = bodyWeightModifier.padding(top = 12.dp),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(28.dp),
                ) {
                    Column(
                        modifier =
                            if (isScrollable) Modifier.weight(0.46f)
                            else Modifier.weight(0.46f).fillMaxHeight(),
                        verticalArrangement = Arrangement.Center,
                    ) {
                        ClockHeroTimepiece(
                            hours = hoursText,
                            minutes = minutesText,
                            seconds = secondsText,
                            secondsInt = secondsNum,
                            dateText = dateText,
                            amPm = amPmText,
                            digitSize = wideDigitSp,
                            compact = isCompactHeight,
                        )
                        Spacer(modifier = Modifier.height(14.dp))
                        Text(
                            text = stringResource(R.string.lbl_clock_keep_screen_on),
                            color = OledInkMuted,
                            fontSize = 11.sp,
                            fontFamily = FontFamily.Monospace,
                            letterSpacing = 0.6.sp,
                            modifier = Modifier.graphicsLayer { alpha = chromeAlpha },
                        )
                    }

                    ClockStudioDeckCard(
                        song = song,
                        isPlaying = isPlaying,
                        positionDsFlow = positionDsFlow,
                        waveformFlow = waveformFlow,
                        repeatMode = repeatMode,
                        isShuffled = isShuffled,
                        accent = accent,
                        compact = isCompactHeight,
                        transportAlpha = transportAlpha,
                        onSeekTo = onSeekTo,
                        onPrev = onPrev,
                        onPlayPause = onPlayPause,
                        onNext = onNext,
                        onToggleShuffle = onToggleShuffle,
                        onToggleRepeat = onToggleRepeat,
                        modifier = Modifier.weight(0.54f),
                    )
                }
            } else {
                Column(
                    modifier = bodyWeightModifier,
                    horizontalAlignment = Alignment.CenterHorizontally,
                ) {
                    if (isScrollable) {
                        Spacer(modifier = Modifier.height(16.dp))
                    } else {
                        Spacer(modifier = Modifier.weight(0.9f))
                    }
                    ClockHeroTimepiece(
                        hours = hoursText,
                        minutes = minutesText,
                        seconds = secondsText,
                        secondsInt = secondsNum,
                        dateText = dateText,
                        amPm = amPmText,
                        digitSize = portraitDigitSp,
                        centered = true,
                        compact = isCompactPortrait,
                    )
                    Spacer(modifier = Modifier.height(10.dp))
                    Text(
                        text = stringResource(R.string.lbl_clock_keep_screen_on),
                        color = OledInkMuted,
                        fontSize = 11.sp,
                        fontFamily = FontFamily.Monospace,
                        letterSpacing = 0.5.sp,
                        modifier = Modifier.graphicsLayer { alpha = chromeAlpha },
                    )
                    if (isScrollable) {
                        Spacer(modifier = Modifier.height(18.dp))
                    } else {
                        Spacer(modifier = Modifier.weight(1f))
                    }
                    ClockStudioDeckCard(
                        song = song,
                        isPlaying = isPlaying,
                        positionDsFlow = positionDsFlow,
                        waveformFlow = waveformFlow,
                        repeatMode = repeatMode,
                        isShuffled = isShuffled,
                        accent = accent,
                        compact = isCompactPortrait,
                        transportAlpha = transportAlpha,
                        onSeekTo = onSeekTo,
                        onPrev = onPrev,
                        onPlayPause = onPlayPause,
                        onNext = onNext,
                        onToggleShuffle = onToggleShuffle,
                        onToggleRepeat = onToggleRepeat,
                        modifier = Modifier.fillMaxWidth(),
                    )
                    Spacer(modifier = Modifier.height(8.dp))
                }
            }
        }

        if (showSleepMenu) {
            Box(
                modifier =
                    Modifier.fillMaxSize()
                        .background(Color(0xD9000000))
                        .clickable { showSleepMenu = false },
                contentAlignment = Alignment.Center,
            ) {
                Column(
                    modifier =
                        Modifier.width(260.dp)
                            .clip(RoundedCornerShape(18.dp))
                            .background(OledDeckSurface)
                            .border(1.dp, OledDeckBorder, RoundedCornerShape(18.dp))
                            .clickable(enabled = false) {}
                            .padding(16.dp),
                    verticalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    Text(
                        text = stringResource(R.string.lbl_sleep_timer),
                        color = OledBrass,
                        fontSize = 12.sp,
                        fontFamily = FontFamily.Monospace,
                        fontWeight = FontWeight.SemiBold,
                    )
                    val entries =
                        listOf<Pair<String, () -> Unit>>(
                            stringResource(R.string.lbl_sleep_off) to onCancelSleepTimer,
                            stringResource(R.string.lbl_sleep_15m) to { onSetSleepMinutes(15) },
                            stringResource(R.string.lbl_sleep_30m) to { onSetSleepMinutes(30) },
                            stringResource(R.string.lbl_sleep_45m) to { onSetSleepMinutes(45) },
                            stringResource(R.string.lbl_sleep_60m) to { onSetSleepMinutes(60) },
                            stringResource(R.string.lbl_sleep_end_track) to onSetSleepEndOfTrack,
                            stringResource(R.string.lbl_sleep_end_work) to onSetSleepEndOfWork,
                        )
                    for ((label, action) in entries) {
                        Box(
                            modifier =
                                Modifier.fillMaxWidth()
                                    .clip(RoundedCornerShape(10.dp))
                                    .background(Color(0xFF11141B))
                                    .clickable {
                                        sleepExpiredLockReleased = false
                                        action()
                                        showSleepMenu = false
                                    }
                                    .padding(horizontal = 12.dp, vertical = 9.dp)
                        ) {
                            Text(
                                text = label,
                                color = OledInkPrimary,
                                fontSize = 13.sp,
                            )
                        }
                    }
                }
            }
        }
    }
}

private val OledClockOrbit =
    listOf(
        0 to 0,
        6 to 0,
        12 to 4,
        10 to 10,
        4 to 12,
        -4 to 12,
        -10 to 10,
        -12 to 4,
        -12 to -4,
        -10 to -10,
        -4 to -12,
        4 to -12,
        10 to -10,
        12 to -4,
        6 to -6,
        -6 to 6,
    )

internal fun computePixelShiftDp(minuteTick: Int): Pair<Dp, Dp> {
    val size = OledClockOrbit.size
    val cycle = ((minuteTick % size) + size) % size
    val (dx, dy) = OledClockOrbit[cycle]
    return dx.dp to dy.dp
}

private fun signalTierAccent(tier: SignalQualityTier): Color =
    when (tier) {
        SignalQualityTier.BIT_PERFECT -> OledBrass
        SignalQualityTier.HIGH_RES_FLOAT -> Color(0xFF5CAEFF)
        SignalQualityTier.SYSTEM_MIXER -> Color(0xFF9AA3AF)
    }

@Composable
private fun ClockTopBar(
    signalPath: SignalPathState,
    accent: Color,
    brightnessLabel: String,
    sleepBadgeText: String?,
    onCycleBrightness: () -> Unit,
    onOpenSleepMenu: () -> Unit,
    onDismiss: () -> Unit,
) {
    val tierLabel =
        when (signalPath.qualityTier) {
            SignalQualityTier.BIT_PERFECT -> "BIT-PERFECT"
            SignalQualityTier.HIGH_RES_FLOAT -> "32F STUDIO"
            SignalQualityTier.SYSTEM_MIXER -> "SYS MIXER"
        }
    val rate =
        if (signalPath.sourceSampleRateHz > 0) {
            if (signalPath.sourceSampleRateHz % 1000 == 0) {
                "${signalPath.sourceSampleRateHz / 1000} kHz"
            } else {
                String.format(Locale.US, "%.1f kHz", signalPath.sourceSampleRateHz / 1000.0)
            }
        } else {
            null
        }
    val summary =
        listOfNotNull(tierLabel, signalPath.sourceCodec, rate).joinToString(" · ")

    Row(
        modifier = Modifier.fillMaxWidth(),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.SpaceBetween,
    ) {
        Row(
            modifier =
                Modifier.weight(1f, fill = false)
                    .clip(RoundedCornerShape(999.dp))
                    .background(OledDeckSurface)
                    .border(1.dp, OledDeckBorder, RoundedCornerShape(999.dp))
                    .padding(horizontal = 12.dp, vertical = 7.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            Box(modifier = Modifier.size(6.dp).clip(CircleShape).background(accent))
            Text(
                text = summary,
                color = OledInkPrimary,
                fontSize = 11.sp,
                fontFamily = FontFamily.Monospace,
                fontWeight = FontWeight.Medium,
                letterSpacing = 0.6.sp,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            Box(
                modifier =
                    Modifier.clip(RoundedCornerShape(4.dp))
                        .background(Color(0xFF14171E))
                        .padding(horizontal = 5.dp, vertical = 2.dp)
            ) {
                Text(
                    text = "OLED",
                    color = OledInkMuted,
                    fontSize = 9.sp,
                    fontFamily = FontFamily.Monospace,
                    letterSpacing = 0.8.sp,
                )
            }
        }

        Spacer(modifier = Modifier.width(8.dp))

        Row(
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            Box(
                modifier =
                    Modifier.clip(RoundedCornerShape(999.dp))
                        .background(OledDeckSurface)
                        .border(
                            1.dp,
                            if (sleepBadgeText != null) OledBrass.copy(alpha = 0.55f) else OledDeckBorder,
                            RoundedCornerShape(999.dp),
                        )
                        .clickable(onClick = onOpenSleepMenu)
                        .padding(horizontal = 10.dp, vertical = 7.dp)
            ) {
                Text(
                    text = sleepBadgeText ?: "SLEEP",
                    color = if (sleepBadgeText != null) OledBrass else OledInkSecondary,
                    fontSize = 10.sp,
                    fontFamily = FontFamily.Monospace,
                    fontWeight = FontWeight.Medium,
                    letterSpacing = 0.6.sp,
                )
            }

            Box(
                modifier =
                    Modifier.clip(RoundedCornerShape(999.dp))
                        .background(OledDeckSurface)
                        .border(1.dp, OledDeckBorder, RoundedCornerShape(999.dp))
                        .clickable(onClick = onCycleBrightness)
                        .padding(horizontal = 10.dp, vertical = 7.dp)
            ) {
                Text(
                    text = brightnessLabel,
                    color = OledInkSecondary,
                    fontSize = 10.sp,
                    fontFamily = FontFamily.Monospace,
                    fontWeight = FontWeight.Medium,
                    letterSpacing = 0.6.sp,
                )
            }

            IconButton(
                onClick = onDismiss,
                modifier =
                    Modifier.size(36.dp)
                        .clip(CircleShape)
                        .background(OledDeckSurface)
                        .border(1.dp, OledDeckBorder, CircleShape),
            ) {
                Icon(
                    painter = painterResource(R.drawable.ic_close_24),
                    contentDescription = stringResource(R.string.lbl_clock_close),
                    tint = OledInkSecondary,
                    modifier = Modifier.size(18.dp),
                )
            }
        }
    }
}

@Composable
private fun ClockHeroTimepiece(
    hours: String,
    minutes: String,
    seconds: String,
    secondsInt: Int,
    dateText: String,
    amPm: String? = null,
    digitSize: TextUnit? = null,
    centered: Boolean = false,
    compact: Boolean = false,
) {
    val resolvedDigitSize = digitSize ?: if (compact) 68.sp else 84.sp
    val subDialSize = (resolvedDigitSize.value * 0.27f).coerceIn(16f, 32f).sp
    val colonColor = if (secondsInt % 2 == 0) Color(0xFF9EA4AE) else Color(0xFF3E434C)
    val secondFraction = ((secondsInt + 1).coerceIn(1, 60)) / 60f

    Column(
        horizontalAlignment = if (centered) Alignment.CenterHorizontally else Alignment.Start,
        verticalArrangement = Arrangement.spacedBy(6.dp),
    ) {
        Text(
            text = dateText,
            color = OledInkSecondary,
            fontSize = 11.sp,
            fontFamily = FontFamily.Monospace,
            letterSpacing = 2.8.sp,
        )

        Row(verticalAlignment = Alignment.Bottom) {
            val digitStyle =
                TextStyle(
                    fontSize = resolvedDigitSize,
                    fontFamily = FontFamily.SansSerif,
                    fontWeight = FontWeight.ExtraLight,
                    letterSpacing = (-2.5).sp,
                    fontFeatureSettings = "tnum",
                    color = OledInkPrimary,
                )
            Text(text = hours, style = digitStyle)
            Text(
                text = ":",
                style = digitStyle.copy(color = colonColor, fontSize = resolvedDigitSize * 0.78f),
                modifier = Modifier.padding(horizontal = 4.dp, vertical = 8.dp),
            )
            Text(text = minutes, style = digitStyle)

            Spacer(modifier = Modifier.width(10.dp))

            Column(
                modifier = Modifier.padding(bottom = if (compact) 10.dp else 14.dp),
                verticalArrangement = Arrangement.spacedBy(4.dp),
            ) {
                if (!amPm.isNullOrBlank()) {
                    Text(
                        text = amPm,
                        style =
                            TextStyle(
                                color = OledInkMuted,
                                fontSize = 10.sp,
                                fontFamily = FontFamily.Monospace,
                                letterSpacing = 0.8.sp,
                            ),
                    )
                }
                Text(
                    text = seconds,
                    style =
                        TextStyle(
                            color = OledBrass,
                            fontSize = subDialSize,
                            fontFamily = FontFamily.Monospace,
                            fontWeight = FontWeight.Normal,
                            fontFeatureSettings = "tnum",
                            letterSpacing = 1.sp,
                        ),
                )
                Box(
                    modifier =
                        Modifier.width(34.dp)
                            .height(2.dp)
                            .clip(CircleShape)
                            .background(Color(0xFF1A1E26))
                ) {
                    Box(
                        modifier =
                            Modifier.fillMaxHeight()
                                .fillMaxWidth(secondFraction)
                                .background(OledBrass.copy(alpha = 0.85f))
                    )
                }
            }
        }
    }
}

@Composable
private fun ClockStudioDeckCard(
    song: Song?,
    isPlaying: Boolean,
    positionDsFlow: StateFlow<Long>,
    waveformFlow: StateFlow<FloatArray?>,
    repeatMode: RepeatMode,
    isShuffled: Boolean,
    accent: Color,
    compact: Boolean,
    transportAlpha: Float,
    onSeekTo: (Long) -> Unit,
    onPrev: () -> Unit,
    onPlayPause: () -> Unit,
    onNext: () -> Unit,
    onToggleShuffle: () -> Unit,
    onToggleRepeat: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val context = LocalContext.current
    val coverSize = if (compact) 76.dp else 92.dp
    val controlsEnabled = song != null

    Column(
        modifier =
            modifier
                .clip(RoundedCornerShape(22.dp))
                .background(OledDeckSurface)
                .border(1.dp, OledDeckBorder, RoundedCornerShape(22.dp))
                .padding(horizontal = 18.dp, vertical = if (compact) 14.dp else 18.dp),
        verticalArrangement = Arrangement.spacedBy(if (compact) 12.dp else 16.dp),
    ) {
        if (song != null) {
            val isClassical = !song.workName.isNullOrBlank()
            val sleeveCorner = if (isClassical) 4.dp else 10.dp

            Row(
                modifier = Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(14.dp),
            ) {
                Box(
                    modifier =
                        Modifier.size(coverSize)
                            .clip(RoundedCornerShape(sleeveCorner))
                            .border(1.dp, OledSleeveBorder, RoundedCornerShape(sleeveCorner))
                ) {
                    val bindKey = "${song.uid}|$isClassical"
                    AndroidView(
                        factory = { ctx -> CoverView(ctx) },
                        update = { view ->
                            if (view.tag != bindKey) {
                                view.tag = bindKey
                                view.bind(song, sharpSquare = isClassical)
                            }
                        },
                        modifier = Modifier.fillMaxSize(),
                    )
                }

                Column(
                    modifier = Modifier.weight(1f),
                    verticalArrangement = Arrangement.spacedBy(4.dp),
                ) {
                    val eyebrow =
                        if (isClassical) {
                            song.composers
                                .filter { it.isNotBlank() }
                                .distinct()
                                .joinToString(" · ")
                                .ifEmpty { song.album.name.resolve(context) }
                                .uppercase(Locale.getDefault())
                        } else {
                            song.album.name.resolve(context)
                        }
                    Text(
                        text = eyebrow,
                        color = if (isClassical) OledBrass else OledInkMuted,
                        fontSize = 11.sp,
                        fontFamily = if (isClassical) FontFamily.Monospace else FontFamily.Default,
                        fontWeight = FontWeight.Medium,
                        letterSpacing = if (isClassical) 0.9.sp else 0.sp,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )

                    val titleText =
                        if (isClassical) {
                            val rawMvt =
                                song.movementName?.trim()?.takeIf { it.isNotEmpty() }
                                    ?: song.name.resolve(context)
                            val mvtNum = song.movementNumber ?: 0
                            val mvtLabel =
                                if (mvtNum > 0 && !rawMvt.startsWith("$mvtNum.")) {
                                    "$mvtNum. $rawMvt"
                                } else {
                                    rawMvt
                                }
                            if (rawMvt.equals(song.workName, ignoreCase = true)) {
                                mvtLabel
                            } else {
                                "${song.workName} · $mvtLabel"
                            }
                        } else {
                            song.name.resolve(context)
                        }
                    Text(
                        text = titleText,
                        color = OledInkPrimary,
                        fontSize = if (compact) 15.sp else 17.sp,
                        lineHeight = if (compact) 20.sp else 22.sp,
                        fontFamily = if (isClassical) FontFamily.Serif else FontFamily.Default,
                        fontWeight = FontWeight.SemiBold,
                        maxLines = 2,
                        overflow = TextOverflow.Ellipsis,
                    )

                    val artistLine =
                        if (isClassical) {
                            val performers = song.artists.resolveNames(context)
                            val conductor =
                                song.conductors
                                    .firstOrNull()
                                    ?.takeIf {
                                        it.isNotBlank() && !performers.contains(it, ignoreCase = true)
                                    }
                            if (conductor != null && performers.isNotBlank()) {
                                "$conductor · $performers"
                            } else {
                                conductor ?: performers
                            }
                        } else {
                            song.artists.resolveNames(context)
                        }
                    Text(
                        text = artistLine,
                        color = OledInkSecondary,
                        fontSize = 12.5.sp,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                }
            }
        } else {
            Text(
                text = stringResource(R.string.lbl_playback),
                color = OledInkMuted,
                fontSize = 15.sp,
                modifier = Modifier.padding(vertical = 12.dp),
            )
        }

        ClockProgressSection(
            song = song,
            positionDsFlow = positionDsFlow,
            waveformFlow = waveformFlow,
            accent = accent,
            onSeekTo = onSeekTo,
        )

        // Balanced 5-button studio console transport row
        Row(
            modifier = Modifier.fillMaxWidth().graphicsLayer { alpha = transportAlpha },
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically,
        ) {
            ClockToggleButton(
                iconRes =
                    if (isShuffled) R.drawable.ic_shuffle_on_24 else R.drawable.ic_shuffle_off_24,
                contentDescription = stringResource(R.string.desc_shuffle),
                active = isShuffled,
                accent = accent,
                enabled = controlsEnabled,
                onClick = onToggleShuffle,
            )

            IconButton(
                onClick = onPrev,
                enabled = controlsEnabled,
                modifier = Modifier.size(46.dp),
            ) {
                Icon(
                    painter = painterResource(R.drawable.ic_skip_prev_24),
                    contentDescription = stringResource(R.string.desc_skip_prev),
                    tint = if (controlsEnabled) Color(0xFFDCDAD6) else OledInkMuted.copy(alpha = 0.45f),
                    modifier = Modifier.size(26.dp),
                )
            }

            IconButton(
                onClick = onPlayPause,
                enabled = controlsEnabled,
                modifier =
                    Modifier.size(58.dp)
                        .clip(CircleShape)
                        .background(if (isPlaying) Color(0xFF1B1F27) else Color(0xFF12151B))
                        .border(
                            1.dp,
                            if (isPlaying) accent.copy(alpha = 0.55f) else Color(0xFF323742),
                            CircleShape,
                        ),
            ) {
                Icon(
                    painter =
                        painterResource(
                            if (isPlaying) R.drawable.ic_pause_24 else R.drawable.ic_play_24
                        ),
                    contentDescription = stringResource(R.string.desc_play_pause),
                    tint = if (controlsEnabled) Color.White else OledInkMuted.copy(alpha = 0.45f),
                    modifier = Modifier.size(26.dp),
                )
            }

            IconButton(
                onClick = onNext,
                enabled = controlsEnabled,
                modifier = Modifier.size(46.dp),
            ) {
                Icon(
                    painter = painterResource(R.drawable.ic_skip_next_24),
                    contentDescription = stringResource(R.string.desc_skip_next),
                    tint = if (controlsEnabled) Color(0xFFDCDAD6) else OledInkMuted.copy(alpha = 0.45f),
                    modifier = Modifier.size(26.dp),
                )
            }

            ClockToggleButton(
                iconRes = repeatMode.icon,
                contentDescription = stringResource(R.string.desc_change_repeat),
                active = repeatMode != RepeatMode.NONE,
                accent = accent,
                enabled = controlsEnabled,
                onClick = onToggleRepeat,
            )
        }
    }
}

@Composable
private fun ClockProgressSection(
    song: Song?,
    positionDsFlow: StateFlow<Long>,
    waveformFlow: StateFlow<FloatArray?>,
    accent: Color,
    onSeekTo: (Long) -> Unit,
) {
    val positionDs by positionDsFlow.collectAsStateWithLifecycle()
    val waveform by waveformFlow.collectAsStateWithLifecycle()
    val durationMs = song?.durationMs ?: 0L
    val canSeek = song != null && durationMs > 0L
    val clampedPosMs = (positionDs * 100L).coerceIn(0L, durationMs.coerceAtLeast(0L))
    val baseProgress =
        if (durationMs > 0L) (clampedPosMs.toFloat() / durationMs.toFloat()).coerceIn(0f, 1f)
        else 0f
    var dragFraction by remember(song?.uid) { mutableStateOf<Float?>(null) }
    var dragPosPx by remember { mutableFloatStateOf(0f) }
    val progress = dragFraction ?: baseProgress
    val displayedDs =
        if (dragFraction != null && durationMs > 0L) {
            ((progress * durationMs) / 100f).toLong().coerceIn(0L, durationMs / 100L)
        } else {
            positionDs
        }

    fun fractionToDs(fraction: Float): Long =
        ((fraction.coerceIn(0f, 1f) * durationMs) / 100f).toLong().coerceIn(0L, durationMs / 100L)

    val scrubModifier =
        if (canSeek) {
            Modifier.pointerInput(song.uid, durationMs) {
                    detectTapGestures { offset ->
                        val frac = if (size.width > 0) (offset.x / size.width).coerceIn(0f, 1f) else 0f
                        onSeekTo(fractionToDs(frac))
                    }
                }
                .pointerInput(song.uid, durationMs) {
                    detectHorizontalDragGestures(
                        onDragStart = { start ->
                            dragPosPx = start.x
                            dragFraction =
                                if (size.width > 0) (start.x / size.width).coerceIn(0f, 1f) else 0f
                        },
                        onDragEnd = {
                            dragFraction?.let { frac -> onSeekTo(fractionToDs(frac)) }
                            dragFraction = null
                        },
                        onDragCancel = { dragFraction = null },
                        onHorizontalDrag = { change, dragAmount ->
                            change.consume()
                            dragPosPx = (dragPosPx + dragAmount).coerceIn(0f, size.width.toFloat())
                            dragFraction =
                                if (size.width > 0) (dragPosPx / size.width).coerceIn(0f, 1f) else 0f
                        },
                    )
                }
        } else {
            Modifier
        }

    // Interactive 150-pt waveform / hairline timeline isolated from album cover recompositions
    Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
        val bins = waveform
        if (bins != null && bins.isNotEmpty() && canSeek) {
            Canvas(modifier = Modifier.fillMaxWidth().height(28.dp).then(scrubModifier)) {
                val barCount = bins.size.coerceAtMost(150)
                val step = if (barCount > 0) size.width / barCount.toFloat() else size.width
                val barWidth = (step * 0.62f).coerceAtLeast(1.5f)
                val radius = CornerRadius(barWidth / 2f, barWidth / 2f)
                val playedColor = accent
                val unplayedColor = Color(0xFF232833)
                for (i in 0 until barCount) {
                    val sampleIdx =
                        if (bins.size == barCount) i else (i * bins.size) / barCount
                    val amp = bins[sampleIdx].coerceIn(0.10f, 1f)
                    val barHeight = (size.height * amp).coerceAtLeast(3f)
                    val x = i * step + (step - barWidth) / 2f
                    val y = (size.height - barHeight) / 2f
                    val barFrac = (i + 0.5f) / barCount.toFloat()
                    drawRoundRect(
                        color = if (barFrac <= progress) playedColor else unplayedColor,
                        topLeft = Offset(x, y),
                        size = Size(barWidth, barHeight),
                        cornerRadius = radius,
                    )
                }
            }
        } else {
            Box(
                modifier = Modifier.fillMaxWidth().height(20.dp).then(scrubModifier),
                contentAlignment = Alignment.CenterStart,
            ) {
                Box(
                    modifier =
                        Modifier.fillMaxWidth()
                            .height(3.dp)
                            .clip(CircleShape)
                            .background(Color(0xFF1A1E26))
                ) {
                    Box(
                        modifier =
                            Modifier.fillMaxHeight()
                                .fillMaxWidth(progress)
                                .clip(CircleShape)
                                .background(accent)
                    )
                }
            }
        }

        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.SpaceBetween,
        ) {
            val timeStyle =
                TextStyle(
                    color = if (dragFraction != null) OledBrass else OledInkMuted,
                    fontSize = 11.sp,
                    fontFamily = FontFamily.Monospace,
                    fontFeatureSettings = "tnum",
                )
            Text(text = displayedDs.formatDurationDs(true), style = timeStyle)
            Text(text = durationMs.formatDurationMs(false), style = timeStyle)
        }
    }
}

@Composable
private fun ClockToggleButton(
    iconRes: Int,
    contentDescription: String,
    active: Boolean,
    accent: Color,
    enabled: Boolean = true,
    onClick: () -> Unit,
) {
    IconButton(
        onClick = onClick,
        enabled = enabled,
        modifier =
            Modifier.size(38.dp)
                .clip(CircleShape)
                .then(
                    if (active && enabled) {
                        Modifier.background(accent.copy(alpha = 0.14f))
                            .border(1.dp, accent.copy(alpha = 0.4f), CircleShape)
                    } else {
                        Modifier
                    }
                ),
    ) {
        Icon(
            painter = painterResource(iconRes),
            contentDescription = contentDescription,
            tint =
                when {
                    !enabled -> OledInkMuted.copy(alpha = 0.4f)
                    active -> accent
                    else -> OledInkMuted
                },
            modifier = Modifier.size(19.dp),
        )
    }
}
