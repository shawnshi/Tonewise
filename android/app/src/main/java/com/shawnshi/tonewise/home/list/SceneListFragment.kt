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
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.ElevatedCard
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.dynamicDarkColorScheme
import androidx.compose.material3.dynamicLightColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
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
import androidx.compose.ui.unit.dp
import androidx.core.graphics.toColorInt
import androidx.fragment.app.Fragment
import androidx.fragment.app.activityViewModels
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import dagger.hilt.android.AndroidEntryPoint
import javax.inject.Inject
import com.shawnshi.tonewise.R
import com.shawnshi.tonewise.music.MusicViewModel
import com.shawnshi.tonewise.playback.PlaybackViewModel
import com.shawnshi.tonewise.sync.LanSyncRepository
import com.shawnshi.tonewise.sync.SceneBanner
import com.shawnshi.tonewise.sync.SessionSlotStore
import com.shawnshi.tonewise.util.showToast

/**
 * Library tab displaying reorderable 5D Scene Banners and the interactive DJ Tag Matrix Filter.
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
                var selectedScene by remember { mutableStateOf("all") }
                var selectedVocals by remember { mutableStateOf("all") }
                var selectedEnergy by remember { mutableStateOf("all") }

                val matchedSongs =
                    remember(selectedScene, selectedVocals, selectedEnergy, indexingState) {
                        lanSyncRepository.matchSongsByDjFilter(
                            selectedScene,
                            selectedVocals,
                            selectedEnergy,
                        )
                    }

                TonewiseSceneTabTheme {
                    SceneTabContent(
                        scenes = scenes,
                        indexingKey = indexingState,
                        countForScene = { scene -> lanSyncRepository.matchSongsForScene(scene).size },
                        onMoveUp = { idx -> lanSyncRepository.moveScene(idx, idx - 1) },
                        onMoveDown = { idx -> lanSyncRepository.moveScene(idx, idx + 1) },
                        onLaunchScene = { banner ->
                            val songs = lanSyncRepository.matchSongsForScene(banner)
                            if (songs.isNotEmpty()) {
                                sessionSlotStore.setActiveScene(banner.title)
                                lanSyncRepository.scheduleM3u8WriteBack(banner.title, songs)
                                playbackModel.play(songs)
                            } else {
                                requireContext().showToast(R.string.lbl_scene_no_matches)
                            }
                        },
                        selectedScene = selectedScene,
                        selectedVocals = selectedVocals,
                        selectedEnergy = selectedEnergy,
                        matchedCount = matchedSongs.size,
                        onSelectScene = { selectedScene = it },
                        onSelectVocals = { selectedVocals = it },
                        onSelectEnergy = { selectedEnergy = it },
                        onPlayMatched = {
                            if (matchedSongs.isNotEmpty()) {
                                val sceneLabel =
                                    if (selectedScene == "all") "Custom" else selectedScene
                                sessionSlotStore.setActiveScene(sceneLabel)
                                lanSyncRepository.scheduleM3u8WriteBack(sceneLabel, matchedSongs)
                                playbackModel.play(matchedSongs)
                            } else {
                                requireContext().showToast(R.string.lbl_scene_no_matches)
                            }
                        },
                    )
                }
            }
        }
    }
}

@Composable
private fun TonewiseSceneTabTheme(content: @Composable () -> Unit) {
    val context = LocalContext.current
    val dark = isSystemInDarkTheme()
    val scheme =
        when {
            Build.VERSION.SDK_INT >= Build.VERSION_CODES.S ->
                if (dark) dynamicDarkColorScheme(context) else dynamicLightColorScheme(context)
            dark -> darkColorScheme()
            else -> lightColorScheme()
        }
    MaterialTheme(colorScheme = scheme, content = content)
}

@Composable
private fun SceneTabContent(
    scenes: List<SceneBanner>,
    indexingKey: Any?,
    countForScene: (SceneBanner) -> Int,
    onMoveUp: (Int) -> Unit,
    onMoveDown: (Int) -> Unit,
    onLaunchScene: (SceneBanner) -> Unit,
    selectedScene: String,
    selectedVocals: String,
    selectedEnergy: String,
    matchedCount: Int,
    onSelectScene: (String) -> Unit,
    onSelectVocals: (String) -> Unit,
    onSelectEnergy: (String) -> Unit,
    onPlayMatched: () -> Unit,
) {
    Surface(modifier = Modifier.fillMaxSize(), color = Color.Transparent) {
        Column(
            modifier =
                Modifier.fillMaxSize()
                    .verticalScroll(rememberScrollState())
                    .navigationBarsPadding()
                    .padding(horizontal = 16.dp, vertical = 12.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            Text(
                text = stringResource(R.string.lbl_scenes_5d_feed),
                style = MaterialTheme.typography.titleSmall,
                color = MaterialTheme.colorScheme.primary,
            )

            scenes.forEachIndexed { index, scene ->
                val trackCount = remember(scene.id, indexingKey) { countForScene(scene) }
                val accent =
                    runCatching { Color(scene.accentHex.toColorInt()) }
                        .getOrElse { MaterialTheme.colorScheme.primary }
                ElevatedCard(
                    modifier = Modifier.fillMaxWidth().clickable { onLaunchScene(scene) },
                    shape = RoundedCornerShape(14.dp),
                ) {
                    Row(
                        modifier = Modifier.fillMaxWidth().padding(start = 14.dp, top = 12.dp, bottom = 12.dp, end = 6.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Box(
                            modifier =
                                Modifier.width(6.dp)
                                    .height(44.dp)
                                    .clip(RoundedCornerShape(3.dp))
                                    .background(accent)
                        )
                        Spacer(modifier = Modifier.width(12.dp))
                        Column(modifier = Modifier.weight(1f)) {
                            Text(
                                text = scene.title,
                                style = MaterialTheme.typography.titleMedium,
                                fontWeight = FontWeight.Medium,
                            )
                            val detailLine =
                                listOfNotNull(
                                        scene.subtitle.takeIf { it.isNotEmpty() },
                                        stringResource(R.string.fmt_scene_track_count, trackCount),
                                    )
                                    .joinToString(" · ")
                            Text(
                                text = detailLine,
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        }
                        IconButton(
                            onClick = { onMoveUp(index) },
                            enabled = index > 0,
                        ) {
                            Icon(
                                painter = painterResource(R.drawable.ic_down_24),
                                contentDescription = stringResource(R.string.lbl_scene_move_up),
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
                            )
                        }
                    }
                }
            }

            HorizontalDivider(modifier = Modifier.padding(vertical = 4.dp))

            Text(
                text = stringResource(R.string.lbl_dj_filter),
                style = MaterialTheme.typography.titleSmall,
                color = MaterialTheme.colorScheme.primary,
            )
            SceneDjFilterRow(
                label = stringResource(R.string.lbl_dj_scene),
                options = listOf("all", "focus", "coding", "relax", "energy", "pop"),
                selected = selectedScene,
                onSelect = onSelectScene,
            )
            SceneDjFilterRow(
                label = stringResource(R.string.lbl_dj_vocals),
                options = listOf("all", "vocal", "instrumental", "unknown"),
                selected = selectedVocals,
                onSelect = onSelectVocals,
            )
            SceneDjFilterRow(
                label = stringResource(R.string.lbl_dj_energy),
                options = listOf("all", "low", "medium", "high", "unknown"),
                selected = selectedEnergy,
                onSelect = onSelectEnergy,
            )
            Button(
                onClick = onPlayMatched,
                enabled = matchedCount > 0,
                modifier = Modifier.fillMaxWidth(),
            ) {
                Text(stringResource(R.string.fmt_dj_play_matches, matchedCount))
            }

            // Reserve space for the bottom mini playback bar & FAB.
            Spacer(modifier = Modifier.height(104.dp))
        }
    }
}

@Composable
private fun SceneDjFilterRow(
    label: String,
    options: List<String>,
    selected: String,
    onSelect: (String) -> Unit,
) {
    val allLabel = stringResource(R.string.lbl_dj_all)
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
                FilterChip(
                    selected = selected == opt,
                    onClick = { onSelect(opt) },
                    label = { Text(if (opt == "all") allLabel else opt) },
                )
            }
        }
    }
}
