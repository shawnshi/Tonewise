/*
 * Copyright (c) 2026 Tonewise Project
 * SceneListFragment.kt is part of Tonewise.
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

package com.shawnshi.tonewise.home.list

import android.os.Build
import android.os.Bundle
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.defaultMinSize
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.rotate
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.ComposeView
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.ViewCompositionStrategy
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.fragment.app.Fragment
import androidx.fragment.app.activityViewModels
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import dagger.hilt.android.AndroidEntryPoint
import javax.inject.Inject
import com.shawnshi.tonewise.R
import com.shawnshi.tonewise.music.MusicViewModel
import com.shawnshi.tonewise.playback.PlaybackViewModel
import com.shawnshi.tonewise.sync.FocusFilterSpec
import com.shawnshi.tonewise.sync.LanSyncRepository
import com.shawnshi.tonewise.sync.SceneBanner
import com.shawnshi.tonewise.sync.SceneRuleEvaluator
import com.shawnshi.tonewise.sync.SessionSlotStore
import com.shawnshi.tonewise.ui.TonewiseComposeTheme
import com.shawnshi.tonewise.util.showToast
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONObject
import org.oxycblt.musikr.Song

/**
 * Library tab displaying reorderable 5D Scene Banners and the interactive 5D DJ Tag Matrix Filter.
 */
@AndroidEntryPoint
class SceneListFragment : Fragment() {
    @Inject lateinit var lanSyncRepository: LanSyncRepository
    @Inject lateinit var sessionSlotStore: SessionSlotStore
    private val playbackModel: PlaybackViewModel by activityViewModels()
    private val musicModel: MusicViewModel by activityViewModels()

    override fun onCreateView(
        inflater: LayoutInflater,
        container: ViewGroup?,
        savedInstanceState: Bundle?,
    ): View {
        return ComposeView(requireContext()).apply {
            setViewCompositionStrategy(ViewCompositionStrategy.DisposeOnViewTreeLifecycleDestroyed)
            setContent {
                val scenes by lanSyncRepository.scenes.collectAsStateWithLifecycle()
                val indexingState by musicModel.indexingState.collectAsStateWithLifecycle()
                val pendingMutationCount by
                    lanSyncRepository.pendingMutationCount.collectAsStateWithLifecycle()
                val rediscoverySpec = remember {
                    FocusFilterSpec(unplayedDaysGte = 90, sortArc = "rediscovery_90d")
                }

                val scope = rememberCoroutineScope()
                val sceneCounts by
                    produceState<Map<String, Int>>(
                        initialValue = emptyMap(),
                        scenes,
                        indexingState,
                        pendingMutationCount,
                    ) {
                        value =
                            withContext(Dispatchers.Default) {
                                lanSyncRepository.countSongsForScenes(scenes)
                            }
                    }

                val rediscoverySongs by
                    produceState<List<Song>>(
                        initialValue = emptyList(),
                        indexingState,
                        pendingMutationCount,
                    ) {
                        value =
                            withContext(Dispatchers.Default) {
                                lanSyncRepository.matchSongsByFocus(rediscoverySpec)
                            }
                    }

                TonewiseComposeTheme {
                    SceneTabContent(
                        scenes = scenes,
                        sceneCounts = sceneCounts,
                        rediscoveryCount = rediscoverySongs.size,
                        onLaunchRediscovery = {
                            if (rediscoverySongs.isNotEmpty()) {
                                val title = getString(R.string.lbl_rediscovery_90d_title)
                                sessionSlotStore.setActiveScene(title)
                                lanSyncRepository.scheduleM3u8WriteBack(title, rediscoverySongs)
                                playbackModel.play(rediscoverySongs)
                            } else {
                                requireContext().showToast(R.string.lbl_scene_no_matches)
                            }
                        },
                        onMoveUp = { idx -> lanSyncRepository.moveScene(idx, idx - 1) },
                        onMoveDown = { idx -> lanSyncRepository.moveScene(idx, idx + 1) },
                        onLaunchScene = { banner ->
                            scope.launch {
                                val ordered =
                                    withContext(Dispatchers.Default) {
                                        val matched = lanSyncRepository.matchSongsForScene(banner)
                                        val arc = SceneRuleEvaluator.extractSortArc(banner.rawSceneJson)
                                        if (arc == "work_shuffle") {
                                            lanSyncRepository.orderSongsWorkShuffle(matched)
                                        } else {
                                            matched
                                        }
                                    }
                                if (ordered.isNotEmpty()) {
                                    sessionSlotStore.setActiveScene(banner.title)
                                    lanSyncRepository.scheduleM3u8WriteBack(banner.title, ordered)
                                    playbackModel.play(ordered)
                                } else {
                                    requireContext().showToast(R.string.lbl_scene_no_matches)
                                }
                            }
                        },
                    )
                }
            }
        }
    }
}

