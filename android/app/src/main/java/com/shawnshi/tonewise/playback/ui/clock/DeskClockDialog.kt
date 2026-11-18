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

import android.os.Bundle
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.view.WindowManager
import androidx.compose.foundation.background
import androidx.compose.foundation.border
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
import androidx.compose.foundation.layout.systemBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
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
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.fragment.app.DialogFragment
import androidx.fragment.app.activityViewModels
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.shawnshi.tonewise.R
import com.shawnshi.tonewise.image.CoverView
import com.shawnshi.tonewise.music.resolve
import com.shawnshi.tonewise.music.resolveNames
import com.shawnshi.tonewise.playback.PlaybackViewModel
import com.shawnshi.tonewise.playback.audio.SignalPathState
import com.shawnshi.tonewise.playback.audio.SignalQualityTier
import com.shawnshi.tonewise.playback.formatDurationDs
import com.shawnshi.tonewise.playback.formatDurationMs
import com.shawnshi.tonewise.playback.state.RepeatMode
import dagger.hilt.android.AndroidEntryPoint
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import kotlinx.coroutines.delay
import org.oxycblt.musikr.Song

/**
 * Pure-black (`#000000`) OLED Braun/Nagra studio chronometer & reference monitor with cyclic
 * 60-second `±2dp` pixel-shift burn-in protection, live hardware telemetry, and framed track deck.
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
        dialog?.window?.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
    }

    override fun onStop() {
        dialog?.window?.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        super.onStop()
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
                val positionDs by playbackModel.positionDs.collectAsStateWithLifecycle()
                val repeatMode by playbackModel.repeatMode.collectAsStateWithLifecycle()
                val isShuffled by playbackModel.isShuffled.collectAsStateWithLifecycle()

                MaterialTheme(
                    colorScheme = darkColorScheme(background = Color.Black, surface = Color.Black)
                ) {
                    DeskClockScreen(
                        song = song,
                        isPlaying = isPlaying,
                        positionDs = positionDs,
                        repeatMode = repeatMode,
                        isShuffled = isShuffled,
                        signalPath = signalPath,
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
    positionDs: Long,
    repeatMode: RepeatMode,
    isShuffled: Boolean,
    signalPath: SignalPathState,
    onPrev: () -> Unit,
    onPlayPause: () -> Unit,
    onNext: () -> Unit,
    onToggleShuffle: () -> Unit,
    onToggleRepeat: () -> Unit,
    onDismiss: () -> Unit,
) {
    var nowMs by remember { mutableLongStateOf(System.currentTimeMillis()) }
    LaunchedEffect(Unit) {
        while (true) {
            nowMs = System.currentTimeMillis()
            delay(1_000L)
        }
    }

    val secTick = nowMs / 1_000L
    val minuteTick = (nowMs / 60_000L).toInt()

    val hoursText = remember(minuteTick) { SimpleDateFormat("HH", Locale.US).format(Date(nowMs)) }
    val minutesText =
        remember(minuteTick) { SimpleDateFormat("mm", Locale.US).format(Date(nowMs)) }
    val secondsNum =
        remember(secTick) {
            SimpleDateFormat("ss", Locale.US).format(Date(nowMs)).toIntOrNull() ?: 0
        }
    val secondsText = remember(secondsNum) { String.format(Locale.US, "%02d", secondsNum) }
    val dateText =
        remember(minuteTick) {
            SimpleDateFormat("EEEE · MMM d", Locale.US).format(Date(nowMs)).uppercase(Locale.US)
        }

    // Rotate offset every 60 seconds across a 4-corner ±2dp pattern to mitigate OLED burn-in.
    val (shiftX, shiftY) = computePixelShiftDp(minuteTick)
    val accent = signalTierAccent(signalPath.qualityTier)

    BoxWithConstraints(
        modifier =
            Modifier.fillMaxSize()
                .background(Color.Black)
                .systemBarsPadding()
                .padding(horizontal = 20.dp, vertical = 16.dp)
    ) {
        val isWide = maxWidth >= 560.dp
        val isCompactHeight = maxHeight < 440.dp
        val isCompactPortrait = maxHeight < 660.dp

        Column(modifier = Modifier.fillMaxSize().offset(x = shiftX, y = shiftY)) {
            ClockTopBar(signalPath = signalPath, accent = accent, onDismiss = onDismiss)

            if (isWide) {
                Row(
                    modifier = Modifier.fillMaxWidth().weight(1f).padding(top = 12.dp),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(28.dp),
                ) {
                    Column(
                        modifier = Modifier.weight(0.46f).fillMaxHeight(),
                        verticalArrangement = Arrangement.Center,
                    ) {
                        ClockHeroTimepiece(
                            hours = hoursText,
                            minutes = minutesText,
                            seconds = secondsText,
                            secondsInt = secondsNum,
                            dateText = dateText,
                            compact = isCompactHeight,
                        )
                        Spacer(modifier = Modifier.height(14.dp))
                        Text(
                            text = stringResource(R.string.lbl_clock_keep_screen_on),
                            color = OledInkMuted,
                            fontSize = 11.sp,
                            fontFamily = FontFamily.Monospace,
                            letterSpacing = 0.6.sp,
                        )
                    }

                    ClockStudioDeckCard(
                        song = song,
                        isPlaying = isPlaying,
                        positionDs = positionDs,
                        repeatMode = repeatMode,
                        isShuffled = isShuffled,
                        accent = accent,
                        compact = isCompactHeight,
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
                    modifier = Modifier.fillMaxWidth().weight(1f),
                    horizontalAlignment = Alignment.CenterHorizontally,
                ) {
                    Spacer(modifier = Modifier.weight(0.9f))
                    ClockHeroTimepiece(
                        hours = hoursText,
                        minutes = minutesText,
                        seconds = secondsText,
                        secondsInt = secondsNum,
                        dateText = dateText,
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
                    )
                    Spacer(modifier = Modifier.weight(1f))
                    ClockStudioDeckCard(
                        song = song,
                        isPlaying = isPlaying,
                        positionDs = positionDs,
                        repeatMode = repeatMode,
                        isShuffled = isShuffled,
                        accent = accent,
                        compact = isCompactPortrait,
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
    }
}

internal fun computePixelShiftDp(minuteTick: Int): Pair<Dp, Dp> {
    val cycle = ((minuteTick % 4) + 4) % 4
    return when (cycle) {
        0 -> (-2).dp to (-2).dp
        1 -> 2.dp to (-2).dp
        2 -> 2.dp to 2.dp
        else -> (-2).dp to 2.dp
    }
}

private fun signalTierAccent(tier: SignalQualityTier): Color =
    when (tier) {
        SignalQualityTier.BIT_PERFECT -> OledBrass
        SignalQualityTier.HIGH_RES_FLOAT -> Color(0xFF5CAEFF)
        SignalQualityTier.SYSTEM_MIXER -> Color(0xFF9AA3AF)
    }

@Composable
private fun ClockTopBar(signalPath: SignalPathState, accent: Color, onDismiss: () -> Unit) {
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

        Spacer(modifier = Modifier.width(12.dp))

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

@Composable
private fun ClockHeroTimepiece(
    hours: String,
    minutes: String,
    seconds: String,
    secondsInt: Int,
    dateText: String,
    centered: Boolean = false,
    compact: Boolean = false,
) {
    val digitSize = if (compact) 68.sp else 84.sp
    val subDialSize = if (compact) 19.sp else 22.sp
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
                    fontSize = digitSize,
                    fontFamily = FontFamily.SansSerif,
                    fontWeight = FontWeight.ExtraLight,
                    letterSpacing = (-2.5).sp,
                    fontFeatureSettings = "tnum",
                    color = OledInkPrimary,
                )
            Text(text = hours, style = digitStyle)
            Text(
                text = ":",
                style = digitStyle.copy(color = colonColor, fontSize = digitSize * 0.78f),
                modifier = Modifier.padding(horizontal = 4.dp, vertical = 8.dp),
            )
            Text(text = minutes, style = digitStyle)

            Spacer(modifier = Modifier.width(10.dp))

            Column(
                modifier = Modifier.padding(bottom = if (compact) 11.dp else 14.dp),
                verticalArrangement = Arrangement.spacedBy(5.dp),
            ) {
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
    positionDs: Long,
    repeatMode: RepeatMode,
    isShuffled: Boolean,
    accent: Color,
    compact: Boolean,
    onPrev: () -> Unit,
    onPlayPause: () -> Unit,
    onNext: () -> Unit,
    onToggleShuffle: () -> Unit,
    onToggleRepeat: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val context = LocalContext.current
    val coverSize = if (compact) 76.dp else 92.dp
    val durationMs = song?.durationMs ?: 0L
    val clampedPosMs = (positionDs * 100L).coerceIn(0L, durationMs.coerceAtLeast(0L))
    val progress =
        if (durationMs > 0L) (clampedPosMs.toFloat() / durationMs.toFloat()).coerceIn(0f, 1f)
        else 0f

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
                    AndroidView(
                        factory = { ctx -> CoverView(ctx) },
                        update = { view -> view.bind(song, sharpSquare = isClassical) },
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
                                .uppercase(Locale.US)
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
                            val mvt = song.movementName ?: song.name.resolve(context)
                            if (mvt.equals(song.workName, ignoreCase = true)) {
                                song.workName!!
                            } else {
                                "${song.workName} · $mvt"
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
                            val conductor = song.conductors.firstOrNull()?.takeIf { it.isNotBlank() }
                            val performers = song.artists.resolveNames(context)
                            if (conductor != null) "$conductor · $performers" else performers
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

        // Hairline timeline & tabular timecodes
        Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
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
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
            ) {
                val timeStyle =
                    TextStyle(
                        color = OledInkMuted,
                        fontSize = 11.sp,
                        fontFamily = FontFamily.Monospace,
                        fontFeatureSettings = "tnum",
                    )
                Text(text = positionDs.formatDurationDs(true), style = timeStyle)
                Text(text = durationMs.formatDurationMs(false), style = timeStyle)
            }
        }

        // Balanced 5-button studio console transport row
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically,
        ) {
            ClockToggleButton(
                iconRes =
                    if (isShuffled) R.drawable.ic_shuffle_on_24 else R.drawable.ic_shuffle_off_24,
                contentDescription = stringResource(R.string.desc_shuffle),
                active = isShuffled,
                accent = accent,
                onClick = onToggleShuffle,
            )

            IconButton(onClick = onPrev, modifier = Modifier.size(46.dp)) {
                Icon(
                    painter = painterResource(R.drawable.ic_skip_prev_24),
                    contentDescription = stringResource(R.string.desc_skip_prev),
                    tint = Color(0xFFDCDAD6),
                    modifier = Modifier.size(26.dp),
                )
            }

            IconButton(
                onClick = onPlayPause,
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
                    tint = Color.White,
                    modifier = Modifier.size(26.dp),
                )
            }

            IconButton(onClick = onNext, modifier = Modifier.size(46.dp)) {
                Icon(
                    painter = painterResource(R.drawable.ic_skip_next_24),
                    contentDescription = stringResource(R.string.desc_skip_next),
                    tint = Color(0xFFDCDAD6),
                    modifier = Modifier.size(26.dp),
                )
            }

            ClockToggleButton(
                iconRes = repeatMode.icon,
                contentDescription = stringResource(R.string.desc_change_repeat),
                active = repeatMode != RepeatMode.NONE,
                accent = accent,
                onClick = onToggleRepeat,
            )
        }
    }
}

@Composable
private fun ClockToggleButton(
    iconRes: Int,
    contentDescription: String,
    active: Boolean,
    accent: Color,
    onClick: () -> Unit,
) {
    IconButton(
        onClick = onClick,
        modifier =
            Modifier.size(38.dp)
                .clip(CircleShape)
                .then(
                    if (active) {
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
            tint = if (active) accent else OledInkMuted,
            modifier = Modifier.size(19.dp),
        )
    }
}
