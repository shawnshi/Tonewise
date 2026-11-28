/*
 * Copyright (c) 2026 Tonewise Project
 * QuickTagDialog.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.sync

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
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
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
import com.shawnshi.tonewise.R
import com.shawnshi.tonewise.music.FocusFilterLabels
import com.shawnshi.tonewise.music.MusicRepository
import com.shawnshi.tonewise.music.resolve
import com.shawnshi.tonewise.music.resolveNames
import com.shawnshi.tonewise.playback.PlaybackViewModel
import com.shawnshi.tonewise.ui.DynamicAccentController
import com.shawnshi.tonewise.ui.TonewiseComposeTheme
import com.shawnshi.tonewise.ui.UISettings
import org.oxycblt.musikr.Song

/**
 * Zero-keyboard 5D Enum Quick-Tag sheet for modifying Loved status, DJ_SCENE, DJ_VOCALS,
 * DJ_ENERGY, DJ_TEMPO, and Mood clusters via local LWW mutation queue.
 */
@AndroidEntryPoint
class QuickTagDialog : BackportBottomSheetDialogFragment() {
    @Inject lateinit var lanSyncRepository: LanSyncRepository
    @Inject lateinit var musicRepository: MusicRepository
    @Inject lateinit var uiSettings: UISettings
    @Inject lateinit var dynamicAccentController: DynamicAccentController
    private val playbackModel: PlaybackViewModel by activityViewModels()

    override fun onCreateView(
        inflater: LayoutInflater,
        container: ViewGroup?,
        savedInstanceState: Bundle?,
    ): View {
        val targetUid = arguments?.getString(ARG_SONG_UID)
        return ComposeView(requireContext()).apply {
            setViewCompositionStrategy(ViewCompositionStrategy.DisposeOnViewTreeLifecycleDestroyed)
            setContent {
                val playingSong by playbackModel.song.collectAsStateWithLifecycle()
                val activeAccent by dynamicAccentController.activeAccentArgb.collectAsStateWithLifecycle()
                val pendingCount by
                    lanSyncRepository.pendingMutationCount.collectAsStateWithLifecycle()
                val song =
                    remember(targetUid, playingSong) {
                        targetUid?.let { uid ->
                            musicRepository.library?.songs?.firstOrNull {
                                it.uid.toString() == uid
                            }
                        } ?: playingSong
                    }

                TonewiseComposeTheme(
                    roundMode = uiSettings.roundMode,
                    dynamicAccentArgb = if (uiSettings.enableDynamicCoverAccent) activeAccent else null,
                ) {
                    QuickTagSheet(
                        song = song,
                        pendingMutationsCount = pendingCount,
                        initialLoved = song?.let(lanSyncRepository::isLoved) ?: false,
                        initialScenes = song?.let(lanSyncRepository::getEffectiveDjScenes) ?: emptyList(),
                        initialVocals = song?.let(lanSyncRepository::getEffectiveDjVocals) ?: "unknown",
                        initialEnergy = song?.let(lanSyncRepository::getEffectiveDjEnergy) ?: "unknown",
                        initialTempo = song?.let(lanSyncRepository::getEffectiveDjTempo) ?: "",
                        initialMoodClusters =
                            song?.let(lanSyncRepository::getEffectiveMoodClusters) ?: emptyList(),
                        onMutate = { target, field, value ->
                            lanSyncRepository.enqueueTagMutation(target, field, value)
                        },
                    )
                }
            }
        }
    }

