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
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.systemBarsPadding
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
import dagger.hilt.android.AndroidEntryPoint
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import kotlinx.coroutines.delay
import com.shawnshi.tonewise.R
import com.shawnshi.tonewise.image.CoverView
import com.shawnshi.tonewise.music.resolve
import com.shawnshi.tonewise.music.resolveNames
import com.shawnshi.tonewise.playback.PlaybackViewModel
import com.shawnshi.tonewise.playback.audio.SignalPathState
import com.shawnshi.tonewise.playback.audio.SignalQualityTier
import org.oxycblt.musikr.Song

/**
 * Pure-black (`#000000`) OLED desk clock display with cyclic 60-second `±2dp` pixel-shift burn-in
 * protection, Classical serif typography, 1:1 album sleeve, and live [SignalQualityTier] pill.
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

                MaterialTheme(colorScheme = darkColorScheme(background = Color.Black, surface = Color.Black)) {
                    DeskClockScreen(
                        song = song,
                        isPlaying = isPlaying,
                        signalPath = signalPath,
                        onPrev = { playbackModel.prev() },
                        onPlayPause = { playbackModel.togglePlaying() },
                        onNext = { playbackModel.next() },
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

@Composable
private fun DeskClockScreen(
    song: Song?,
    isPlaying: Boolean,
    signalPath: SignalPathState,
    onPrev: () -> Unit,
    onPlayPause: () -> Unit,
    onNext: () -> Unit,
    onDismiss: () -> Unit,
) {
    var nowMs by remember { mutableLongStateOf(System.currentTimeMillis()) }
    LaunchedEffect(Unit) {
        while (true) {
            nowMs = System.currentTimeMillis()
            delay(1_000L)
        }
    }

    val timeText =
        remember(nowMs / 1_000L) {
            SimpleDateFormat("HH:mm:ss", Locale.US).format(Date(nowMs))
        }
    val dateText =
        remember(nowMs / 60_000L) {
            SimpleDateFormat("EEE · MMM d", Locale.US).format(Date(nowMs))
        }

    // Rotate offset every 60 seconds across a 4-corner ±2dp pattern to mitigate OLED burn-in.
    val (shiftX, shiftY) = computePixelShiftDp((nowMs / 60_000L).toInt())

    BoxWithConstraints(
        modifier = Modifier.fillMaxSize().background(Color.Black).systemBarsPadding().padding(20.dp)
    ) {
        val isWide = maxWidth >= 560.dp

        IconButton(
            onClick = onDismiss,
            modifier = Modifier.align(Alignment.TopEnd).offset(x = shiftX, y = shiftY),
        ) {
            Icon(
                painter = painterResource(R.drawable.ic_close_24),
                contentDescription = stringResource(R.string.lbl_clock_close),
                tint = Color(0xFFB8B8B8),
            )
        }

        if (isWide) {
            Row(
                modifier =
                    Modifier.fillMaxSize()
                        .offset(x = shiftX, y = shiftY)
                        .padding(horizontal = 16.dp),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(28.dp),
            ) {
                Column(
                    modifier = Modifier.weight(1f),
                    verticalArrangement = Arrangement.spacedBy(10.dp),
                ) {
                    ClockTimeHeader(timeText = timeText, dateText = dateText)
                    ClockSignalPill(signalPath = signalPath)
                    Text(
                        text = stringResource(R.string.lbl_clock_keep_screen_on),
                        color = Color(0xFF666666),
                        fontSize = 11.sp,
                        fontFamily = FontFamily.Monospace,
                    )
                }

                ClockTrackPanel(
                    song = song,
                    isPlaying = isPlaying,
                    onPrev = onPrev,
                    onPlayPause = onPlayPause,
                    onNext = onNext,
                    modifier = Modifier.weight(1f),
                )
            }
        } else {
            Column(
                modifier = Modifier.fillMaxSize().offset(x = shiftX, y = shiftY),
                verticalArrangement = Arrangement.SpaceEvenly,
                horizontalAlignment = Alignment.CenterHorizontally,
            ) {
                ClockTimeHeader(timeText = timeText, dateText = dateText)
                ClockSignalPill(signalPath = signalPath)
                ClockTrackPanel(
                    song = song,
                    isPlaying = isPlaying,
                    onPrev = onPrev,
                    onPlayPause = onPlayPause,
                    onNext = onNext,
                    modifier = Modifier.fillMaxWidth(),
                )
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

@Composable
private fun ClockTimeHeader(timeText: String, dateText: String) {
    Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
        Text(
            text = timeText,
            color = Color(0xFFE6E6E6),
            fontSize = 48.sp,
            fontFamily = FontFamily.Monospace,
            fontWeight = FontWeight.Light,
            letterSpacing = 2.sp,
        )
        Text(
            text = dateText,
            color = Color(0xFF9A9A9A),
            fontSize = 14.sp,
            fontFamily = FontFamily.Monospace,
        )
    }
}

@Composable
private fun ClockSignalPill(signalPath: SignalPathState) {
    val (tierLabel, accent) =
        when (signalPath.qualityTier) {
            SignalQualityTier.BIT_PERFECT -> "BIT-PERFECT" to Color(0xFF2EC27E)
            SignalQualityTier.HIGH_RES_FLOAT -> "HI-RES FLOAT" to Color(0xFF4EA8DE)
            SignalQualityTier.SYSTEM_MIXER -> "SYS MIXER" to Color(0xFFE5A50A)
        }
    val rate =
        if (signalPath.sourceSampleRateHz > 0) {
            String.format(Locale.US, "%.1f kHz", signalPath.sourceSampleRateHz / 1000.0)
        } else {
            "-- kHz"
        }
    val label =
        listOfNotNull(
                signalPath.sourceCodec,
                rate,
                signalPath.outputDeviceName,
                tierLabel,
            )
            .joinToString(" · ")

    Box(
        modifier =
            Modifier.clip(RoundedCornerShape(999.dp))
                .background(accent.copy(alpha = 0.14f))
                .border(1.dp, accent.copy(alpha = 0.55f), RoundedCornerShape(999.dp))
                .padding(horizontal = 12.dp, vertical = 5.dp)
    ) {
        Text(
            text = label,
            color = accent,
            fontSize = 11.sp,
            fontFamily = FontFamily.Monospace,
            fontWeight = FontWeight.SemiBold,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
    }
}

@Composable
private fun ClockTrackPanel(
    song: Song?,
    isPlaying: Boolean,
    onPrev: () -> Unit,
    onPlayPause: () -> Unit,
    onNext: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val context = LocalContext.current
    Column(
        modifier = modifier,
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        if (song != null) {
            val isClassical = !song.workName.isNullOrBlank()
            AndroidView(
                factory = { ctx -> CoverView(ctx) },
                update = { view -> view.bind(song, sharpSquare = isClassical) },
                modifier = Modifier.size(144.dp),
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
                color = Color(0xFFEDEDED),
                fontSize = 18.sp,
                fontFamily = if (isClassical) FontFamily.Serif else FontFamily.Default,
                fontWeight = FontWeight.SemiBold,
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
            )
            Text(
                text = "${song.artists.resolveNames(context)} · ${song.album.name.resolve(context)}",
                color = Color(0xFF9E9E9E),
                fontSize = 13.sp,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        } else {
            Text(
                text = stringResource(R.string.lbl_playback),
                color = Color(0xFF777777),
                fontSize = 16.sp,
            )
        }

        Spacer(modifier = Modifier.height(4.dp))

        Row(
            horizontalArrangement = Arrangement.spacedBy(20.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            IconButton(onClick = onPrev) {
                Icon(
                    painter = painterResource(R.drawable.ic_skip_prev_24),
                    contentDescription = stringResource(R.string.desc_skip_prev),
                    tint = Color(0xFFD6D6D6),
                )
            }
            IconButton(
                onClick = onPlayPause,
                modifier =
                    Modifier.size(56.dp)
                        .clip(CircleShape)
                        .background(Color(0xFF1F1F1F))
                        .border(1.dp, Color(0xFF424242), CircleShape),
            ) {
                Icon(
                    painter =
                        painterResource(
                            if (isPlaying) R.drawable.ic_pause_24 else R.drawable.ic_play_24
                        ),
                    contentDescription = stringResource(R.string.desc_play_pause),
                    tint = Color(0xFFFFFFFF),
                )
            }
            IconButton(onClick = onNext) {
                Icon(
                    painter = painterResource(R.drawable.ic_skip_next_24),
                    contentDescription = stringResource(R.string.desc_skip_next),
                    tint = Color(0xFFD6D6D6),
                )
            }
        }
    }
}
