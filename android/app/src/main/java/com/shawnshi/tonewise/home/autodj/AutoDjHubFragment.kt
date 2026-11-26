/*
 * Copyright (c) 2026 Tonewise Project
 * AutoDjHubFragment.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.home.autodj

import android.os.Bundle
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.Toast
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.RectangleShape
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.ui.platform.ComposeView
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.ViewCompositionStrategy
import androidx.compose.ui.platform.rememberNestedScrollInteropConnection
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.fragment.app.Fragment
import androidx.fragment.app.activityViewModels
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.shawnshi.tonewise.R
import com.shawnshi.tonewise.music.MusicViewModel
import com.shawnshi.tonewise.playback.PlaybackViewModel
import com.shawnshi.tonewise.sync.AutoDjEngine
import com.shawnshi.tonewise.sync.LanSyncRepository
import com.shawnshi.tonewise.sync.SceneBanner
import com.shawnshi.tonewise.sync.SessionSlotStore
import com.shawnshi.tonewise.ui.DynamicAccentController
import com.shawnshi.tonewise.ui.TonewiseComposeTheme
import com.shawnshi.tonewise.ui.UISettings
import com.shawnshi.tonewise.util.showToast
import dagger.hilt.android.AndroidEntryPoint
import javax.inject.Inject
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/**
 * Zune Panorama Level-1 `autodj` hub providing direct 1-tap access to time-aware 50-track
 * Auto DJ mixes, 5D preset launches, Boolean 5D filter, and LAN/Last.fm controls.
 */
@AndroidEntryPoint
class AutoDjHubFragment : Fragment() {
    @Inject lateinit var lanSyncRepository: LanSyncRepository
    @Inject lateinit var sessionSlotStore: SessionSlotStore
    @Inject lateinit var uiSettings: UISettings
    @Inject lateinit var dynamicAccentController: DynamicAccentController

    private val playbackModel: PlaybackViewModel by activityViewModels()
    private val musicModel: MusicViewModel by activityViewModels()

