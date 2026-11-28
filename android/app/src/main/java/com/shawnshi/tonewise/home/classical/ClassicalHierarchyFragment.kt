/*
 * Copyright (c) 2026 Tonewise Project
 * ClassicalHierarchyFragment.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.home.classical

import android.graphics.BitmapFactory
import android.os.Bundle
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import androidx.activity.OnBackPressedCallback
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.defaultMinSize
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.SideEffect
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
import androidx.compose.ui.graphics.ColorFilter
import androidx.compose.ui.graphics.ColorMatrix
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.ComposeView
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.ViewCompositionStrategy
import androidx.compose.ui.platform.rememberNestedScrollInteropConnection
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.stateDescription
import androidx.compose.ui.text.TextStyle
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
import com.shawnshi.tonewise.home.quickplay.prominentAlbumCoverId
import com.shawnshi.tonewise.music.ClassicalWork
import com.shawnshi.tonewise.music.MusicRepository
import com.shawnshi.tonewise.music.MusicViewModel
import com.shawnshi.tonewise.music.WorkRecording
import com.shawnshi.tonewise.music.resolve
import com.shawnshi.tonewise.playback.PlaybackViewModel
import com.shawnshi.tonewise.playback.formatDurationMs
import com.shawnshi.tonewise.ui.DynamicAccentController
import com.shawnshi.tonewise.ui.LocalTonewiseAccentInk
import com.shawnshi.tonewise.ui.TonewiseComposeTheme
import com.shawnshi.tonewise.ui.UISettings
import com.shawnshi.tonewise.util.showToast
import dagger.hilt.android.AndroidEntryPoint
import java.io.File
import java.util.Locale
import javax.inject.Inject
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/**
 * Two-stage classical collection hub:
 * - Stage 1: Minimalist all-lowercase Composer index (18dp negative space, zero dividers,
 *   optional 0dp grayscale portrait only when local file exists) + full-screen #000000 0dp
 *   Metro Era & `# / a–z` Jump Matrix.
 * - Stage 2: Composer detail with Form pivot (`all / symphonies / concertos / chamber / solo`),
 *   multi-version work rows, inline horizontal recording switcher (`karajan · 1963`), and
 *   borderless 16dp-indented Roman-numeral movement rows.
 */
@AndroidEntryPoint
class ClassicalHierarchyFragment : Fragment() {
    @Inject lateinit var musicRepository: MusicRepository
    @Inject lateinit var quickplayPinStore: QuickplayPinStore
    @Inject lateinit var uiSettings: UISettings
    @Inject lateinit var dynamicAccentController: DynamicAccentController

    private val playbackModel: PlaybackViewModel by activityViewModels()
    private val musicModel: MusicViewModel by activityViewModels()

    private var showJumpMatrixState by mutableStateOf(false)
    private var selectedComposerKeyState by mutableStateOf<String?>(null)

    private val backCallback =
        object : OnBackPressedCallback(false) {
            override fun handleOnBackPressed() {
                when {
                    showJumpMatrixState -> showJumpMatrixState = false
                    selectedComposerKeyState != null -> selectedComposerKeyState = null
                }
                isEnabled = showJumpMatrixState || selectedComposerKeyState != null
            }
        }

    override fun onViewCreated(view: View, savedInstanceState: Bundle?) {
        super.onViewCreated(view, savedInstanceState)
        requireActivity()
            .onBackPressedDispatcher
            .addCallback(viewLifecycleOwner, backCallback)
    }

