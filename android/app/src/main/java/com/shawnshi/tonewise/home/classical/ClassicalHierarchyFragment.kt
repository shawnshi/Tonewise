/*
 * Copyright (c) 2026 Tonewise Project
 * ClassicalHierarchyFragment.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.home.classical

import android.os.Bundle
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
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
import com.shawnshi.tonewise.detail.WorkRecordingsDialog
import com.shawnshi.tonewise.home.quickplay.QuickplayPinKind
import com.shawnshi.tonewise.home.quickplay.QuickplayPinStore
import com.shawnshi.tonewise.music.ClassicalWork
import com.shawnshi.tonewise.music.MusicRepository
import com.shawnshi.tonewise.music.MusicViewModel
import com.shawnshi.tonewise.music.resolve
import com.shawnshi.tonewise.playback.PlaybackViewModel
import com.shawnshi.tonewise.playback.formatDurationMs
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
 * Zune 3-level frameless classical hierarchy: Composer (Level 1) → Work (Level 2) → Movement
 * (Level 3), with right-aligned recording badges, multi-recording A/B launcher, and a Zune
 * full-screen 0dp `# / A–Z` jump matrix when tapping any composer header.
 */
@AndroidEntryPoint
class ClassicalHierarchyFragment : Fragment() {
    @Inject lateinit var musicRepository: MusicRepository
    @Inject lateinit var quickplayPinStore: QuickplayPinStore
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
                val indexingState by musicModel.indexingState.collectAsStateWithLifecycle()
                val activeAccent by dynamicAccentController.activeAccentArgb.collectAsStateWithLifecycle()

                val groups by
                    produceState(
                        initialValue = emptyList<ClassicalComposerGroup>(),
                        indexingState,
                    ) {
                        value =
                            withContext(Dispatchers.Default) {
                                ClassicalHierarchyLogic.groupByComposer(musicRepository.classicalWorks)
                            }
                    }

