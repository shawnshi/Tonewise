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

import android.os.Bundle
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.Toast
import androidx.annotation.DrawableRes
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.defaultMinSize
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.CircularProgressIndicator
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
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.ui.platform.ComposeView
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.ViewCompositionStrategy
import androidx.compose.ui.platform.rememberNestedScrollInteropConnection
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
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
import com.shawnshi.tonewise.sync.AutoDjEngine
import com.shawnshi.tonewise.sync.AutoDjTimeProfile
import com.shawnshi.tonewise.sync.FocusFilterSpec
import com.shawnshi.tonewise.sync.LanSyncRepository
import com.shawnshi.tonewise.sync.SceneBanner
import com.shawnshi.tonewise.sync.SessionSlotStore
import com.shawnshi.tonewise.ui.TonewiseComposeTheme
import com.shawnshi.tonewise.ui.UISettings
import com.shawnshi.tonewise.util.showToast
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.oxycblt.musikr.Song

private const val AUTO_DJ_KEY = "__autodj__"
private const val REDISCOVERY_BUILTIN_ID = "44444444-4444-4444-8444-444444444444"

/**
 * Library tab displaying time-aware Auto DJ and 5D Scene Auto DJ mixes (50 tracks each).
 */
@AndroidEntryPoint
class SceneListFragment : Fragment() {
    @Inject lateinit var lanSyncRepository: LanSyncRepository
    @Inject lateinit var sessionSlotStore: SessionSlotStore
    @Inject lateinit var uiSettings: UISettings
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
                val activeSceneLabel by sessionSlotStore.activeSceneLabel.collectAsStateWithLifecycle()
                val indexingState by musicModel.indexingState.collectAsStateWithLifecycle()
                val pendingMutationCount by
                    lanSyncRepository.pendingMutationCount.collectAsStateWithLifecycle()
                val hasBuiltinRediscovery =
                    remember(scenes) { hasBuiltinRediscoveryScene(scenes) }
                val rediscoverySpec = remember {
                    FocusFilterSpec(unplayedDaysGte = 90, sortArc = "rediscovery_90d")
                }

