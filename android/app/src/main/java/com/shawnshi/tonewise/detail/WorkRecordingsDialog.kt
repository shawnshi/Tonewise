/*
 * Copyright (c) 2026 Tonewise Project
 * WorkRecordingsDialog.kt is part of Tonewise.
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

package com.shawnshi.tonewise.detail

import android.os.Build
import android.os.Bundle
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import androidx.compose.foundation.clickable
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
import androidx.compose.material3.AssistChip
import androidx.compose.material3.ElevatedCard
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
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
import com.shawnshi.tonewise.music.ClassicalWork
import com.shawnshi.tonewise.music.MusicRepository
import com.shawnshi.tonewise.music.WorkRecording
import com.shawnshi.tonewise.music.resolve
import com.shawnshi.tonewise.playback.PlaybackViewModel
import com.shawnshi.tonewise.playback.formatDurationMs
import com.shawnshi.tonewise.playback.replaygain.ReplayGainAudioProcessor
import com.shawnshi.tonewise.ui.TonewiseComposeTheme
import kotlin.math.roundToLong
import org.oxycblt.musikr.Song

@AndroidEntryPoint
class WorkRecordingsDialog : BackportBottomSheetDialogFragment() {
    private val playbackModel: PlaybackViewModel by activityViewModels()
    private val detailModel: DetailViewModel by activityViewModels()
    @Inject lateinit var musicRepository: MusicRepository

    override fun onCreateView(
        inflater: LayoutInflater,
        container: ViewGroup?,
        savedInstanceState: Bundle?,
    ): View {
        return ComposeView(requireContext()).apply {
            setViewCompositionStrategy(ViewCompositionStrategy.DisposeOnViewTreeLifecycleDestroyed)
            setContent {
                val song by playbackModel.song.collectAsStateWithLifecycle()
                val work = song?.let(musicRepository::findWorkForSong)

                TonewiseComposeTheme {
                    WorkRecordingsSheet(
                        currentSong = song,
                        work = work,
                        onSelectRecording = { recording ->
                            switchRecordingPreservingMovement(song, recording)
                        },
                        onSelectComposer = { composer ->
                            dismiss()
                            detailModel.showComposer(composer)
                        },
                        onSelectConductor = { conductor ->
                            dismiss()
                            detailModel.showConductor(conductor)
                        },
                        onOpenBooklet = {
                            val fm = parentFragmentManager
                            dismiss()
                            BookletPdfDialog.newInstance().show(fm, BookletPdfDialog.TAG)
                        },
                    )
                }
            }
        }
    }

    private fun switchRecordingPreservingMovement(currentSong: Song?, recording: WorkRecording) {
        if (recording.movements.isEmpty()) return
        val targetIndex =
            if (currentSong == null) {
                0
            } else {
                val byNumber =
                    currentSong.movementNumber?.let { num ->
                        recording.movements.indexOfFirst { it.movementNumber == num }
                    } ?: -1
                val byName =
                    currentSong.movementName?.let { mName ->
                        recording.movements.indexOfFirst {
                            it.movementName?.equals(mName, ignoreCase = true) == true
                        }
                    } ?: -1
                when {
                    byNumber >= 0 -> byNumber
                    byName >= 0 -> byName
                    else -> 0
                }
            }
        val targetMovement = recording.movements[targetIndex]
        val currentPosMs = playbackModel.positionDs.value * 100L
        val safeSeekMs =
            computeProportionalSeekMs(
                currentPosMs = currentPosMs,
                currentDurationMs = currentSong?.durationMs ?: 0L,
                targetDurationMs = targetMovement.durationMs,
            )
        val pq = playbackModel.pagerQueue.value
        val (updatedQueue, newIndex) =
            replaceWorkBlockInQueue(
                currentQueue = pq.queue,
                currentIndex = pq.index,
                targetMovements = recording.movements,
                targetOffsetInMovements = targetIndex,
            )
        playbackModel.restoreSessionQueue(updatedQueue, newIndex, safeSeekMs)
        dismiss()
    }

    companion object {
        const val TAG = "WorkRecordingsDialog"

        fun newInstance() = WorkRecordingsDialog()

        internal fun computeProportionalSeekMs(
            currentPosMs: Long,
            currentDurationMs: Long,
            targetDurationMs: Long,
        ): Long {
            if (currentPosMs <= 0L || currentDurationMs <= 0L || targetDurationMs <= 0L) return 0L
            val ratio = (currentPosMs.toDouble() / currentDurationMs.toDouble()).coerceIn(0.0, 1.0)
            return (targetDurationMs.toDouble() * ratio)
                .roundToLong()
                .coerceIn(0L, (targetDurationMs - 1000L).coerceAtLeast(0L))
        }

        internal fun replaceWorkBlockInQueue(
            currentQueue: List<Song>,
            currentIndex: Int,
            targetMovements: List<Song>,
            targetOffsetInMovements: Int,
        ): Pair<List<Song>, Int> {
            if (targetMovements.isEmpty()) {
                return currentQueue to currentIndex.coerceIn(0, (currentQueue.size - 1).coerceAtLeast(0))
            }
            val safeOffset = targetOffsetInMovements.coerceIn(0, targetMovements.lastIndex)
            if (currentQueue.isEmpty() || currentIndex !in currentQueue.indices) {
                return targetMovements to safeOffset
            }

            val curSong = currentQueue[currentIndex]
            val curWorkKey = ReplayGainAudioProcessor.buildWorkBlockKey(curSong)
            var blockStart = currentIndex
            var blockEnd = currentIndex
            if (curWorkKey != null) {
                while (blockStart > 0 &&
                    ReplayGainAudioProcessor.buildWorkBlockKey(currentQueue[blockStart - 1]) == curWorkKey
                ) {
                    blockStart--
                }
                while (blockEnd + 1 < currentQueue.size &&
                    ReplayGainAudioProcessor.buildWorkBlockKey(currentQueue[blockEnd + 1]) == curWorkKey
                ) {
                    blockEnd++
                }
            }

            val updated =
                ArrayList<Song>(currentQueue.size - (blockEnd - blockStart + 1) + targetMovements.size)
            for (i in 0 until blockStart) updated.add(currentQueue[i])
            updated.addAll(targetMovements)
            for (i in (blockEnd + 1) until currentQueue.size) updated.add(currentQueue[i])
            return updated to (blockStart + safeOffset)
        }
    }
}

@Composable
private fun WorkRecordingsSheet(
    currentSong: Song?,
    work: ClassicalWork?,
    onSelectRecording: (WorkRecording) -> Unit,
    onSelectComposer: (String) -> Unit,
    onSelectConductor: (String) -> Unit,
    onOpenBooklet: () -> Unit,
) {
    val context = LocalContext.current
    Surface(modifier = Modifier.fillMaxWidth(), color = MaterialTheme.colorScheme.surface) {
        Column(
            modifier =
                Modifier.fillMaxWidth()
                    .verticalScroll(rememberScrollState())
                    .navigationBarsPadding()
                    .padding(20.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            if (work == null) {
                Text(
                    text = stringResource(R.string.lbl_work_none),
                    style = MaterialTheme.typography.titleMedium,
                )
                OutlinedButton(
                    onClick = onOpenBooklet,
                    modifier = Modifier.fillMaxWidth(),
                ) {
                    Text(stringResource(R.string.lbl_booklet_pdf))
                }
                return@Column
            }

            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(
                    text = work.composerName,
                    style = MaterialTheme.typography.labelLarge,
                    color = MaterialTheme.colorScheme.primary,
                    modifier = Modifier.weight(1f).clickable { onSelectComposer(work.composerName) },
                )
                OutlinedButton(onClick = onOpenBooklet) {
                    Text(stringResource(R.string.lbl_booklet_pdf))
                }
            }
            Text(
                text = work.workTitle,
                style = MaterialTheme.typography.headlineSmall,
                fontFamily = FontFamily.Serif,
                fontWeight = FontWeight.SemiBold,
            )
            Text(
                text = stringResource(R.string.fmt_work_recordings_count, work.recordings.size),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )

            for (recording in work.recordings) {
                val isCurrent = currentSong?.album?.uid == recording.album.uid
                val leadSong = recording.movements.firstOrNull()
                val rateLabel =
                    leadSong?.let {
                        String.format(Locale.US, "%.1f kHz", it.sampleRateHz / 1000.0)
                    } ?: ""
                val codecLabel = leadSong?.format?.resolve(context) ?: ""
                ElevatedCard(
                    modifier = Modifier.fillMaxWidth().clickable { onSelectRecording(recording) },
                    shape = RoundedCornerShape(14.dp),
                ) {
                    Column(
                        modifier = Modifier.fillMaxWidth().padding(14.dp),
                        verticalArrangement = Arrangement.spacedBy(4.dp),
                    ) {
                        Row(
                            modifier = Modifier.fillMaxWidth(),
                            horizontalArrangement = Arrangement.SpaceBetween,
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            Text(
                                text = recording.album.name.resolve(context),
                                style = MaterialTheme.typography.titleSmall,
                                fontWeight = FontWeight.SemiBold,
                                color =
                                    if (isCurrent) MaterialTheme.colorScheme.primary
                                    else MaterialTheme.colorScheme.onSurface,
                                modifier = Modifier.weight(1f),
                            )
                            if (isCurrent) {
                                Text(
                                    text = stringResource(R.string.lbl_work_now_playing),
                                    style = MaterialTheme.typography.labelSmall,
                                    color = MaterialTheme.colorScheme.primary,
                                )
                            }
                        }

                        val performerLine =
                            listOfNotNull(
                                    recording.conductorLabel.takeIf { it.isNotEmpty() },
                                    recording.ensembleLabel.takeIf { it.isNotEmpty() },
                                    recording.year?.toString(),
                                )
                                .joinToString(" · ")
                        if (performerLine.isNotEmpty()) {
                            Text(
                                text = performerLine,
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        }
                        Text(
                            text =
                                "$codecLabel · $rateLabel · ${recording.movements.size} mvt · ${recording.totalDurationMs.formatDurationMs(false)}",
                            style = MaterialTheme.typography.labelSmall,
                            fontFamily = FontFamily.Monospace,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                }
            }

            val conductors =
                work.recordings
                    .flatMap { it.movements }
                    .flatMap { it.conductors }
                    .distinct()
            if (conductors.isNotEmpty()) {
                HorizontalDivider()
                Text(
                    text = stringResource(R.string.lbl_work_credits),
                    style = MaterialTheme.typography.labelMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                Row(
                    modifier = Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()),
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    AssistChip(
                        onClick = { onSelectComposer(work.composerName) },
                        label = { Text(work.composerName) },
                    )
                    for (cond in conductors) {
                        AssistChip(
                            onClick = { onSelectConductor(cond) },
                            label = { Text(cond) },
                        )
                    }
                }
            }

            Spacer(modifier = Modifier.height(6.dp))
        }
    }
}