@Composable
private fun SceneTabContent(
    scenes: List<SceneBanner>,
    sceneCounts: Map<String, Int>,
    rediscoveryCount: Int,
    onLaunchRediscovery: () -> Unit,
    onMoveUp: (Int) -> Unit,
    onMoveDown: (Int) -> Unit,
    onLaunchScene: (SceneBanner) -> Unit,
) {
    val context = LocalContext.current
    var isReorderMode by remember { mutableStateOf(false) }

    Surface(modifier = Modifier.fillMaxSize(), color = Color.Transparent) {
        Column(
            modifier =
                Modifier.fillMaxSize()
                    .verticalScroll(rememberScrollState())
                    .navigationBarsPadding(),
        ) {
            Row(
                modifier =
                    Modifier.fillMaxWidth()
                        .defaultMinSize(minHeight = 48.dp)
                        .padding(start = 16.dp, end = 4.dp, top = 4.dp, bottom = 4.dp),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(
                    text = stringResource(R.string.lbl_scenes),
                    style = MaterialTheme.typography.labelLarge,
                    fontWeight = FontWeight.Medium,
                    color = MaterialTheme.colorScheme.secondary,
                )
                TextButton(onClick = { isReorderMode = !isReorderMode }) {
                    Text(
                        text =
                            stringResource(
                                if (isReorderMode) {
                                    R.string.lbl_scene_done
                                } else {
                                    R.string.lbl_scene_reorder
                                }
                            )
                    )
                }
            }

            scenes.forEachIndexed { index, scene ->
                val trackCount = sceneCounts[scene.id] ?: 0
                val countText =
                    if (trackCount > 0) {
                        context.resources.getQuantityString(
                            R.plurals.fmt_song_count,
                            trackCount,
                            trackCount,
                        )
                    } else {
                        stringResource(R.string.def_song_count)
                    }
                val detailLine =
                    if (scene.subtitle.isNotEmpty()) "$countText • ${scene.subtitle}" else countText

                val accent = parseSceneAccentColor(scene.accentHex, MaterialTheme.colorScheme.primary)
                Row(
                    modifier =
                        Modifier.fillMaxWidth()
                            .clickable { onLaunchScene(scene) }
                            .padding(start = 16.dp, top = 12.dp, end = 12.dp, bottom = 12.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Box(
                        modifier =
                            Modifier.size(56.dp)
                                .clip(RoundedCornerShape(12.dp))
                                .background(accent.copy(alpha = 0.14f))
                                .border(1.dp, accent.copy(alpha = 0.30f), RoundedCornerShape(12.dp)),
                        contentAlignment = Alignment.Center,
                    ) {
                        Icon(
                            painter = painterResource(R.drawable.ic_playlist_24),
                            contentDescription = null,
                            tint = accent,
                            modifier = Modifier.size(28.dp),
                        )
                    }
                    Spacer(modifier = Modifier.width(16.dp))
                    Column(modifier = Modifier.weight(1f)) {
                        Text(
                            text = scene.title,
                            style = MaterialTheme.typography.titleMedium,
                            color = MaterialTheme.colorScheme.onSurface,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                        )
                        Text(
                            text = detailLine,
                            style = MaterialTheme.typography.bodyMedium,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                        )
                    }
                    if (isReorderMode) {
                        IconButton(
                            onClick = { onMoveUp(index) },
                            enabled = index > 0,
                        ) {
                            Icon(
                                painter = painterResource(R.drawable.ic_down_24),
                                contentDescription = stringResource(R.string.lbl_scene_move_up),
                                tint =
                                    if (index > 0) {
                                        MaterialTheme.colorScheme.onSurfaceVariant
                                    } else {
                                        MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.38f)
                                    },
                                modifier = Modifier.rotate(180f),
                            )
                        }
                        IconButton(
                            onClick = { onMoveDown(index) },
                            enabled = index < scenes.lastIndex,
                        ) {
                            Icon(
                                painter = painterResource(R.drawable.ic_down_24),
                                contentDescription = stringResource(R.string.lbl_scene_move_down),
                                tint =
                                    if (index < scenes.lastIndex) {
                                        MaterialTheme.colorScheme.onSurfaceVariant
                                    } else {
                                        MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.38f)
                                    },
                            )
                        }
                    } else {
                        IconButton(onClick = { onLaunchScene(scene) }) {
                            Icon(
                                painter = painterResource(R.drawable.ic_play_24),
                                contentDescription = scene.title,
                                tint = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        }
                    }
                }
            }

            // Explicit P1-3 anti-filter-bubble entry: Buried Treasures (90d+ unplayed)
            val rediscoveryCountText =
                if (rediscoveryCount > 0) {
                    context.resources.getQuantityString(
                        R.plurals.fmt_song_count,
                        rediscoveryCount,
                        rediscoveryCount,
                    )
                } else {
                    stringResource(R.string.def_song_count)
                }
            Row(
                modifier =
                    Modifier.fillMaxWidth()
                        .clickable { onLaunchRediscovery() }
                        .padding(start = 16.dp, top = 12.dp, end = 12.dp, bottom = 12.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Box(
                    modifier =
                        Modifier.size(56.dp)
                            .clip(RoundedCornerShape(12.dp))
                            .background(MaterialTheme.colorScheme.secondaryContainer),
                    contentAlignment = Alignment.Center,
                ) {
                    Icon(
                        painter = painterResource(R.drawable.ic_time_24),
                        contentDescription = null,
                        tint = MaterialTheme.colorScheme.onSecondaryContainer,
                        modifier = Modifier.size(28.dp),
                    )
                }
                Spacer(modifier = Modifier.width(16.dp))
                Column(modifier = Modifier.weight(1f)) {
                    Text(
                        text = stringResource(R.string.lbl_rediscovery_90d_title),
                        style = MaterialTheme.typography.titleMedium,
                        color = MaterialTheme.colorScheme.onSurface,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                    Text(
                        text =
                            "$rediscoveryCountText • ${stringResource(R.string.lbl_rediscovery_90d_subtitle)}",
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                }
                IconButton(onClick = onLaunchRediscovery) {
                    Icon(
                        painter = painterResource(R.drawable.ic_play_24),
                        contentDescription = stringResource(R.string.lbl_rediscovery_90d_title),
                        tint = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }

            // Reserve space for the bottom mini playback bar & FAB.
            Spacer(modifier = Modifier.height(104.dp))
        }
    }
}

private fun parseSceneAccentColor(hex: String, fallback: Color): Color =
    runCatching { Color(android.graphics.Color.parseColor(hex)) }.getOrDefault(fallback)