    override fun onCreateView(
        inflater: LayoutInflater,
        container: ViewGroup?,
        savedInstanceState: Bundle?,
    ): View =
        ComposeView(requireContext()).apply {
            setViewCompositionStrategy(ViewCompositionStrategy.DisposeOnViewTreeLifecycleDestroyed)
            setContent {
                val scenes by lanSyncRepository.scenes.collectAsStateWithLifecycle()
                val activeSceneLabel by sessionSlotStore.activeSceneLabel.collectAsStateWithLifecycle()
                val indexingState by musicModel.indexingState.collectAsStateWithLifecycle()
                val activeAccent by dynamicAccentController.activeAccentArgb.collectAsStateWithLifecycle()
                val scope = rememberCoroutineScope()
                var generatingKey by remember { mutableStateOf<String?>(null) }
                var summaryText by remember { mutableStateOf<String?>(null) }

                val timeProfile =
                    remember(indexingState) { AutoDjEngine.resolveTimeProfile() }
                val sceneCounts by
                    produceState<Map<String, Int>>(
                        initialValue = emptyMap(),
                        scenes,
                        indexingState,
                    ) {
                        value =
                            withContext(Dispatchers.Default) {
                                lanSyncRepository.countSongsForScenes(scenes)
                            }
                    }

                fun launchMix(scene: SceneBanner?) {
                    if (generatingKey != null) return
                    generatingKey = scene?.id ?: "__hero__"
                    scope.launch {
                        try {
                            val (result, ordered) =
                                withContext(Dispatchers.Default) {
                                    lanSyncRepository.generateAutoDjPlaylist(
                                        scene = scene,
                                        targetCount = AutoDjEngine.DEFAULT_TARGET_COUNT,
                                    )
                                }
                            if (!isAdded) return@launch
                            if (ordered.isNotEmpty()) {
                                val label = scene?.title ?: "Auto DJ · ${result.profile.periodLabel}"
                                sessionSlotStore.setActiveScene(label)
                                summaryText = result.summaryText
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

                TonewiseComposeTheme(
                    roundMode = uiSettings.roundMode,
                    dynamicAccentArgb = if (uiSettings.enableDynamicCoverAccent) activeAccent else null,
                ) {
                    val nestedScroll = rememberNestedScrollInteropConnection()
                    val context = LocalContext.current
                    Surface(modifier = Modifier.fillMaxSize(), color = Color.Transparent) {
                        LazyColumn(
                            modifier =
                                Modifier.fillMaxSize()
                                    .nestedScroll(nestedScroll)
                                    .navigationBarsPadding(),
                            contentPadding =
                                PaddingValues(start = 16.dp, end = 16.dp, top = 8.dp, bottom = 104.dp),
                            verticalArrangement = Arrangement.spacedBy(16.dp),
                        ) {
                            item(key = "hero") {
                                Column(
                                    modifier =
                                        Modifier.fillMaxWidth()
                                            .background(MaterialTheme.colorScheme.surfaceContainerLow)
                                            .border(1.dp, MaterialTheme.colorScheme.outlineVariant)
                                            .padding(16.dp)
                                ) {
                                    Text(
                                        text = timeProfile.periodLabel.lowercase(),
                                        fontSize = 26.sp,
                                        fontWeight = FontWeight.Light,
                                        color = MaterialTheme.colorScheme.onSurface,
                                    )
                                    Spacer(modifier = Modifier.height(4.dp))
                                    Text(
                                        text = summaryText ?: timeProfile.description,
                                        style = MaterialTheme.typography.bodySmall,
                                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                                    )
                                    Spacer(modifier = Modifier.height(14.dp))
                                    Button(
                                        onClick = { launchMix(null) },
                                        enabled = generatingKey == null,
                                        shape = RectangleShape,
                                        colors =
                                            ButtonDefaults.buttonColors(
                                                containerColor = MaterialTheme.colorScheme.primary,
                                                contentColor = MaterialTheme.colorScheme.onPrimary,
                                            ),
                                        modifier = Modifier.fillMaxWidth(),
                                    ) {
                                        Text(
                                            text =
                                                if (generatingKey == "__hero__") {
                                                    "..."
                                                } else {
                                                    stringResource(R.string.lbl_autodj_action)
                                                }
                                        )
                                    }
                                }
                            }

                            item(key = "presets_grid") {
                                Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                                    Text(
                                        text = "scenes",
                                        fontSize = 14.sp,
                                        fontWeight = FontWeight.SemiBold,
                                        letterSpacing = 0.6.sp,
                                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                                    )
                                    val rows = scenes.chunked(2)
                                    for (pair in rows) {
                                        Row(
                                            modifier = Modifier.fillMaxWidth(),
                                            horizontalArrangement = Arrangement.spacedBy(8.dp),
                                        ) {
                                            for (scene in pair) {
                                                val isActive = activeSceneLabel == scene.title
                                                val count = sceneCounts[scene.id] ?: 0
                                                Box(
                                                    modifier =
                                                        Modifier.weight(1f)
                                                            .height(88.dp)
                                                            .background(
                                                                if (isActive) {
                                                                    MaterialTheme.colorScheme.primary
                                                                } else {
                                                                    MaterialTheme.colorScheme.surfaceContainer
                                                                }
                                                            )
                                                            .border(
                                                                1.dp,
                                                                if (isActive) {
                                                                    MaterialTheme.colorScheme.primary
                                                                } else {
                                                                    MaterialTheme.colorScheme.outlineVariant
                                                                },
                                                            )
                                                            .clickable(enabled = generatingKey == null) {
                                                                launchMix(scene)
                                                            }
                                                            .padding(10.dp)
                                                ) {
                                                    Column(
                                                        modifier = Modifier.align(Alignment.BottomStart)
                                                    ) {
                                                        Text(
                                                            text = scene.title,
                                                            style = MaterialTheme.typography.titleSmall,
                                                            color =
                                                                if (isActive) {
                                                                    MaterialTheme.colorScheme.onPrimary
                                                                } else {
                                                                    MaterialTheme.colorScheme.onSurface
                                                                },
                                                            maxLines = 1,
                                                            overflow = TextOverflow.Ellipsis,
                                                        )
                                                        Text(
                                                            text =
                                                                context.resources.getQuantityString(
                                                                    R.plurals.fmt_song_count,
                                                                    count,
                                                                    count,
                                                                ),
                                                            style = MaterialTheme.typography.labelSmall,
                                                            color =
                                                                if (isActive) {
                                                                    MaterialTheme.colorScheme.onPrimary
                                                                        .copy(alpha = 0.85f)
                                                                } else {
                                                                    MaterialTheme.colorScheme.onSurfaceVariant
                                                                },
                                                        )
                                                    }
                                                }
                                            }
                                            if (pair.size == 1) {
                                                Spacer(modifier = Modifier.weight(1f))
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
}
