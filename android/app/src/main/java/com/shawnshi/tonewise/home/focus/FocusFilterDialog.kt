/*
 * Copyright (c) 2026 Tonewise Project
 * FocusFilterDialog.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.home.focus

import android.os.Bundle
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.FilterChip
import androidx.compose.material3.FilterChipDefaults
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.ComposeView
import androidx.compose.ui.platform.ViewCompositionStrategy
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.fragment.app.activityViewModels
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.google.android.material.bottomsheet.BackportBottomSheetDialogFragment
import dagger.hilt.android.AndroidEntryPoint
import javax.inject.Inject
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import com.shawnshi.tonewise.R
import com.shawnshi.tonewise.music.MusicViewModel
import com.shawnshi.tonewise.playback.PlaybackViewModel
import com.shawnshi.tonewise.sync.FocusFilterSpec
import com.shawnshi.tonewise.sync.FocusPillState
import com.shawnshi.tonewise.sync.LanSyncRepository
import com.shawnshi.tonewise.sync.SessionSlotStore
import com.shawnshi.tonewise.ui.TonewiseComposeTheme
import com.shawnshi.tonewise.util.showToast
import org.oxycblt.musikr.Song

/**
 * Roon ARC-style tri-state boolean Focus Funnel drawer allowing `+ include` and `- exclude`
 * multi-dimensional filtering across the whole library or a scoped playlist/artist/album/genre.
 */
@AndroidEntryPoint
class FocusFilterDialog : BackportBottomSheetDialogFragment() {
    @Inject lateinit var lanSyncRepository: LanSyncRepository
    @Inject lateinit var sessionSlotStore: SessionSlotStore
    private val playbackModel: PlaybackViewModel by activityViewModels()
    private val musicModel: MusicViewModel by activityViewModels()

    override fun onCreateView(
        inflater: LayoutInflater,
        container: ViewGroup?,
        savedInstanceState: Bundle?,
    ): View {
        val scopeTitle = arguments?.getString(ARG_SCOPE_TITLE)
        val scopedUids = arguments?.getStringArrayList(ARG_SCOPED_UIDS)?.toSet()
        return ComposeView(requireContext()).apply {
            setViewCompositionStrategy(ViewCompositionStrategy.DisposeOnViewTreeLifecycleDestroyed)
            setContent {
                val indexingState by musicModel.indexingState.collectAsStateWithLifecycle()
                val pendingMutations by
                    lanSyncRepository.pendingMutationCount.collectAsStateWithLifecycle()
                var spec by remember { mutableStateOf(FocusFilterSpec()) }

                val matchedSongs by
                    produceState<List<Song>>(
                        initialValue = emptyList(),
                        spec,
                        scopedUids,
                        indexingState,
                        pendingMutations,
                    ) {
                        value =
                            withContext(Dispatchers.Default) {
                                lanSyncRepository.matchSongsByFocus(spec, scopedUids)
                            }
                    }

                TonewiseComposeTheme {
                    FocusFilterSheet(
                        scopeTitle = scopeTitle,
                        spec = spec,
                        matchedCount = matchedSongs.size,
                        onSpecChange = { spec = it },
                        onPlayMatched = {
                            if (matchedSongs.isEmpty()) {
                                requireContext().showToast(R.string.lbl_scene_no_matches)
                            } else {
                                val playSongs =
                                    if (spec.sortArc == "work_shuffle") {
                                        lanSyncRepository.orderSongsWorkShuffle(matchedSongs)
                                    } else {
                                        matchedSongs
                                    }
                                val label = scopeTitle ?: getString(R.string.lbl_focus_filter)
                                sessionSlotStore.setActiveScene(label)
                                lanSyncRepository.scheduleM3u8WriteBack(label, playSongs)
                                playbackModel.play(playSongs)
                                dismiss()
                            }
                        },
                        onAddToQueue = {
                            if (matchedSongs.isEmpty()) {
                                requireContext().showToast(R.string.lbl_scene_no_matches)
                            } else {
                                val queuedSongs =
                                    if (spec.sortArc == "work_shuffle") {
                                        lanSyncRepository.orderSongsWorkShuffle(matchedSongs)
                                    } else {
                                        matchedSongs
                                    }
                                playbackModel.addToQueue(queuedSongs)
                                requireContext().showToast(R.string.lng_queue_added)
                                dismiss()
                            }
                        },
                    )
                }
            }
        }
    }

    companion object {
        const val TAG = "FocusFilterDialog"
        private const val ARG_SCOPE_TITLE = "scope_title"
        private const val ARG_SCOPED_UIDS = "scoped_uids"

        fun newInstance(scopeTitle: String? = null, scopedSongUids: ArrayList<String>? = null) =
            FocusFilterDialog().apply {
                arguments = Bundle().apply {
                    if (scopeTitle != null) putString(ARG_SCOPE_TITLE, scopeTitle)
                    if (scopedSongUids != null) putStringArrayList(ARG_SCOPED_UIDS, scopedSongUids)
                }
            }
    }
}

