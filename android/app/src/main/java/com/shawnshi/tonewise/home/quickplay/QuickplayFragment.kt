/*
 * Copyright (c) 2026 Tonewise Project
 * QuickplayFragment.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.home.quickplay

import android.os.Bundle
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.Toast
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
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.nestedscroll.NestedScrollConnection
import androidx.compose.ui.input.nestedscroll.NestedScrollSource
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.ui.platform.ComposeView
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.platform.ViewCompositionStrategy
import androidx.compose.ui.platform.rememberNestedScrollInteropConnection
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.fragment.app.Fragment
import androidx.fragment.app.activityViewModels
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.lifecycleScope
import com.shawnshi.tonewise.R
import com.shawnshi.tonewise.detail.DetailViewModel
import com.shawnshi.tonewise.home.HomeViewModel
import com.shawnshi.tonewise.image.CoverView
import com.shawnshi.tonewise.music.ClassicalWork
import com.shawnshi.tonewise.music.MusicRepository
import com.shawnshi.tonewise.music.resolve
import com.shawnshi.tonewise.music.resolveNames
import com.shawnshi.tonewise.playback.PlaySong
import com.shawnshi.tonewise.playback.PlaybackViewModel
import com.shawnshi.tonewise.playback.persist.PlayCountRepository
import com.shawnshi.tonewise.sync.AutoDjEngine
import com.shawnshi.tonewise.sync.LanSyncRepository
import com.shawnshi.tonewise.sync.SceneBanner
import com.shawnshi.tonewise.sync.SessionSlotItem
import com.shawnshi.tonewise.sync.SessionSlotStore
import com.shawnshi.tonewise.ui.LocalTonewiseAccentInk
import com.shawnshi.tonewise.ui.DynamicAccentController
import com.shawnshi.tonewise.ui.TonewiseComposeTheme
import com.shawnshi.tonewise.ui.UISettings
import com.shawnshi.tonewise.util.showToast
import dagger.hilt.android.AndroidEntryPoint
import javax.inject.Inject
import kotlin.math.abs
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.oxycblt.musikr.Album
import org.oxycblt.musikr.Artist
import org.oxycblt.musikr.Music
import org.oxycblt.musikr.Playlist
import org.oxycblt.musikr.Song

@AndroidEntryPoint
class QuickplayFragment : Fragment() {
    @Inject lateinit var musicRepository: MusicRepository
    @Inject lateinit var playCountRepository: PlayCountRepository
    @Inject lateinit var quickplayPinStore: QuickplayPinStore
    @Inject lateinit var sessionSlotStore: SessionSlotStore
    @Inject lateinit var lanSyncRepository: LanSyncRepository
    @Inject lateinit var uiSettings: UISettings
    @Inject lateinit var dynamicAccentController: DynamicAccentController

    private val playbackModel: PlaybackViewModel by activityViewModels()
    private val detailModel: DetailViewModel by activityViewModels()
    private val homeModel: HomeViewModel by activityViewModels()

    override fun onCreateView(
        inflater: LayoutInflater,
        container: ViewGroup?,
        savedInstanceState: Bundle?,
    ): View =
        ComposeView(requireContext()).apply {
            setViewCompositionStrategy(ViewCompositionStrategy.DisposeOnViewTreeLifecycleDestroyed)
            setContent {
                val pins by quickplayPinStore.pins.collectAsStateWithLifecycle()
                val slots by sessionSlotStore.slots.collectAsStateWithLifecycle()
                val scenes by lanSyncRepository.scenes.collectAsStateWithLifecycle()
                val activeSlotIndex by sessionSlotStore.activeSlotIndex.collectAsStateWithLifecycle()
                val activeSceneLabel by sessionSlotStore.activeSceneLabel.collectAsStateWithLifecycle()
                val libraryRevision by musicRepository.libraryRevision.collectAsStateWithLifecycle()
                val activeAccent by dynamicAccentController.activeAccentArgb.collectAsStateWithLifecycle()
                val sections by homeModel.quickplaySections.collectAsStateWithLifecycle()
                val presetScenes =
                    remember(scenes) {
                        scenes.filterNot { it.id == "44444444-4444-4444-8444-444444444444" }.take(5)
                    }
                val sceneCounts by
                    androidx.compose.runtime.produceState<Map<String, Int>>(
                        initialValue = emptyMap(),
                        presetScenes,
                        libraryRevision,
                    ) {
                        value =
                            withContext(Dispatchers.Default) {
                                lanSyncRepository.countSongsForScenes(presetScenes)
                            }
                    }

                TonewiseComposeTheme(
                    roundMode = uiSettings.roundMode,
                    dynamicAccentArgb = if (uiSettings.enableDynamicCoverAccent) activeAccent else null,
                ) {
                    QuickplayContent(
                        pins = pins,
                        slots = slots,
                        presetScenes = presetScenes,
                        sceneCounts = sceneCounts,
                        activeSlotIndex = activeSlotIndex,
                        sections = sections,
                        libraryRevision = libraryRevision,
                        resolveCoverAlbum = { albumUid ->
                            val uid = Music.UID.fromString(albumUid)
                            uid?.let { musicRepository.library?.findAlbum(it) }
                        },
                        resolvePinCoverTarget = { pin -> resolvePinTarget(pin) },
                        onTapPin = { pin -> handleTapPin(pin) },
                        onUnpin = { pin ->
                            quickplayPinStore.removePin(pin.kind, pin.id)
                            requireContext().showToast(R.string.lng_unpinned_quickplay)
                        },
                        onTapSlot = { slotIdx, existing, presetScene ->
                            handleTapRelaySlot(
                                slotIndex = slotIdx,
                                activeSlotIndex = activeSlotIndex,
                                activeSceneLabel = activeSceneLabel,
                                slots = slots,
                                existing = existing,
                                presetScene = presetScene,
                            )
                        },
                        onLongPressSlot = { slotIdx ->
                            saveCurrentQueueToSlot(slotIdx, activeSceneLabel, activate = true)
                        },
                        onTapAlbumSummary = { summary ->
                            val lib = musicRepository.library
                            if (lib == null) {
                                requireContext().showToast(R.string.lbl_indexer)
                            } else {
                                val uid = Music.UID.fromString(summary.albumUid)
                                val album = uid?.let { lib.findAlbum(it) }
                                if (album != null) {
                                    playbackModel.play(album)
                                }
                            }
                        },
                        onLongPressAlbumSummary = { summary ->
                            val uid = Music.UID.fromString(summary.albumUid)
                            val album = uid?.let { musicRepository.library?.findAlbum(it) }
                            if (album != null) {
                                detailModel.showAlbum(album)
                            }
                        },
                        onLaunchRediscovery = { launch90dRediscovery() },
                    )
                }
            }
        }

    private fun resolvePinTarget(pin: QuickplayPinRecord): Any? {
        val lib = musicRepository.library ?: return null
        return when (pin.kind) {
            QuickplayPinKind.ALBUM -> Music.UID.fromString(pin.id)?.let(lib::findAlbum)
            QuickplayPinKind.ARTIST -> Music.UID.fromString(pin.id)?.let(musicRepository::findArtist)
            QuickplayPinKind.PLAYLIST -> Music.UID.fromString(pin.id)?.let(lib::findPlaylist)
            QuickplayPinKind.SONG -> Music.UID.fromString(pin.id)?.let(lib::findSong)
            QuickplayPinKind.WORK ->
                musicRepository.classicalWorks.firstOrNull { it.key == pin.id }?.recordings?.firstOrNull()?.album
        }
    }

    private fun handleTapPin(pin: QuickplayPinRecord) {
        val lib = musicRepository.library
        if (lib == null) {
            requireContext().showToast(R.string.lbl_indexer)
            return
        }
        when (pin.kind) {
            QuickplayPinKind.ALBUM -> {
                val album = Music.UID.fromString(pin.id)?.let(lib::findAlbum)
                if (album != null) playbackModel.play(album)
            }
            QuickplayPinKind.ARTIST -> {
                val artist = Music.UID.fromString(pin.id)?.let(musicRepository::findArtist)
                if (artist != null) playbackModel.play(artist)
            }
            QuickplayPinKind.PLAYLIST -> {
                val playlist = Music.UID.fromString(pin.id)?.let(lib::findPlaylist)
                if (playlist != null) playbackModel.play(playlist)
            }
            QuickplayPinKind.SONG -> {
                val song = Music.UID.fromString(pin.id)?.let(lib::findSong)
                if (song != null) playbackModel.play(song, PlaySong.ByItself)
            }
            QuickplayPinKind.WORK -> {
                val work = musicRepository.classicalWorks.firstOrNull { it.key == pin.id }
                val rec = work?.recordings?.firstOrNull()
                if (rec != null && rec.movements.isNotEmpty()) {
                    playbackModel.playClassicalRecording(rec.movements, 0)
                }
            }
        }
    }

    private fun launch90dRediscovery() {
        viewLifecycleOwner.lifecycleScope.launch {
            val banner =
                SceneBanner(
                    id = "44444444-4444-4444-8444-444444444444",
                    title = getString(R.string.lbl_rediscovery_90d_title),
                    subtitle = getString(R.string.lbl_rediscovery_90d_subtitle),
                    accentHex = "#FF1E7A",
                    rawSceneJson = com.shawnshi.tonewise.sync.SceneRuleEvaluator.REDISCOVERY_90D_SCENE_JSON,
                )
            val (_, ordered) =
                withContext(Dispatchers.Default) {
                    lanSyncRepository.generateAutoDjPlaylist(
                        scene = banner,
                        targetCount = AutoDjEngine.DEFAULT_TARGET_COUNT,
                    )
                }
            if (!isAdded) return@launch
            if (ordered.isNotEmpty()) {
                sessionSlotStore.setActiveScene(banner.title)
                playbackModel.play(ordered)
            } else {
                requireContext().showToast(R.string.lbl_scene_no_matches)
            }
        }
    }

    private fun saveCurrentQueueToSlot(
        slotIndex: Int,
        sceneLabel: String,
        showFeedback: Boolean = true,
        activate: Boolean = true,
    ) {
        val pagerQueue = playbackModel.pagerQueue.value
        val snapshotSongs = pagerQueue.queue.toList()
        if (snapshotSongs.isEmpty()) {
            if (showFeedback) {
                requireContext().showToast(R.string.lbl_slot_empty_queue)
            }
            return
        }
        val title =
            sceneLabel
                .takeUnless { it.equals("Default", ignoreCase = true) || it.isBlank() }
                ?: playbackModel.song.value?.name?.raw
                ?: getString(R.string.fmt_queue_slot, slotIndex)
        val curIdx = pagerQueue.index
        val posMs = playbackModel.positionDs.value * 100L
        val nowMs = System.currentTimeMillis()
        val feedbackText = getString(R.string.fmt_scene_switched_slot, slotIndex, title)
        viewLifecycleOwner.lifecycleScope.launch {
            val canonicalUids = lanSyncRepository.resolveCanonicalTrackUids(snapshotSongs)
            sessionSlotStore.saveSlot(
                SessionSlotItem(
                    slotIndex = slotIndex,
                    name = title,
                    sceneId = null,
                    updatedAtMs = nowMs,
                    queueTrackUids = canonicalUids,
                    currentIndex = curIdx,
                    positionMs = posMs,
                ),
                activate = activate,
            )
            lanSyncRepository.scheduleM3u8WriteBack(title, snapshotSongs)
            if (showFeedback && isAdded) {
                Toast.makeText(requireContext(), feedbackText, Toast.LENGTH_SHORT).show()
            }
        }
    }

    private fun handleTapRelaySlot(
        slotIndex: Int,
        activeSlotIndex: Int,
        activeSceneLabel: String,
        slots: List<SessionSlotItem>,
        existing: SessionSlotItem?,
        presetScene: SceneBanner?,
    ) {
        val outgoingPopulated =
            SessionSlotStore.isSlotPopulated(slots.firstOrNull { it.slotIndex == activeSlotIndex })
        if (slotIndex == activeSlotIndex) {
            if (existing != null && existing.queueTrackUids.isNotEmpty()) {
                saveCurrentQueueToSlot(slotIndex, activeSceneLabel, showFeedback = true, activate = true)
                return
            }
        } else if (outgoingPopulated) {
            saveCurrentQueueToSlot(activeSlotIndex, activeSceneLabel, showFeedback = false, activate = false)
        }
        if (existing == null || existing.queueTrackUids.isEmpty()) {
            if (presetScene != null) {
                viewLifecycleOwner.lifecycleScope.launch {
                    val (_, ordered) =
                        withContext(Dispatchers.Default) {
                            lanSyncRepository.generateAutoDjPlaylist(
                                scene = presetScene,
                                targetCount = AutoDjEngine.DEFAULT_TARGET_COUNT,
                            )
                        }
                    if (!isAdded) return@launch
                    if (ordered.isEmpty()) {
                        requireContext().showToast(R.string.lbl_scene_no_matches)
                        return@launch
                    }
                    val canonicalUids = lanSyncRepository.resolveCanonicalTrackUids(ordered)
                    sessionSlotStore.saveSlot(
                        SessionSlotItem(
                            slotIndex = slotIndex,
                            name = presetScene.title,
                            sceneId = presetScene.id,
                            updatedAtMs = System.currentTimeMillis(),
                            queueTrackUids = canonicalUids,
                            currentIndex = 0,
                            positionMs = 0L,
                        ),
                        activate = true,
                    )
                    playbackModel.play(ordered)
                    Toast.makeText(
                            requireContext(),
                            getString(R.string.fmt_scene_switched_slot, slotIndex, presetScene.title),
                            Toast.LENGTH_SHORT,
                        )
                        .show()
                }
            } else {
                saveCurrentQueueToSlot(slotIndex, activeSceneLabel, showFeedback = true, activate = true)
            }
            return
        }
        viewLifecycleOwner.lifecycleScope.launch {
            val resolved = lanSyncRepository.resolveSlotSongs(existing)
            if (!isAdded) return@launch
            if (resolved.isEmpty()) {
                requireContext().showToast(R.string.lbl_scene_no_matches)
                return@launch
            }
            sessionSlotStore.setActiveScene(existing.name, slotIndex)
            playbackModel.restoreSessionQueue(
                songs = resolved,
                index = existing.currentIndex.coerceIn(0, resolved.lastIndex),
                positionMs = existing.positionMs,
            )
            Toast.makeText(
                    requireContext(),
                    getString(R.string.fmt_scene_switched_slot, slotIndex, existing.name),
                    Toast.LENGTH_SHORT,
                )
                .show()
        }
    }
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun QuickplayContent(
    pins: List<QuickplayPinRecord>,
    slots: List<SessionSlotItem>,
    presetScenes: List<SceneBanner>,
    sceneCounts: Map<String, Int>,
    activeSlotIndex: Int,
    sections: QuickplaySections,
    libraryRevision: Long,
    resolveCoverAlbum: (String) -> Album?,
    resolvePinCoverTarget: (QuickplayPinRecord) -> Any?,
    onTapPin: (QuickplayPinRecord) -> Unit,
    onUnpin: (QuickplayPinRecord) -> Unit,
    onTapSlot: (Int, SessionSlotItem?, SceneBanner?) -> Unit,
    onLongPressSlot: (Int) -> Unit,
    onTapAlbumSummary: (QuickplayAlbumSummary) -> Unit,
    onLongPressAlbumSummary: (QuickplayAlbumSummary) -> Unit,
    onLaunchRediscovery: () -> Unit,
) {
    val nestedScroll = rememberNestedScrollInteropConnection()
    val hostView = LocalView.current
    val horizontalRowNestedLock =
        remember(hostView) {
            object : NestedScrollConnection {
                override fun onPreScroll(available: Offset, source: NestedScrollSource): Offset {
                    if (abs(available.x) > abs(available.y) && abs(available.x) > 0.5f) {
                        hostView.parent?.requestDisallowInterceptTouchEvent(true)
                    }
                    return Offset.Zero
                }
            }
        }
    var pinPendingUnpin by remember { mutableStateOf<QuickplayPinRecord?>(null) }

    if (pinPendingUnpin != null) {
        val targetPin = pinPendingUnpin!!
        AlertDialog(
            onDismissRequest = { pinPendingUnpin = null },
            title = { Text(stringResource(R.string.lng_unpinned_quickplay)) },
            text = { Text(targetPin.title) },
            confirmButton = {
                TextButton(
                    onClick = {
                        pinPendingUnpin = null
                        onUnpin(targetPin)
                    }
                ) {
                    Text(stringResource(android.R.string.ok))
                }
            },
            dismissButton = {
                TextButton(onClick = { pinPendingUnpin = null }) {
                    Text(stringResource(android.R.string.cancel))
                }
            },
        )
    }

    Surface(modifier = Modifier.fillMaxSize(), color = Color.Transparent) {
        LazyColumn(
            modifier =
                Modifier.fillMaxSize()
                    .nestedScroll(nestedScroll)
                    .navigationBarsPadding(),
            contentPadding = PaddingValues(start = 16.dp, end = 16.dp, top = 8.dp, bottom = 104.dp),
            verticalArrangement = Arrangement.spacedBy(20.dp),
        ) {
            item(key = "sec_pins") {
                Column {
                    ZuneSectionTitle(stringResource(R.string.lbl_hub_pins))
                    Spacer(modifier = Modifier.height(8.dp))
                    if (pins.isEmpty()) {
                        Box(
                            modifier =
                                Modifier.fillMaxWidth()
                                    .border(1.dp, MaterialTheme.colorScheme.outlineVariant)
                                    .padding(14.dp)
                        ) {
                            Text(
                                text = stringResource(R.string.lbl_quickplay_empty_pins),
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        }
                    } else {
                        val rows = pins.chunked(3)
                        Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
                            for (row in rows) {
                                Row(
                                    modifier = Modifier.fillMaxWidth(),
                                    horizontalArrangement = Arrangement.spacedBy(6.dp),
                                ) {
                                    for (pin in row) {
                                        val target =
                                            remember(libraryRevision, pin.kind, pin.id) {
                                                resolvePinCoverTarget(pin)
                                            }
                                        Column(
                                            modifier =
                                                Modifier.weight(1f)
                                                    .combinedClickable(
                                                        onClick = { onTapPin(pin) },
                                                        onLongClick = { pinPendingUnpin = pin },
                                                    )
                                        ) {
                                            Box(
                                                modifier =
                                                    Modifier.fillMaxWidth()
                                                        .aspectRatio(1f)
                                                        .background(
                                                            MaterialTheme.colorScheme.surfaceContainer
                                                        )
                                            ) {
                                                ZuneBoundCoverTile(
                                                    target = target,
                                                    cachedCoverId = pin.coverId,
                                                    fallbackLabel = pin.title,
                                                )
                                            }
                                            Spacer(modifier = Modifier.height(4.dp))
                                            Text(
                                                text = pin.title,
                                                style = MaterialTheme.typography.bodySmall,
                                                maxLines = 1,
                                                overflow = TextOverflow.Ellipsis,
                                            )
                                        }
                                    }
                                    repeat(3 - row.size) {
                                        Spacer(modifier = Modifier.weight(1f))
                                    }
                                }
                            }
                        }
                    }
                }
            }

            item(key = "sec_slots") {
                val accentInk = LocalTonewiseAccentInk.current
                Column {
                    ZuneSectionTitle(stringResource(R.string.lbl_hub_relay_slots))
                    Spacer(modifier = Modifier.height(8.dp))
                    LazyRow(
                        modifier = Modifier.nestedScroll(horizontalRowNestedLock),
                        horizontalArrangement = Arrangement.spacedBy(8.dp),
                    ) {
                        items(count = 5, key = { idx -> "slot_${idx + 1}" }) { idx ->
                            val slotIdx = idx + 1
                            val existing = slots.firstOrNull { it.slotIndex == slotIdx }
                            val presetScene =
                                existing?.sceneId?.let { sid ->
                                    presetScenes.firstOrNull {
                                        it.id.equals(sid, ignoreCase = true) ||
                                            it.title.startsWith(sid, ignoreCase = true)
                                    }
                                } ?: presetScenes.getOrNull(idx)
                            val isActive = activeSlotIndex == slotIdx
                            val hasTracks = existing != null && existing.queueTrackUids.isNotEmpty()
                            Column(
                                modifier =
                                    Modifier.width(124.dp)
                                        .border(
                                            width = 1.dp,
                                            color =
                                                if (isActive) accentInk
                                                else MaterialTheme.colorScheme.outlineVariant,
                                        )
                                        .background(
                                            if (isActive) accentInk.copy(alpha = 0.14f)
                                            else MaterialTheme.colorScheme.surfaceContainer
                                        )
                                        .combinedClickable(
                                            onClick = { onTapSlot(slotIdx, existing, presetScene) },
                                            onLongClick = { onLongPressSlot(slotIdx) },
                                        )
                                        .padding(horizontal = 10.dp, vertical = 10.dp),
                                verticalArrangement = Arrangement.spacedBy(2.dp),
                            ) {
                                Text(
                                    text = stringResource(R.string.fmt_queue_slot, slotIdx),
                                    style = MaterialTheme.typography.labelSmall,
                                    color = if (isActive) accentInk else MaterialTheme.colorScheme.onSurfaceVariant,
                                )
                                Text(
                                    text =
                                        if (hasTracks) existing.name
                                        else presetScene?.title ?: existing?.name ?: stringResource(R.string.lbl_slot_empty),
                                    style = MaterialTheme.typography.bodySmall,
                                    fontWeight = FontWeight.Medium,
                                    color = MaterialTheme.colorScheme.onSurface,
                                    maxLines = 1,
                                    overflow = TextOverflow.Ellipsis,
                                )
                                Text(
                                    text =
                                        if (hasTracks) "${existing.queueTrackUids.size} tracks"
                                        else if (presetScene != null) "${sceneCounts[presetScene.id] ?: 0} tracks"
                                        else stringResource(R.string.lbl_save_current_slot),
                                    style = MaterialTheme.typography.labelSmall,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                                    maxLines = 1,
                                )
                            }
                        }
                    }
                }
            }

            item(key = "sec_history") {
                Column {
                    ZuneSectionTitle(stringResource(R.string.lbl_hub_history))
                    Spacer(modifier = Modifier.height(8.dp))
                    if (sections.history.isEmpty()) {
                        Text(
                            text = stringResource(R.string.def_playback),
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    } else {
                        LazyRow(
                            modifier = Modifier.nestedScroll(horizontalRowNestedLock),
                            horizontalArrangement = Arrangement.spacedBy(8.dp),
                        ) {
                            items(sections.history, key = { "hist_${it.albumUid}" }) { item ->
                                val album =
                                    remember(libraryRevision, item.albumUid) {
                                        resolveCoverAlbum(item.albumUid)
                                    }
                                QuickplayAlbumTile(
                                    summary = item,
                                    album = album,
                                    onClick = { onTapAlbumSummary(item) },
                                    onLongClick = { onLongPressAlbumSummary(item) },
                                )
                            }
                        }
                    }
                }
            }

            item(key = "sec_new") {
                Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                    ZuneSectionTitle(stringResource(R.string.lbl_hub_new_rediscovery))
                    Box(
                        modifier =
                            Modifier.fillMaxWidth()
                                .background(MaterialTheme.colorScheme.surfaceContainer)
                                .border(1.dp, MaterialTheme.colorScheme.outlineVariant)
                                .clickable(onClick = onLaunchRediscovery)
                                .padding(14.dp)
                    ) {
                        Column {
                            Text(
                                text = stringResource(R.string.lbl_rediscovery_90d_title),
                                style = MaterialTheme.typography.titleMedium,
                                color = MaterialTheme.colorScheme.primary,
                            )
                            Text(
                                text = stringResource(R.string.lbl_rediscovery_90d_subtitle),
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        }
                    }

                    if (sections.newlyAdded.isNotEmpty()) {
                        val newColumns =
                            remember(sections.newlyAdded) { sections.newlyAdded.chunked(2) }
                        LazyRow(
                            modifier = Modifier.nestedScroll(horizontalRowNestedLock),
                            horizontalArrangement = Arrangement.spacedBy(8.dp),
                        ) {
                            items(
                                newColumns,
                                key = { col -> "newcol_${col.first().albumUid}" },
                            ) { columnItems ->
                                Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                                    for (item in columnItems) {
                                        val album =
                                            remember(libraryRevision, item.albumUid) {
                                                resolveCoverAlbum(item.albumUid)
                                            }
                                        QuickplayAlbumTile(
                                            summary = item,
                                            album = album,
                                            onClick = { onTapAlbumSummary(item) },
                                            onLongClick = { onLongPressAlbumSummary(item) },
                                        )
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

@Composable
private fun ZuneSectionTitle(title: String) {
    Text(
        text = title,
        fontSize = 14.sp,
        fontWeight = FontWeight.SemiBold,
        letterSpacing = 0.6.sp,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
    )
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun QuickplayAlbumTile(
    summary: QuickplayAlbumSummary,
    album: Album?,
    onClick: () -> Unit,
    onLongClick: () -> Unit,
) {
    Column(
        modifier =
            Modifier.width(116.dp)
                .combinedClickable(onClick = onClick, onLongClick = onLongClick)
    ) {
        Box(
            modifier =
                Modifier.size(116.dp)
                    .background(MaterialTheme.colorScheme.surfaceContainer)
        ) {
            ZuneBoundCoverTile(
                target = album,
                cachedCoverId = summary.coverId,
                fallbackLabel = summary.albumTitle,
            )
        }
        Spacer(modifier = Modifier.height(4.dp))
        Text(
            text = summary.albumTitle,
            style = MaterialTheme.typography.bodySmall,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
        Text(
            text = summary.artist,
            style = MaterialTheme.typography.labelSmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
    }
}

@Composable
private fun ZuneBoundCoverTile(
    target: Any?,
    cachedCoverId: String = "",
    fallbackLabel: String,
) {
    val context = LocalContext.current
    val cachedCover =
        remember(cachedCoverId) { QuickplayCoverResolver.fromId(context, cachedCoverId) }
    if (target == null && cachedCover == null) {
        Box(
            modifier =
                Modifier.fillMaxSize()
                    .background(MaterialTheme.colorScheme.surfaceContainerHigh),
            contentAlignment = Alignment.Center,
        ) {
            Text(
                text = fallbackLabel.take(2).uppercase(),
                style = MaterialTheme.typography.titleMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
        return
    }
    AndroidView(
        modifier = Modifier.fillMaxSize(),
        factory = {
            (LayoutInflater.from(context).inflate(R.layout.view_zune_cover_tile, null) as CoverView)
        },
        update = { coverView ->
            val bindKey =
                when (target) {
                    is Album ->
                        prominentAlbumCoverId(target)
                            .takeIf { it.isNotEmpty() }
                            ?.let { "cover:$it" } ?: "album:${target.uid}"
                    is Song ->
                        target.cover
                            ?.id
                            ?.takeIf { it.isNotEmpty() }
                            ?.let { "cover:$it" } ?: "song:${target.uid}"
                    is Artist -> "artist:${target.uid}"
                    is Playlist -> "playlist:${target.uid}"
                    else -> cachedCover?.let { "cover:${it.id}" }
                }
            if (bindKey != null && coverView.tag == bindKey) {
                return@AndroidView
            }
            coverView.tag = bindKey
            when (target) {
                is Album -> coverView.bind(target)
                is Artist -> coverView.bind(target)
                is Playlist -> coverView.bind(target)
                is Song -> coverView.bind(target)
                else -> cachedCover?.let { coverView.bind(it, fallbackLabel) }
            }
        },
    )
}