                val scope = rememberCoroutineScope()
                var lastAutoDjSummary by remember { mutableStateOf<String?>(null) }
                var generatingKey by remember { mutableStateOf<String?>(null) }
                val timeProfile = remember(indexingState, pendingMutationCount) {
                    AutoDjEngine.resolveTimeProfile()
                }
                val roundMode = remember { uiSettings.roundMode }

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
                        hasBuiltinRediscovery,
                        indexingState,
                        pendingMutationCount,
                    ) {
                        value =
                            if (hasBuiltinRediscovery) {
                                emptyList()
                            } else {
                                withContext(Dispatchers.Default) {
                                    lanSyncRepository.matchSongsByFocus(rediscoverySpec)
                                }
                            }
                    }

                fun launchAutoDj(scene: SceneBanner?) {
                    if (generatingKey != null) return
                    generatingKey = scene?.id ?: AUTO_DJ_KEY
                    scope.launch {
                        try {
                            val (result, ordered) =
                                withContext(Dispatchers.Default) {
                                    lanSyncRepository.generateAutoDjPlaylist(
                                        scene = scene,
                                        targetCount = AutoDjEngine.DEFAULT_TARGET_COUNT,
                                    )
                                }
                            if (ordered.isNotEmpty()) {
                                val label = scene?.title ?: "Auto DJ · ${result.profile.periodLabel}"
                                sessionSlotStore.setActiveScene(label)
                                lastAutoDjSummary = result.summaryText
                                playbackModel.play(ordered)
                                Toast.makeText(
                                        requireContext().applicationContext,
                                        result.summaryText,
                                        Toast.LENGTH_SHORT,
                                    )
                                    .show()
                            } else {
                                requireContext().showToast(R.string.lbl_scene_no_matches)
                            }
                        } finally {
                            generatingKey = null
                        }
                    }
                }

                TonewiseComposeTheme {
                    SceneTabContent(
                        scenes = scenes,
                        sceneCounts = sceneCounts,
                        activeSceneLabel = activeSceneLabel,
                        timeProfile = timeProfile,
                        lastAutoDjSummary = lastAutoDjSummary,
                        generatingKey = generatingKey,
                        roundMode = roundMode,
                        showStandaloneRediscovery = !hasBuiltinRediscovery,
                        rediscoveryCount = rediscoverySongs.size,
                        onLaunchAutoDj = { launchAutoDj(null) },
                        onLaunchRediscovery = {
                            launchAutoDj(
                                SceneBanner(
                                    id = "rediscovery_90d",
                                    title = getString(R.string.lbl_rediscovery_90d_title),
                                    subtitle = getString(R.string.lbl_rediscovery_90d_subtitle),
                                    accentHex = "#6366F1",
                                    rawSceneJson =
                                        """{"id":"rediscovery_90d","sort_arc":"rediscovery_90d","rule":{"all":[{"predicate":{"field":"unplayed_days_gte","op":"gte","value":90}}]}}""",
                                )
                            )
                        },
                        onMoveUp = { idx -> lanSyncRepository.moveScene(idx, idx - 1) },
                        onMoveDown = { idx -> lanSyncRepository.moveScene(idx, idx + 1) },
                        onLaunchScene = { banner -> launchAutoDj(banner) },
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
    activeSceneLabel: String,
    timeProfile: AutoDjTimeProfile,
    lastAutoDjSummary: String?,
    generatingKey: String?,
    roundMode: Boolean,
    showStandaloneRediscovery: Boolean,
    rediscoveryCount: Int,
    onLaunchAutoDj: () -> Unit,
    onLaunchRediscovery: () -> Unit,
    onMoveUp: (Int) -> Unit,
    onMoveDown: (Int) -> Unit,
    onLaunchScene: (SceneBanner) -> Unit,
) {
    val context = LocalContext.current
    var isReorderMode by remember { mutableStateOf(false) }
    val isBusy = generatingKey != null
    val tileShape = remember(roundMode) {
        if (roundMode) RoundedCornerShape(12.dp) else RoundedCornerShape(0.dp)
    }
    val nestedScrollInterop = rememberNestedScrollInteropConnection()

    Surface(modifier = Modifier.fillMaxSize(), color = Color.Transparent) {
        LazyColumn(
            modifier =
                Modifier.fillMaxSize()
                    .nestedScroll(nestedScrollInterop)
                    .navigationBarsPadding(),
            contentPadding = PaddingValues(bottom = 88.dp),
        ) {
            item(key = "header_autodj") {
                Box(
                    modifier =
                        Modifier.fillMaxWidth()
                            .defaultMinSize(minHeight = 48.dp)
                            .padding(horizontal = 16.dp, vertical = 8.dp),
                    contentAlignment = Alignment.CenterStart,
                ) {
                    Text(
                        text = stringResource(R.string.lbl_autodj_title),
                        style = MaterialTheme.typography.labelLarge,
                        color = MaterialTheme.colorScheme.secondary,
                    )
                }
            }

            item(key = "row_autodj") {
                val isAutoDjActive = activeSceneLabel.startsWith("Auto DJ")
                val autoDjBadge = stringResource(R.string.lbl_autodj_badge)
                val autoDjSubtitle = lastAutoDjSummary ?: "$autoDjBadge • ${timeProfile.description}"
                SceneListRow(
                    title = timeProfile.periodLabel,
                    subtitle = autoDjSubtitle,
                    iconRes = R.drawable.ic_auto_24,
                    tileShape = tileShape,
                    isActive = isAutoDjActive,
                    isGenerating = generatingKey == AUTO_DJ_KEY,
                    enabled = !isBusy,
                    idleTileColor = MaterialTheme.colorScheme.secondaryContainer,
                    idleIconTint = MaterialTheme.colorScheme.onSecondaryContainer,
                    actionContentDescription = stringResource(R.string.lbl_autodj_action),
                    onClick = onLaunchAutoDj,
                )
            }

            item(key = "header_scenes") {
                Row(
                    modifier =
                        Modifier.fillMaxWidth()
                            .defaultMinSize(minHeight = 48.dp)
                            .padding(start = 16.dp, end = 4.dp),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Text(
                        text = stringResource(R.string.lbl_scenes),
                        style = MaterialTheme.typography.labelLarge,
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
            }

            itemsIndexed(
                items = scenes,
                key = { _, scene -> scene.id },
            ) { index, scene ->
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
                    if (scene.subtitle.isNotEmpty()) {
                        "$countText • ${scene.subtitle}"
                    } else {
                        countText
                    }
                val isActive = activeSceneLabel == scene.title

                SceneListRow(
                    title = scene.title,
                    subtitle = detailLine,
                    iconRes = resolveSceneIconRes(scene),
                    tileShape = tileShape,
                    isActive = isActive,
                    isGenerating = generatingKey == scene.id,
                    enabled = !isBusy,
                    idleTileColor = MaterialTheme.colorScheme.surfaceContainer,
                    idleIconTint = MaterialTheme.colorScheme.onSurface,
                    actionContentDescription = scene.title,
                    onClick = { onLaunchScene(scene) },
                    trailingContent =
                        if (isReorderMode) {
                            {
                                Row(verticalAlignment = Alignment.CenterVertically) {
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
                                                    MaterialTheme.colorScheme.onSurfaceVariant.copy(
                                                        alpha = 0.38f
                                                    )
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
                                            contentDescription =
                                                stringResource(R.string.lbl_scene_move_down),
                                            tint =
                                                if (index < scenes.lastIndex) {
                                                    MaterialTheme.colorScheme.onSurfaceVariant
                                                } else {
                                                    MaterialTheme.colorScheme.onSurfaceVariant.copy(
                                                        alpha = 0.38f
                                                    )
                                                },
                                        )
                                    }
                                }
                            }
                        } else {
                            null
                        },
                )
            }

            if (showStandaloneRediscovery) {
                item(key = "row_rediscovery_90d") {
                    val rediscoveryTitle = stringResource(R.string.lbl_rediscovery_90d_title)
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
                    val detailLine =
                        "$rediscoveryCountText • ${stringResource(R.string.lbl_rediscovery_90d_subtitle)}"

                    SceneListRow(
                        title = rediscoveryTitle,
                        subtitle = detailLine,
                        iconRes = R.drawable.ic_time_24,
                        tileShape = tileShape,
                        isActive = activeSceneLabel == rediscoveryTitle,
                        isGenerating = generatingKey == "rediscovery_90d",
                        enabled = !isBusy,
                        idleTileColor = MaterialTheme.colorScheme.surfaceContainer,
                        idleIconTint = MaterialTheme.colorScheme.onSurface,
                        actionContentDescription = rediscoveryTitle,
                        onClick = onLaunchRediscovery,
                    )
                }
            }
        }
    }
}

@Composable
private fun SceneListRow(
    title: String,
    subtitle: String,
    @DrawableRes iconRes: Int,
    tileShape: Shape,
    isActive: Boolean,
    isGenerating: Boolean,
    enabled: Boolean,
    idleTileColor: Color,
    idleIconTint: Color,
    actionContentDescription: String,
    onClick: () -> Unit,
    trailingContent: (@Composable () -> Unit)? = null,
) {
    val tileBg = if (isActive) MaterialTheme.colorScheme.primaryContainer else idleTileColor
    val tileTint = if (isActive) MaterialTheme.colorScheme.onPrimaryContainer else idleIconTint
    val titleColor = if (isActive) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurface
    val actionTint =
        if (isActive) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant

    Row(
        modifier =
            Modifier.fillMaxWidth()
                .clickable(enabled = enabled, onClick = onClick)
                .padding(start = 16.dp, top = 12.dp, end = 12.dp, bottom = 12.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(
            modifier =
                Modifier.size(56.dp)
                    .clip(tileShape)
                    .background(tileBg),
            contentAlignment = Alignment.Center,
        ) {
            Icon(
                painter = painterResource(iconRes),
                contentDescription = null,
                tint = tileTint,
                modifier = Modifier.size(28.dp),
            )
        }
        Spacer(modifier = Modifier.width(16.dp))
        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = title,
                style = MaterialTheme.typography.titleMedium,
                color = titleColor,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            Text(
                text = subtitle,
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }
        Spacer(modifier = Modifier.width(8.dp))
        if (trailingContent != null) {
            trailingContent()
        } else if (isGenerating) {
            Box(
                modifier = Modifier.size(48.dp),
                contentAlignment = Alignment.Center,
            ) {
                CircularProgressIndicator(
                    modifier = Modifier.size(20.dp),
                    strokeWidth = 2.dp,
                    color = MaterialTheme.colorScheme.primary,
                )
            }
        } else {
            IconButton(onClick = onClick, enabled = enabled) {
                Icon(
                    painter = painterResource(R.drawable.ic_play_24),
                    contentDescription = actionContentDescription,
                    tint = actionTint,
                )
            }
        }
    }
}

internal fun hasBuiltinRediscoveryScene(scenes: List<SceneBanner>): Boolean =
    scenes.any {
        it.id.equals("rediscovery_90d", ignoreCase = true) ||
            it.id.equals(REDISCOVERY_BUILTIN_ID, ignoreCase = true) ||
            it.rawSceneJson.contains("\"rediscovery_90d\"", ignoreCase = true) ||
            it.rawSceneJson.contains("\"unplayed_days_gte\"", ignoreCase = true)
    }

@DrawableRes
internal fun resolveSceneIconRes(scene: SceneBanner): Int {
    val raw = scene.rawSceneJson.lowercase()
    val id = scene.id.lowercase()
    val title = scene.title.lowercase()
    return when {
        id == "rediscovery_90d" ||
            id == REDISCOVERY_BUILTIN_ID ||
            raw.contains("\"rediscovery_90d\"") ||
            raw.contains("\"unplayed_days_gte\"") -> R.drawable.ic_time_24
        id == "22222222-2222-4222-8222-222222222222" ||
            raw.contains("\"coding\"") ||
            title.contains("coding") -> R.drawable.ic_code_24
        id == "33333333-3333-4333-8333-333333333333" ||
            raw.contains("\"relax\"") ||
            title.contains("relax") -> R.drawable.ic_dark_24
        id == "55555555-5555-4555-8555-555555555555" ||
            raw.contains("\"energy\"") ||
            title.contains("energy") -> R.drawable.ic_light_24
        id == "66666666-6666-4666-8666-666666666666" ||
            raw.contains("\"pop\"") ||
            title.contains("pop") -> R.drawable.ic_song_24
        id == "11111111-1111-4111-8111-111111111111" ||
            raw.contains("\"focus\"") ||
            title.contains("focus") -> R.drawable.ic_filter_24
        else -> R.drawable.ic_playlist_24
    }
}