@Composable
private fun FocusFilterSheet(
    scopeTitle: String?,
    spec: FocusFilterSpec,
    matchedCount: Int,
    onSpecChange: (FocusFilterSpec) -> Unit,
    onPlayMatched: () -> Unit,
    onAddToQueue: () -> Unit,
) {
    Surface(modifier = Modifier.fillMaxWidth(), color = MaterialTheme.colorScheme.surface) {
        Column(
            modifier =
                Modifier.fillMaxWidth()
                    .verticalScroll(rememberScrollState())
                    .navigationBarsPadding()
                    .padding(horizontal = 20.dp, vertical = 16.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Column(modifier = Modifier.weight(1f)) {
                    Text(
                        text = stringResource(R.string.lbl_focus_filter),
                        style = MaterialTheme.typography.titleLarge,
                        fontWeight = FontWeight.SemiBold,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                    if (!scopeTitle.isNullOrBlank()) {
                        Text(
                            text = scopeTitle,
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.primary,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                        )
                    }
                }
                TextButton(
                    onClick = { onSpecChange(FocusFilterSpec()) },
                    enabled = !spec.isEmpty || spec.sortArc != "flat",
                ) {
                    Text(stringResource(R.string.lbl_focus_reset))
                }
            }

            Text(
                text = stringResource(R.string.lbl_focus_hint),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )

            FocusTriStateMatrix(spec = spec, onSpecChange = onSpecChange)

            HorizontalDivider()

            if (matchedCount > 0) {
                OutlinedButton(
                    onClick = onAddToQueue,
                    modifier = Modifier.fillMaxWidth(),
                ) {
                    Text(stringResource(R.string.lbl_focus_queue_add, matchedCount))
                }
            }
            Button(
                onClick = onPlayMatched,
                enabled = matchedCount > 0,
                modifier = Modifier.fillMaxWidth(),
            ) {
                Text(stringResource(R.string.fmt_dj_play_matches, matchedCount))
            }
        }
    }
}

private fun toggleTriState(
    current: Map<String, FocusPillState>,
    key: String,
): Map<String, FocusPillState> {
    val next = (current[key] ?: FocusPillState.NONE).next()
    val updated = current.toMutableMap()
    if (next == FocusPillState.NONE) {
        updated.remove(key)
    } else {
        updated[key] = next
    }
    return updated
}

/**
 * Shared tri-state (`NONE -> + INCLUDE -> - EXCLUDE`) 5D Focus matrix used by both [FocusFilterDialog]
 * and the collapsible matrix section inside `SceneListFragment`.
 */
@OptIn(ExperimentalLayoutApi::class)
@Composable
internal fun FocusTriStateMatrix(
    spec: FocusFilterSpec,
    onSpecChange: (FocusFilterSpec) -> Unit,
) {
    Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
        FocusTriStateFacetRow(
            label = stringResource(R.string.lbl_dj_scene),
            options = listOf("focus", "coding", "relax", "energy", "pop"),
            states = spec.scenes,
            onToggle = { key -> onSpecChange(spec.copy(scenes = toggleTriState(spec.scenes, key))) },
        )
        FocusTriStateFacetRow(
            label = stringResource(R.string.lbl_dj_vocals),
            options = listOf("instrumental", "vocal", "unknown"),
            states = spec.vocals,
            onToggle = { key -> onSpecChange(spec.copy(vocals = toggleTriState(spec.vocals, key))) },
        )
        FocusTriStateFacetRow(
            label = stringResource(R.string.lbl_dj_energy),
            options = listOf("low", "medium", "high", "unknown"),
            states = spec.energies,
            onToggle = { key -> onSpecChange(spec.copy(energies = toggleTriState(spec.energies, key))) },
        )
        FocusTriStateFacetRow(
            label = stringResource(R.string.lbl_dj_tempo),
            options = listOf("Very Slow", "Slow", "Moderate", "Fast", "Very Fast"),
            states = spec.tempos,
            onToggle = { key -> onSpecChange(spec.copy(tempos = toggleTriState(spec.tempos, key))) },
        )
        FocusTriStateFacetRow(
            label = stringResource(R.string.lbl_dj_mood),
            options =
                listOf(
                    "calm_relaxed",
                    "bright_joyful",
                    "deep_introspective",
                    "warm_romantic",
                    "high_tension",
                ),
            states = spec.moods,
            onToggle = { key -> onSpecChange(spec.copy(moods = toggleTriState(spec.moods, key))) },
        )
        FilterChip(
            selected = spec.unplayedDaysGte != null,
            onClick = {
                onSpecChange(
                    spec.copy(unplayedDaysGte = if (spec.unplayedDaysGte != null) null else 90)
                )
            },
            label = { Text(stringResource(R.string.lbl_rediscovery_90d_title)) },
        )

        Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
            Text(
                text = stringResource(R.string.lbl_dj_sort_arc),
                style = MaterialTheme.typography.labelMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Row(
                modifier = Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                for (arc in
                    listOf(
                        "flat",
                        "energy_asc",
                        "energy_desc_sleep",
                        "rediscovery_90d",
                        "work_shuffle",
                    )) {
                    FilterChip(
                        selected = spec.sortArc == arc,
                        onClick = { onSpecChange(spec.copy(sortArc = arc)) },
                        label = { Text(arc) },
                    )
                }
            }
        }
    }
}

@Composable
private fun FocusTriStateFacetRow(
    label: String,
    options: List<String>,
    states: Map<String, FocusPillState>,
    onToggle: (String) -> Unit,
) {
    Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
        Text(
            text = label,
            style = MaterialTheme.typography.labelMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
        Row(
            modifier = Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()),
            horizontalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            for (opt in options) {
                val st = states[opt] ?: FocusPillState.NONE
                val chipColors =
                    when (st) {
                        FocusPillState.EXCLUDE ->
                            FilterChipDefaults.filterChipColors(
                                selectedContainerColor = MaterialTheme.colorScheme.errorContainer,
                                selectedLabelColor = MaterialTheme.colorScheme.onErrorContainer,
                            )
                        else -> FilterChipDefaults.filterChipColors()
                    }
                val prefix =
                    when (st) {
                        FocusPillState.NONE -> ""
                        FocusPillState.INCLUDE -> "+ "
                        FocusPillState.EXCLUDE -> "- "
                    }
                FilterChip(
                    selected = st != FocusPillState.NONE,
                    onClick = { onToggle(opt) },
                    colors = chipColors,
                    label = { Text("$prefix$opt") },
                )
            }
        }
    }
}