                TonewiseComposeTheme(
                    roundMode = uiSettings.roundMode,
                    dynamicAccentArgb = if (uiSettings.enableDynamicCoverAccent) activeAccent else null,
                ) {
                    ClassicalHierarchyContent(
                        groups = groups,
                        onPlayWork = { work, movementIndex ->
                            val rec = work.recordings.firstOrNull()
                            if (rec != null && rec.movements.isNotEmpty()) {
                                val safeIdx = movementIndex.coerceIn(0, rec.movements.lastIndex)
                                playbackModel.restoreSessionQueue(
                                    songs = rec.movements,
                                    index = safeIdx,
                                    positionMs = 0L,
                                )
                            }
                        },
                        onOpenRecordingsAb = { work ->
                            val rec = work.recordings.firstOrNull()
                            if (rec != null && rec.movements.isNotEmpty()) {
                                playbackModel.restoreSessionQueue(
                                    songs = rec.movements,
                                    index = 0,
                                    positionMs = 0L,
                                )
                            }
                            if (parentFragmentManager.findFragmentByTag(WorkRecordingsDialog.TAG) == null) {
                                WorkRecordingsDialog.newInstance()
                                    .show(parentFragmentManager, WorkRecordingsDialog.TAG)
                            }
                        },
                        onTogglePinWork = { work ->
                            val pinned =
                                quickplayPinStore.togglePin(
                                    kind = QuickplayPinKind.WORK,
                                    id = work.key,
                                    title = work.workTitle,
                                    subtitle = work.composerName,
                                )
                            requireContext().showToast(
                                if (pinned) R.string.lng_pinned_quickplay
                                else R.string.lng_unpinned_quickplay
                            )
                        },
                    )
                }
            }
        }
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun ClassicalHierarchyContent(
    groups: List<ClassicalComposerGroup>,
    onPlayWork: (ClassicalWork, Int) -> Unit,
    onOpenRecordingsAb: (ClassicalWork) -> Unit,
    onTogglePinWork: (ClassicalWork) -> Unit,
) {
    val context = LocalContext.current
    val listState = rememberLazyListState()
    val scope = rememberCoroutineScope()
    val nestedScroll = rememberNestedScrollInteropConnection()
    val expandedWorkKeys = remember { mutableStateMapOf<String, Boolean>() }
    var showJumpMatrix by remember { mutableStateOf(false) }

    val availableInitials =
        remember(groups) { ClassicalHierarchyLogic.extractAvailableInitials(groups) }

    Surface(modifier = Modifier.fillMaxSize(), color = Color.Transparent) {
        if (groups.isEmpty()) {
            Box(modifier = Modifier.fillMaxSize().padding(24.dp), contentAlignment = Alignment.Center) {
                Text(
                    text = stringResource(R.string.lbl_classical_empty),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            return@Surface
        }

        Box(modifier = Modifier.fillMaxSize()) {
            LazyColumn(
                state = listState,
                modifier =
                    Modifier.fillMaxSize()
                        .nestedScroll(nestedScroll)
                        .navigationBarsPadding(),
                contentPadding =
                    PaddingValues(start = 16.dp, end = 16.dp, top = 8.dp, bottom = 104.dp),
            ) {
                groups.forEachIndexed { groupIndex, group ->
                    item(key = "composer_${group.composerName}_$groupIndex") {
                        Row(
                            modifier =
                                Modifier.fillMaxWidth()
                                    .clickable { showJumpMatrix = true }
                                    .padding(top = if (groupIndex == 0) 4.dp else 18.dp, bottom = 6.dp),
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            Box(
                                modifier =
                                    Modifier.width(3.dp)
                                        .height(22.dp)
                                        .background(MaterialTheme.colorScheme.primary)
                            )
                            Spacer(modifier = Modifier.width(10.dp))
                            Text(
                                text = group.composerName,
                                fontSize = 22.sp,
                                fontWeight = FontWeight.Light,
                                color = MaterialTheme.colorScheme.onSurface,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis,
                            )
                        }
                    }

                    for (work in group.works) {
                        val isExpanded = expandedWorkKeys[work.key] ?: false
                        val primaryRec = work.recordings.firstOrNull()
                        val badge =
                            primaryRec?.let { ClassicalHierarchyLogic.formatRecordingBadge(it) }.orEmpty()

                        item(key = "work_${work.key}") {
                            Column(
                                modifier =
                                    Modifier.fillMaxWidth()
                                        .combinedClickable(
                                            onClick = {
                                                expandedWorkKeys[work.key] = !isExpanded
                                            },
                                            onLongClick = { onTogglePinWork(work) },
                                        )
                                        .padding(start = 14.dp, top = 8.dp, bottom = 8.dp)
                            ) {
                                Row(
                                    modifier = Modifier.fillMaxWidth(),
                                    horizontalArrangement = Arrangement.SpaceBetween,
                                    verticalAlignment = Alignment.CenterVertically,
                                ) {
                                    Text(
                                        text = work.workTitle,
                                        style = MaterialTheme.typography.titleMedium,
                                        color = MaterialTheme.colorScheme.onSurface,
                                        maxLines = 2,
                                        overflow = TextOverflow.Ellipsis,
                                        modifier = Modifier.weight(1f).padding(end = 8.dp),
                                    )
                                    if (work.recordings.size > 1) {
                                        Box(
                                            modifier =
                                                Modifier.background(MaterialTheme.colorScheme.primary)
                                                    .clickable { onOpenRecordingsAb(work) }
                                                    .padding(horizontal = 6.dp, vertical = 2.dp)
                                        ) {
                                            Text(
                                                text = "${work.recordings.size} rec",
                                                style = MaterialTheme.typography.labelSmall,
                                                color = MaterialTheme.colorScheme.onPrimary,
                                            )
                                        }
                                    }
                                }
                                Row(
                                    modifier = Modifier.fillMaxWidth().padding(top = 2.dp),
                                    horizontalArrangement = Arrangement.SpaceBetween,
                                    verticalAlignment = Alignment.CenterVertically,
                                ) {
                                    Text(
                                        text = badge.ifEmpty { primaryRec?.movements?.size?.toString().orEmpty() + " mov" },
                                        style = MaterialTheme.typography.labelSmall,
                                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                                        maxLines = 1,
                                        overflow = TextOverflow.Ellipsis,
                                        modifier = Modifier.weight(1f),
                                    )
                                    Text(
                                        text = if (isExpanded) "collapse" else "play / mov",
                                        style = MaterialTheme.typography.labelSmall,
                                        color = MaterialTheme.colorScheme.primary,
                                        modifier =
                                            Modifier.clickable {
                                                if (!isExpanded) {
                                                    onPlayWork(work, 0)
                                                } else {
                                                    expandedWorkKeys[work.key] = false
                                                }
                                            },
                                    )
                                }
                            }
                        }

                        if (isExpanded && primaryRec != null) {
                            primaryRec.movements.forEachIndexed { moveIdx, song ->
                                val moveTitle =
                                    song.movementName?.trim()?.takeIf { it.isNotEmpty() }
                                        ?: song.name.resolve(context)
                                val numPrefix = song.movementNumber?.let { "$it. " } ?: "${moveIdx + 1}. "
                                item(key = "mov_${work.key}_${song.uid}_$moveIdx") {
                                    Row(
                                        modifier =
                                            Modifier.fillMaxWidth()
                                                .clickable { onPlayWork(work, moveIdx) }
                                                .padding(start = 30.dp, top = 6.dp, bottom = 6.dp),
                                        horizontalArrangement = Arrangement.SpaceBetween,
                                        verticalAlignment = Alignment.CenterVertically,
                                    ) {
                                        Text(
                                            text = "$numPrefix$moveTitle",
                                            style = MaterialTheme.typography.bodySmall,
                                            color = MaterialTheme.colorScheme.onSurface,
                                            maxLines = 1,
                                            overflow = TextOverflow.Ellipsis,
                                            modifier = Modifier.weight(1f).padding(end = 8.dp),
                                        )
                                        Text(
                                            text = song.durationMs.formatDurationMs(true),
                                            style = MaterialTheme.typography.labelSmall,
                                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                                        )
                                    }
                                }
                            }
                        }
                    }
                }
            }

            if (showJumpMatrix) {
                Box(
                    modifier =
                        Modifier.fillMaxSize()
                            .background(MaterialTheme.colorScheme.surface.copy(alpha = 0.96f))
                            .clickable { showJumpMatrix = false }
                            .padding(24.dp),
                    contentAlignment = Alignment.Center,
                ) {
                    val rows = ClassicalHierarchyLogic.JUMP_MATRIX_KEYS.chunked(5)
                    Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
                        Text(
                            text = "composers a–z",
                            fontSize = 20.sp,
                            fontWeight = FontWeight.Light,
                            color = MaterialTheme.colorScheme.onSurface,
                            modifier = Modifier.padding(bottom = 8.dp),
                        )
                        for (row in rows) {
                            Row(
                                modifier = Modifier.fillMaxWidth(),
                                horizontalArrangement = Arrangement.spacedBy(6.dp),
                            ) {
                                for (key in row) {
                                    val available = key in availableInitials
                                    Box(
                                        modifier =
                                            Modifier.weight(1f)
                                                .aspectRatio(1f)
                                                .background(
                                                    if (available) {
                                                        MaterialTheme.colorScheme.primary
                                                    } else {
                                                        MaterialTheme.colorScheme.surfaceContainerLow
                                                    }
                                                )
                                                .border(
                                                    1.dp,
                                                    MaterialTheme.colorScheme.outlineVariant,
                                                )
                                                .clickable(enabled = available) {
                                                    showJumpMatrix = false
                                                    val targetItemOffset =
                                                        computeItemIndexForInitial(
                                                            groups,
                                                            expandedWorkKeys,
                                                            key,
                                                        )
                                                    if (targetItemOffset >= 0) {
                                                        scope.launch {
                                                            listState.scrollToItem(targetItemOffset)
                                                        }
                                                    }
                                                },
                                        contentAlignment = Alignment.Center,
                                    ) {
                                        Text(
                                            text = key,
                                            fontSize = 18.sp,
                                            fontWeight = FontWeight.Light,
                                            color =
                                                if (available) {
                                                    MaterialTheme.colorScheme.onPrimary
                                                } else {
                                                    MaterialTheme.colorScheme.onSurfaceVariant.copy(
                                                        alpha = 0.25f
                                                    )
                                                },
                                        )
                                    }
                                }
                                repeat(5 - row.size) { Spacer(modifier = Modifier.weight(1f)) }
                            }
                        }
                    }
                }
            }
        }
    }
}

private fun computeItemIndexForInitial(
    groups: List<ClassicalComposerGroup>,
    expandedWorkKeys: Map<String, Boolean>,
    initial: String,
): Int {
    var offset = 0
    for (group in groups) {
        if (group.initial == initial) {
            return offset
        }
        offset += 1 // composer header
        for (work in group.works) {
            offset += 1 // work row
            if (expandedWorkKeys[work.key] == true) {
                offset += work.recordings.firstOrNull()?.movements?.size ?: 0
            }
        }
    }
    return -1
}