    override fun onCreateView(
        inflater: LayoutInflater,
        container: ViewGroup?,
        savedInstanceState: Bundle?,
    ): View =
        ComposeView(requireContext()).apply {
            setViewCompositionStrategy(ViewCompositionStrategy.DisposeOnViewTreeLifecycleDestroyed)
            setContent {
                val indexingState by musicModel.indexingState.collectAsStateWithLifecycle()
                val libraryRevision by musicRepository.libraryRevision.collectAsStateWithLifecycle()
                val activeSong by playbackModel.song.collectAsStateWithLifecycle()
                val activeAccent by dynamicAccentController.activeAccentArgb.collectAsStateWithLifecycle()

                val portraitDirs =
                    remember {
                        listOf(
                            File(requireContext().filesDir, "composers"),
                            File(requireContext().cacheDir, "composers"),
                        )
                    }

                val groups by
                    produceState(
                        initialValue = emptyList<ClassicalComposerGroup>(),
                        indexingState,
                        libraryRevision,
                    ) {
                        value =
                            withContext(Dispatchers.Default) {
                                ClassicalHierarchyLogic.groupByComposer(
                                    musicRepository.classicalWorks,
                                    portraitDirs = portraitDirs,
                                )
                            }
                    }

                TonewiseComposeTheme(
                    roundMode = uiSettings.roundMode,
                    dynamicAccentArgb = if (uiSettings.enableDynamicCoverAccent) activeAccent else null,
                ) {
                    ClassicalHierarchyContent(
                        groups = groups,
                        activeSongUid = activeSong?.uid?.toString(),
                        selectedComposerKey = selectedComposerKeyState,
                        onSelectComposer = { composerKey ->
                            selectedComposerKeyState = composerKey
                            backCallback.isEnabled = showJumpMatrixState || composerKey != null
                        },
                        showJumpMatrix = showJumpMatrixState,
                        onShowJumpMatrixChange = { visible ->
                            showJumpMatrixState = visible
                            backCallback.isEnabled = visible || selectedComposerKeyState != null
                        },
                        onPlayRecording = { rec, movementIndex ->
                            if (rec.movements.isNotEmpty()) {
                                playbackModel.playClassicalRecording(
                                    movements = rec.movements,
                                    startIndex = movementIndex,
                                )
                            }
                        },
                        onOpenRecordingsAb = { work ->
                            if (parentFragmentManager.findFragmentByTag(WorkRecordingsDialog.TAG) == null) {
                                WorkRecordingsDialog.newInstance(work.key)
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
                                    coverId =
                                        work.recordings
                                            .firstOrNull()
                                            ?.album
                                            ?.let(::prominentAlbumCoverId)
                                            .orEmpty(),
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
    activeSongUid: String?,
    selectedComposerKey: String?,
    onSelectComposer: (String?) -> Unit,
    showJumpMatrix: Boolean,
    onShowJumpMatrixChange: (Boolean) -> Unit,
    onPlayRecording: (WorkRecording, Int) -> Unit,
    onOpenRecordingsAb: (ClassicalWork) -> Unit,
    onTogglePinWork: (ClassicalWork) -> Unit,
) {
    val context = LocalContext.current
    val stage1ListState = rememberLazyListState()
    val stage2ListState = rememberLazyListState()
    val scope = rememberCoroutineScope()
    val nestedScroll = rememberNestedScrollInteropConnection()

    var selectedEraFilter by remember { mutableStateOf<String?>(null) }
    var selectedFormFilter by remember(selectedComposerKey) { mutableStateOf("all") }
    val expandedWorkKeys = remember(selectedComposerKey) { mutableStateMapOf<String, Boolean>() }
    val selectedRecordingIdx = remember(selectedComposerKey) { mutableStateMapOf<String, Int>() }

    val filteredGroups =
        remember(groups, selectedEraFilter) {
            ClassicalHierarchyLogic.filterComposersByEra(groups, selectedEraFilter)
        }
    val availableInitials =
        remember(filteredGroups) { ClassicalHierarchyLogic.extractAvailableInitials(filteredGroups) }

    val selectedGroup =
        remember(groups, selectedComposerKey) {
            selectedComposerKey?.let { key ->
                groups.firstOrNull { it.composerName.equals(key, ignoreCase = true) }
            }
        }

    val jumpHint = stringResource(R.string.desc_classical_jump_hint)
    val pinActionLabel = stringResource(R.string.desc_classical_pin_action)
    val accentInk = LocalTonewiseAccentInk.current
    val accentFill = MaterialTheme.colorScheme.primary

    SideEffect {
        onShowJumpMatrixChange(showJumpMatrix)
    }

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
            if (selectedGroup == null) {
                // Stage 1: All-lowercase composer index
                Column(modifier = Modifier.fillMaxSize()) {
                    Row(
                        modifier =
                            Modifier.fillMaxWidth()
                                .padding(horizontal = 16.dp, vertical = 6.dp),
                        horizontalArrangement = Arrangement.SpaceBetween,
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Row(
                            modifier = Modifier.horizontalScroll(rememberScrollState()),
                            horizontalArrangement = Arrangement.spacedBy(14.dp),
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            Text(
                                text = stringResource(R.string.lbl_classical_all_eras),
                                style = MaterialTheme.typography.labelLarge,
                                color = if (selectedEraFilter == null) accentInk else MaterialTheme.colorScheme.onSurfaceVariant,
                                modifier =
                                    Modifier.defaultMinSize(minWidth = 48.dp, minHeight = 48.dp)
                                        .clickable { selectedEraFilter = null }
                                        .padding(horizontal = 4.dp, vertical = 14.dp),
                            )
                            for (era in ClassicalHierarchyLogic.ERA_KEYS) {
                                val eraLabel = eraDisplayLabel(era)
                                Text(
                                    text = eraLabel,
                                    style = MaterialTheme.typography.labelLarge,
                                    color = if (selectedEraFilter == era) accentInk else MaterialTheme.colorScheme.onSurfaceVariant,
                                    modifier =
                                        Modifier.defaultMinSize(minWidth = 48.dp, minHeight = 48.dp)
                                            .clickable {
                                                selectedEraFilter = if (selectedEraFilter == era) null else era
                                            }
                                            .padding(horizontal = 4.dp, vertical = 14.dp),
                                )
                            }
                        }

                        Box(
                            modifier =
                                Modifier.defaultMinSize(minWidth = 48.dp, minHeight = 48.dp)
                                    .semantics { contentDescription = jumpHint }
                                    .clickable(onClickLabel = jumpHint) { onShowJumpMatrixChange(true) }
                                    .padding(horizontal = 8.dp),
                            contentAlignment = Alignment.CenterEnd,
                        ) {
                            Text(
                                text = "a–z",
                                style = MaterialTheme.typography.labelMedium,
                                color = accentInk,
                            )
                        }
                    }

                    LazyColumn(
                        state = stage1ListState,
                        modifier =
                            Modifier.fillMaxSize()
                                .nestedScroll(nestedScroll)
                                .navigationBarsPadding(),
                        verticalArrangement = Arrangement.spacedBy(18.dp),
                        contentPadding = PaddingValues(start = 16.dp, end = 16.dp, top = 8.dp, bottom = 104.dp),
                    ) {
                        itemsIndexed(
                            items = filteredGroups,
                            key = { idx, g -> "composer_${g.composerName}_$idx" },
                        ) { _, group ->
                            Row(
                                modifier =
                                    Modifier.fillMaxWidth()
                                        .defaultMinSize(minHeight = 48.dp)
                                        .clickable { onSelectComposer(group.composerName) },
                                verticalAlignment = Alignment.CenterVertically,
                            ) {
                                val portraitPath = group.localPortraitPath
                                val portraitBitmap =
                                    remember(portraitPath) {
                                        portraitPath?.let { BitmapFactory.decodeFile(it)?.asImageBitmap() }
                                    }
                                if (portraitBitmap != null) {
                                    Image(
                                        bitmap = portraitBitmap,
                                        contentDescription = group.composerName,
                                        contentScale = ContentScale.Crop,
                                        colorFilter =
                                            ColorFilter.colorMatrix(
                                                ColorMatrix().apply { setToSaturation(0f) }
                                            ),
                                        modifier = Modifier.size(44.dp),
                                    )
                                    Spacer(modifier = Modifier.width(14.dp))
                                }

                                Column(modifier = Modifier.weight(1f)) {
                                    Text(
                                        text = group.displayLowercase,
                                        fontSize = 24.sp,
                                        fontWeight = FontWeight.Light,
                                        color = MaterialTheme.colorScheme.onSurface,
                                        maxLines = 1,
                                        overflow = TextOverflow.Ellipsis,
                                    )
                                    Spacer(modifier = Modifier.height(2.dp))
                                    Text(
                                        text = ClassicalHierarchyLogic.formatComposerStatsLine(group),
                                        style = MaterialTheme.typography.labelMedium,
                                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                                        maxLines = 1,
                                        overflow = TextOverflow.Ellipsis,
                                    )
                                }

                                Text(
                                    text = group.initial.lowercase(Locale.ROOT),
                                    style = MaterialTheme.typography.labelSmall,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.65f),
                                    modifier =
                                        Modifier.defaultMinSize(minWidth = 48.dp, minHeight = 48.dp)
                                            .clickable(onClickLabel = jumpHint) { onShowJumpMatrixChange(true) }
                                            .padding(start = 12.dp, top = 14.dp, bottom = 14.dp),
                                )
                            }
                        }
                    }
                }
            } else {
                // Stage 2: Composer Detail (Form filter + Multi-version Work + Horizontal Switcher + Movements)
                val visibleWorks =
                    remember(selectedGroup.works, selectedFormFilter) {
                        ClassicalHierarchyLogic.filterWorksByForm(selectedGroup.works, selectedFormFilter)
                    }
                val autoExpandAll = visibleWorks.size <= 3

                Column(modifier = Modifier.fillMaxSize()) {
                    Row(
                        modifier =
                            Modifier.fillMaxWidth()
                                .padding(horizontal = 12.dp, vertical = 4.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Box(
                            modifier =
                                Modifier.defaultMinSize(minWidth = 48.dp, minHeight = 48.dp)
                                    .clickable { onSelectComposer(null) },
                            contentAlignment = Alignment.Center,
                        ) {
                            Icon(
                                painter = painterResource(R.drawable.ic_back_24),
                                contentDescription = stringResource(R.string.desc_classical_back_composers),
                                tint = MaterialTheme.colorScheme.onSurface,
                            )
                        }
                        Spacer(modifier = Modifier.width(4.dp))
                        Column(modifier = Modifier.weight(1f)) {
                            Text(
                                text = selectedGroup.displayLowercase,
                                fontSize = 26.sp,
                                fontWeight = FontWeight.Light,
                                color = MaterialTheme.colorScheme.onSurface,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis,
                            )
                            Text(
                                text = ClassicalHierarchyLogic.formatComposerStatsLine(selectedGroup),
                                style = MaterialTheme.typography.labelSmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        }
                    }

                    // Pure-text Form Pivot strip
                    Row(
                        modifier =
                            Modifier.fillMaxWidth()
                                .horizontalScroll(rememberScrollState())
                                .padding(horizontal = 16.dp, vertical = 4.dp),
                        horizontalArrangement = Arrangement.spacedBy(18.dp),
                    ) {
                        for (formKey in ClassicalHierarchyLogic.FORM_FILTERS) {
                            val active = selectedFormFilter == formKey
                            Text(
                                text = formDisplayLabel(formKey),
                                fontSize = 14.sp,
                                fontWeight = if (active) FontWeight.SemiBold else FontWeight.Normal,
                                color = if (active) accentInk else MaterialTheme.colorScheme.onSurfaceVariant,
                                modifier =
                                    Modifier.defaultMinSize(minWidth = 48.dp, minHeight = 48.dp)
                                        .clickable { selectedFormFilter = formKey }
                                        .padding(vertical = 12.dp),
                            )
                        }
                    }

                    LazyColumn(
                        state = stage2ListState,
                        modifier =
                            Modifier.fillMaxSize()
                                .nestedScroll(nestedScroll)
                                .navigationBarsPadding(),
                        verticalArrangement = Arrangement.spacedBy(14.dp),
                        contentPadding = PaddingValues(start = 16.dp, end = 16.dp, top = 8.dp, bottom = 104.dp),
                    ) {
                        for (work in visibleWorks) {
                            val isExpanded = expandedWorkKeys[work.key] ?: autoExpandAll
                            val recIndex = (selectedRecordingIdx[work.key] ?: 0).coerceIn(0, maxOf(0, work.recordings.lastIndex))
                            val selectedRec = work.recordings.getOrNull(recIndex)

                            item(key = "work_${work.key}") {
                                val versionLabel =
                                    if (work.recordings.size > 1) {
                                        stringResource(R.string.fmt_classical_version_count, work.recordings.size)
                                    } else {
                                        stringResource(R.string.fmt_classical_single_version)
                                    }
                                val recBrowseDesc = stringResource(R.string.desc_open_work_recordings, work.recordings.size)
                                Column(
                                    modifier =
                                        Modifier.fillMaxWidth()
                                            .defaultMinSize(minHeight = 48.dp)
                                            .semantics {
                                                stateDescription = if (isExpanded) "Expanded" else "Collapsed"
                                            }
                                            .combinedClickable(
                                                onClick = { expandedWorkKeys[work.key] = !isExpanded },
                                                onLongClickLabel = pinActionLabel,
                                                onLongClick = { onTogglePinWork(work) },
                                            )
                                            .padding(vertical = 4.dp),
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
                                            modifier = Modifier.weight(1f).padding(end = 10.dp),
                                        )

                                        Row(verticalAlignment = Alignment.CenterVertically) {
                                            Box(
                                                modifier =
                                                    Modifier.defaultMinSize(minWidth = 48.dp, minHeight = 48.dp)
                                                        .semantics { contentDescription = recBrowseDesc }
                                                        .clickable(onClickLabel = recBrowseDesc) {
                                                            if (work.recordings.size > 1) {
                                                                onOpenRecordingsAb(work)
                                                            } else {
                                                                expandedWorkKeys[work.key] = !isExpanded
                                                            }
                                                        },
                                                contentAlignment = Alignment.CenterEnd,
                                            ) {
                                                Text(
                                                    text = versionLabel,
                                                    style = MaterialTheme.typography.labelMedium,
                                                    color = accentInk,
                                                )
                                            }
                                            Spacer(modifier = Modifier.width(8.dp))
                                            Box(
                                                modifier =
                                                    Modifier.defaultMinSize(minWidth = 48.dp, minHeight = 48.dp)
                                                        .clickable {
                                                            selectedRec?.let { onPlayRecording(it, 0) }
                                                        },
                                                contentAlignment = Alignment.Center,
                                            ) {
                                                Icon(
                                                    painter = painterResource(R.drawable.ic_play_24),
                                                    contentDescription = stringResource(R.string.desc_classical_play_whole_work),
                                                    tint = accentInk,
                                                    modifier = Modifier.size(20.dp),
                                                )
                                            }
                                        }
                                    }

                                    // Inline horizontal recording version switcher
                                    if (isExpanded && work.recordings.isNotEmpty()) {
                                        Row(
                                            modifier =
                                                Modifier.fillMaxWidth()
                                                    .horizontalScroll(rememberScrollState())
                                                    .padding(top = 6.dp, bottom = 4.dp),
                                            horizontalArrangement = Arrangement.spacedBy(8.dp),
                                        ) {
                                            work.recordings.forEachIndexed { idx, rec ->
                                                val recActive = idx == recIndex
                                                val badgeText =
                                                    ClassicalHierarchyLogic.formatRecordingBadge(rec)
                                                        .ifEmpty { rec.album.name.resolve(context).lowercase(Locale.ROOT) }
                                                Box(
                                                    modifier =
                                                        Modifier.defaultMinSize(minHeight = 48.dp)
                                                            .border(
                                                                width = 1.dp,
                                                                color = if (recActive) accentInk else MaterialTheme.colorScheme.outlineVariant,
                                                            )
                                                            .background(
                                                                if (recActive) accentInk.copy(alpha = 0.14f) else Color.Transparent
                                                            )
                                                            .clickable {
                                                                selectedRecordingIdx[work.key] = idx
                                                            }
                                                            .padding(horizontal = 12.dp, vertical = 12.dp),
                                                    contentAlignment = Alignment.Center,
                                                ) {
                                                    Text(
                                                        text = badgeText,
                                                        style = MaterialTheme.typography.labelSmall,
                                                        color = if (recActive) accentInk else MaterialTheme.colorScheme.onSurfaceVariant,
                                                        maxLines = 1,
                                                    )
                                                }
                                            }
                                        }
                                    }
                                }
                            }

                            // Borderless 16dp-indented Roman-numeral movement rows
                            if (isExpanded && selectedRec != null) {
                                selectedRec.movements.forEachIndexed { moveIdx, song ->
                                    val isPlayingMvt = activeSongUid == song.uid.toString()
                                    val (roman, moveTitle) =
                                        ClassicalHierarchyLogic.formatMovementDisplay(
                                            rawTitle = song.name.resolve(context),
                                            explicitWork = song.workName,
                                            explicitMovement = song.movementName,
                                            explicitMovementNumber = song.movementNumber,
                                            index = moveIdx,
                                        )
                                    item(key = "mov_${work.key}_${selectedRec.scopeKey}_${song.uid}_$moveIdx") {
                                        Row(
                                            modifier =
                                                Modifier.fillMaxWidth()
                                                    .defaultMinSize(minHeight = 48.dp)
                                                    .clickable { onPlayRecording(selectedRec, moveIdx) }
                                                    .padding(start = 16.dp, top = 8.dp, bottom = 8.dp),
                                            verticalAlignment = Alignment.CenterVertically,
                                        ) {
                                            Text(
                                                text = roman,
                                                style =
                                                    MaterialTheme.typography.bodySmall.merge(
                                                        TextStyle(fontFeatureSettings = "tnum")
                                                    ),
                                                color = if (isPlayingMvt) accentInk else MaterialTheme.colorScheme.onSurfaceVariant,
                                                modifier = Modifier.width(34.dp),
                                            )
                                            Text(
                                                text = moveTitle,
                                                style = MaterialTheme.typography.bodyMedium,
                                                color = if (isPlayingMvt) accentInk else MaterialTheme.colorScheme.onSurface,
                                                maxLines = 1,
                                                overflow = TextOverflow.Ellipsis,
                                                modifier = Modifier.weight(1f).padding(end = 10.dp),
                                            )
                                            Text(
                                                text = song.durationMs.formatDurationMs(true),
                                                style =
                                                    MaterialTheme.typography.labelSmall.merge(
                                                        TextStyle(fontFeatureSettings = "tnum")
                                                    ),
                                                color = if (isPlayingMvt) accentInk else MaterialTheme.colorScheme.onSurfaceVariant,
                                            )
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // Full-screen themed 0dp Metro Era + A–Z Jump Matrix
            if (showJumpMatrix) {
                Box(
                    modifier =
                        Modifier.fillMaxSize()
                            .background(MaterialTheme.colorScheme.background.copy(alpha = 0.96f))
                            .clickable { onShowJumpMatrixChange(false) }
                            .padding(24.dp),
                    contentAlignment = Alignment.Center,
                ) {
                    val rows = ClassicalHierarchyLogic.JUMP_MATRIX_KEYS.chunked(5)
                    Column(
                        modifier = Modifier.verticalScroll(rememberScrollState()),
                        verticalArrangement = Arrangement.spacedBy(6.dp),
                    ) {
                        Text(
                            text = stringResource(R.string.lbl_composers_az),
                            fontSize = 20.sp,
                            fontWeight = FontWeight.Light,
                            color = MaterialTheme.colorScheme.onBackground,
                            modifier = Modifier.padding(bottom = 8.dp),
                        )

                        // Era tiles
                        Row(
                            modifier = Modifier.fillMaxWidth().padding(bottom = 8.dp),
                            horizontalArrangement = Arrangement.spacedBy(6.dp),
                        ) {
                            for (era in ClassicalHierarchyLogic.ERA_KEYS) {
                                val eraActive = selectedEraFilter == era
                                Box(
                                    modifier =
                                        Modifier.weight(1f)
                                            .defaultMinSize(minHeight = 48.dp)
                                            .background(if (eraActive) accentFill else MaterialTheme.colorScheme.surfaceContainer)
                                            .border(1.dp, if (eraActive) accentFill else MaterialTheme.colorScheme.outline)
                                            .clickable {
                                                selectedEraFilter = if (selectedEraFilter == era) null else era
                                                onSelectComposer(null)
                                                onShowJumpMatrixChange(false)
                                            },
                                    contentAlignment = Alignment.Center,
                                ) {
                                    Text(
                                        text = era,
                                        fontSize = 12.sp,
                                        color = if (eraActive) MaterialTheme.colorScheme.onPrimary else MaterialTheme.colorScheme.onSurface,
                                        maxLines = 1,
                                    )
                                }
                            }
                        }

                        // # and A–Z tiles
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
                                                    if (available) accentFill else MaterialTheme.colorScheme.surfaceContainerLow
                                                )
                                                .border(
                                                    1.dp,
                                                    if (available) accentFill else MaterialTheme.colorScheme.outlineVariant,
                                                )
                                                .clickable(enabled = available) {
                                                    onSelectComposer(null)
                                                    onShowJumpMatrixChange(false)
                                                    val idx =
                                                        filteredGroups.indexOfFirst { it.initial == key }
                                                    if (idx >= 0) {
                                                        scope.launch {
                                                            stage1ListState.scrollToItem(idx)
                                                        }
                                                    }
                                                },
                                        contentAlignment = Alignment.Center,
                                    ) {
                                        Text(
                                            text = key.lowercase(Locale.ROOT),
                                            fontSize = 18.sp,
                                            fontWeight = FontWeight.Light,
                                            color =
                                                if (available) {
                                                    MaterialTheme.colorScheme.onPrimary
                                                } else {
                                                    MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.45f)
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

@Composable
private fun eraDisplayLabel(era: String): String =
    when (era) {
        "baroque" -> stringResource(R.string.lbl_classical_era_baroque)
        "classical" -> stringResource(R.string.lbl_classical_era_classical)
        "romantic" -> stringResource(R.string.lbl_classical_era_romantic)
        "modern" -> stringResource(R.string.lbl_classical_era_modern)
        else -> era
    }

@Composable
private fun formDisplayLabel(form: String): String =
    when (form) {
        "all" -> stringResource(R.string.lbl_classical_all_forms)
        "symphonies" -> stringResource(R.string.lbl_classical_form_symphonies)
        "concertos" -> stringResource(R.string.lbl_classical_form_concertos)
        "chamber" -> stringResource(R.string.lbl_classical_form_chamber)
        "solo" -> stringResource(R.string.lbl_classical_form_solo)
        else -> form
    }