    companion object {
        const val TAG = "QuickTagDialog"
        private const val ARG_SONG_UID = "song_uid"

        fun newInstance(songUid: String? = null) =
            QuickTagDialog().apply {
                arguments = Bundle().apply {
                    if (songUid != null) putString(ARG_SONG_UID, songUid)
                }
            }
    }
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun QuickTagSheet(
    song: Song?,
    pendingMutationsCount: Int,
    initialLoved: Boolean,
    initialScenes: List<String>,
    initialVocals: String,
    initialEnergy: String,
    initialTempo: String,
    initialMoodClusters: List<String>,
    onMutate: (Song, String, String) -> Unit,
) {
    val context = androidx.compose.ui.platform.LocalContext.current
    Surface(modifier = Modifier.fillMaxWidth(), color = MaterialTheme.colorScheme.surface) {
        Column(
            modifier =
                Modifier.fillMaxWidth()
                    .verticalScroll(rememberScrollState())
                    .navigationBarsPadding()
                    .padding(horizontal = 20.dp, vertical = 16.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            Text(
                text = stringResource(R.string.lbl_quick_tag_menu),
                style = MaterialTheme.typography.titleLarge,
                fontWeight = FontWeight.SemiBold,
            )

            if (song == null) {
                Text(
                    text = stringResource(R.string.lbl_quick_tag_no_song),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            } else {
                var loved by remember(song.uid, pendingMutationsCount) { mutableStateOf(initialLoved) }
                var scenes by
                    remember(song.uid, pendingMutationsCount) {
                        mutableStateOf(initialScenes.toSet())
                    }
                var vocals by
                    remember(song.uid, pendingMutationsCount) { mutableStateOf(initialVocals) }
                var energy by
                    remember(song.uid, pendingMutationsCount) { mutableStateOf(initialEnergy) }
                var tempo by
                    remember(song.uid, pendingMutationsCount) { mutableStateOf(initialTempo) }
                var moods by
                    remember(song.uid, pendingMutationsCount) {
                        mutableStateOf(initialMoodClusters.toSet())
                    }

                Column(verticalArrangement = Arrangement.spacedBy(2.dp)) {
                    Text(
                        text = song.name.resolve(context),
                        style = MaterialTheme.typography.titleMedium,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                    Text(
                        text = song.artists.resolveNames(context),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                }

                HorizontalDivider()

                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Text(
                        text = stringResource(R.string.lbl_quick_tag_loved),
                        style = MaterialTheme.typography.bodyMedium,
                        modifier = Modifier.weight(1f),
                    )
                    Switch(
                        checked = loved,
                        onCheckedChange = { checked ->
                            loved = checked
                            onMutate(song, "rating", if (checked) "5" else "0")
                        },
                    )
                }

                // 1. DJ_SCENE (multi-select)
                QuickTagSectionTitle(stringResource(R.string.lbl_dj_scene))
                FlowRow(
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                    verticalArrangement = Arrangement.spacedBy(4.dp),
                ) {
                    for (sc in listOf("focus", "coding", "relax", "energy", "pop")) {
                        val selected = sc in scenes
                        val label = FocusFilterLabels.labelResForKey(sc)?.let { stringResource(it) } ?: sc
                        FilterChip(
                            selected = selected,
                            onClick = {
                                val next = if (selected) scenes - sc else scenes + sc
                                scenes = next
                                onMutate(song, "dj_scene", next.joinToString("; "))
                            },
                            label = { Text(label) },
                        )
                    }
                }

                // 2. DJ_VOCALS (single-select)
                QuickTagSectionTitle(stringResource(R.string.lbl_dj_vocals))
                Row(
                    modifier = Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()),
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    for (vc in listOf("instrumental", "vocal", "unknown")) {
                        val label = FocusFilterLabels.labelResForKey(vc)?.let { stringResource(it) } ?: vc
                        FilterChip(
                            selected = vocals == vc,
                            onClick = {
                                vocals = vc
                                onMutate(song, "dj_vocals", vc)
                            },
                            label = { Text(label) },
                        )
                    }
                }

                // 3. DJ_ENERGY (single-select)
                QuickTagSectionTitle(stringResource(R.string.lbl_dj_energy))
                Row(
                    modifier = Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()),
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    for (en in listOf("low", "medium", "high", "unknown")) {
                        val label = FocusFilterLabels.labelResForKey(en)?.let { stringResource(it) } ?: en
                        FilterChip(
                            selected = energy == en,
                            onClick = {
                                energy = en
                                onMutate(song, "dj_energy", en)
                            },
                            label = { Text(label) },
                        )
                    }
                }

                // 4. DJ_TEMPO (single-select, tap selected to clear)
                QuickTagSectionTitle(stringResource(R.string.lbl_dj_tempo))
                FlowRow(
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                    verticalArrangement = Arrangement.spacedBy(4.dp),
                ) {
                    for (tp in listOf("Very Slow", "Slow", "Moderate", "Fast", "Very Fast")) {
                        val selected = tempo.equals(tp, ignoreCase = true)
                        val label = FocusFilterLabels.labelResForKey(tp)?.let { stringResource(it) } ?: tp
                        FilterChip(
                            selected = selected,
                            onClick = {
                                val next = if (selected) "" else tp
                                tempo = next
                                onMutate(song, "tempo_bucket", next)
                            },
                            label = { Text(label) },
                        )
                    }
                }

                // 5. MOOD CLUSTER (multi-select)
                QuickTagSectionTitle(stringResource(R.string.lbl_dj_mood))
                FlowRow(
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                    verticalArrangement = Arrangement.spacedBy(4.dp),
                ) {
                    for (cluster in
                        listOf(
                            "calm_relaxed",
                            "bright_joyful",
                            "deep_introspective",
                            "warm_romantic",
                            "high_tension",
                        )) {
                        val selected = cluster in moods
                        val label = FocusFilterLabels.labelResForKey(cluster)?.let { stringResource(it) } ?: cluster
                        FilterChip(
                            selected = selected,
                            onClick = {
                                val next = if (selected) moods - cluster else moods + cluster
                                moods = next
                                onMutate(song, "mood", next.joinToString("; "))
                            },
                            label = { Text(label) },
                        )
                    }
                }

                Text(
                    text = stringResource(R.string.fmt_pending_mutations, pendingMutationsCount),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
    }
}

@Composable
private fun QuickTagSectionTitle(text: String) {
    Text(
        text = text,
        style = MaterialTheme.typography.labelLarge,
        fontWeight = FontWeight.Medium,
        color = MaterialTheme.colorScheme.secondary,
    )
}
