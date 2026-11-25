/*
 * Copyright (c) 2026 Tonewise Project
 * QuickplayFragment.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.home.quickplay

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
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.produceState
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
import androidx.compose.ui.viewinterop.AndroidView
import androidx.fragment.app.Fragment
import androidx.fragment.app.activityViewModels
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.lifecycleScope
import com.shawnshi.tonewise.R
import com.shawnshi.tonewise.detail.DetailViewModel
import com.shawnshi.tonewise.image.CoverView
import com.shawnshi.tonewise.music.ClassicalWork
import com.shawnshi.tonewise.music.MusicRepository
import com.shawnshi.tonewise.music.MusicViewModel
import com.shawnshi.tonewise.music.resolve
import com.shawnshi.tonewise.music.resolveNames
import com.shawnshi.tonewise.playback.PlaySong
import com.shawnshi.tonewise.playback.PlaybackViewModel
import com.shawnshi.tonewise.playback.persist.PlayCountRepository
import com.shawnshi.tonewise.sync.AutoDjEngine
import com.shawnshi.tonewise.sync.LanSyncRepository
import com.shawnshi.tonewise.sync.SceneBanner
import com.shawnshi.tonewise.sync.ScenesAndSyncDialog
import com.shawnshi.tonewise.sync.SessionSlotItem
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
    private val musicModel: MusicViewModel by activityViewModels()

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
                val activeSlotIdx by sessionSlotStore.activeSlotIndex.collectAsStateWithLifecycle()
                val indexingState by musicModel.indexingState.collectAsStateWithLifecycle()
                val playUpdates by playCountRepository.updates.collectAsStateWithLifecycle()
                val activeAccent by dynamicAccentController.activeAccentArgb.collectAsStateWithLifecycle()

                val sections by
                    produceState(
                        initialValue = QuickplaySections(emptyList(), emptyList(), emptyList()),
                        indexingState,
                        playUpdates,
                    ) {
                        value =
                            withContext(Dispatchers.Default) {
                                val songs = musicRepository.library?.songs.orEmpty()
                                val ctx = requireContext()
                                val candidates =
                                    songs.map { song ->
                                        val snap = playCountRepository.getSnapshot(song)
                                        QuickplayTrackCandidate(
                                            songUid = song.uid.toString(),
                                            title = song.name.resolve(ctx),
                                            artist = song.artists.resolveNames(ctx),
                                            albumUid = song.album.uid.toString(),
                                            albumTitle = song.album.name.resolve(ctx),
                                            addedMs = song.addedMs,
                                            playCount = snap.playCount,
                                            lastPlayedMs = snap.lastPlayedMs,
                                        )
                                    }
                                QuickplayAggregator.computeSections(
                                    candidates = candidates,
                                    nowMs = System.currentTimeMillis(),
                                    limit = 12,
                                )
                            }
                    }

                TonewiseComposeTheme(
                    roundMode = uiSettings.roundMode,
                    dynamicAccentArgb = if (uiSettings.enableDynamicCoverAccent) activeAccent else null,
                ) {
                    QuickplayContent(
                        pins = pins,
                        sections = sections,
                        slots = slots,
                        activeSlotIdx = activeSlotIdx,
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
                        onTapAlbumSummary = { summary ->
                            val uid = Music.UID.fromString(summary.albumUid)
                            val album = uid?.let { musicRepository.library?.findAlbum(it) }
                            if (album != null) {
                                playbackModel.play(album)
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
                        onTapSlot = { slotIndex, existing -> handleSlotRestoreOrSave(slotIndex, existing) },
                        onLongPressSlot = { slotIndex -> handleSlotSave(slotIndex) },
                        onOpenSync = {
                            if (parentFragmentManager.findFragmentByTag(ScenesAndSyncDialog.TAG) == null) {
                                ScenesAndSyncDialog.newInstance()
                                    .show(parentFragmentManager, ScenesAndSyncDialog.TAG)
                            }
                        },
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
        val lib = musicRepository.library ?: return
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
                    playbackModel.play(rec.movements)
                }
            }
        }
    }

    private fun launch90dRediscovery() {
        viewLifecycleOwner.lifecycleScope.launch {
            val banner =
                SceneBanner(
                    id = "rediscovery_90d",
                    title = getString(R.string.lbl_rediscovery_90d_title),
                    subtitle = getString(R.string.lbl_rediscovery_90d_subtitle),
                    accentHex = "#FF1E7A",
                    rawSceneJson =
                        """{"id":"rediscovery_90d","sort_arc":"rediscovery_90d","rule":{"all":[{"predicate":{"field":"unplayed_days_gte","op":"gte","value":90}}]}}""",
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

    private fun handleSlotRestoreOrSave(slotIndex: Int, existing: SessionSlotItem?) {
        if (existing != null && existing.queueTrackUids.isNotEmpty()) {
            viewLifecycleOwner.lifecycleScope.launch {
                val resolved = lanSyncRepository.resolveSlotSongs(existing)
                if (!isAdded) return@launch
                if (resolved.isNotEmpty()) {
                    sessionSlotStore.setActiveScene(existing.name, slotIndex)
                    playbackModel.restoreSessionQueue(
                        songs = resolved,
                        index = existing.currentIndex.coerceIn(0, resolved.lastIndex),
                        positionMs = existing.positionMs,
                    )
                } else {
                    requireContext().showToast(R.string.lbl_scene_no_matches)
                }
            }
            return
        }
        handleSlotSave(slotIndex)
    }

    private fun handleSlotSave(slotIndex: Int) {
        val queue = playbackModel.pagerQueue.value
        val snapshotSongs = queue.queue.toList()
        if (snapshotSongs.isEmpty()) {
            requireContext().showToast(R.string.lbl_slot_empty_queue)
            return
        }
        val title = playbackModel.song.value?.name?.raw ?: "Session #$slotIndex"
        val posMs = playbackModel.positionDs.value * 100L
        val nowMs = System.currentTimeMillis()
        viewLifecycleOwner.lifecycleScope.launch {
            val canonicalUids = lanSyncRepository.resolveCanonicalTrackUids(snapshotSongs)
            sessionSlotStore.saveSlot(
                SessionSlotItem(
                    slotIndex = slotIndex,
                    name = title,
                    sceneId = null,
                    updatedAtMs = nowMs,
                    queueTrackUids = canonicalUids,
                    currentIndex = queue.index,
                    positionMs = posMs,
                )
            )
            lanSyncRepository.scheduleM3u8WriteBack(title, snapshotSongs)
            if (isAdded) {
                requireContext().showToast(R.string.lbl_save_current_slot)
            }
        }
    }
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun QuickplayContent(
    pins: List<QuickplayPinRecord>,
    sections: QuickplaySections,
    slots: List<SessionSlotItem>,
    activeSlotIdx: Int,
    resolveCoverAlbum: (String) -> Album?,
    resolvePinCoverTarget: (QuickplayPinRecord) -> Any?,
    onTapPin: (QuickplayPinRecord) -> Unit,
    onUnpin: (QuickplayPinRecord) -> Unit,
    onTapAlbumSummary: (QuickplayAlbumSummary) -> Unit,
    onLongPressAlbumSummary: (QuickplayAlbumSummary) -> Unit,
    onLaunchRediscovery: () -> Unit,
    onTapSlot: (Int, SessionSlotItem?) -> Unit,
    onLongPressSlot: (Int) -> Unit,
    onOpenSync: () -> Unit,
) {
    val nestedScroll = rememberNestedScrollInteropConnection()

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
                    ZuneSectionTitle("pins")
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
                                        val target = resolvePinCoverTarget(pin)
                                        Column(
                                            modifier =
                                                Modifier.weight(1f)
                                                    .combinedClickable(
                                                        onClick = { onTapPin(pin) },
                                                        onLongClick = { onUnpin(pin) },
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

            item(key = "sec_history") {
                Column {
                    ZuneSectionTitle("history")
                    Spacer(modifier = Modifier.height(8.dp))
                    if (sections.history.isEmpty()) {
                        Text(
                            text = stringResource(R.string.def_playback),
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    } else {
                        LazyRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                            items(sections.history, key = { "hist_${it.albumUid}" }) { item ->
                                QuickplayAlbumTile(
                                    summary = item,
                                    album = resolveCoverAlbum(item.albumUid),
                                    onClick = { onTapAlbumSummary(item) },
                                    onLongClick = { onLongPressAlbumSummary(item) },
                                )
                            }
                        }
                    }
                }
            }

            item(key = "sec_slots") {
                val slotMap = slots.associateBy { it.slotIndex }
                Column {
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.SpaceBetween,
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        ZuneSectionTitle("relay slots")
                        TextButton(onClick = onOpenSync) {
                            Text(
                                text = "sync & last.fm",
                                style = MaterialTheme.typography.labelMedium,
                                color = MaterialTheme.colorScheme.primary,
                            )
                        }
                    }
                    Spacer(modifier = Modifier.height(6.dp))
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.spacedBy(6.dp),
                    ) {
                        for (idx in 1..5) {
                            val existing = slotMap[idx]
                            val isActive = idx == activeSlotIdx && existing != null
                            Box(
                                modifier =
                                    Modifier.weight(1f)
                                        .height(64.dp)
                                        .background(
                                            if (isActive) MaterialTheme.colorScheme.primary
                                            else MaterialTheme.colorScheme.surfaceContainerLow
                                        )
                                        .border(
                                            1.dp,
                                            if (isActive) MaterialTheme.colorScheme.primary
                                            else MaterialTheme.colorScheme.outlineVariant,
                                        )
                                        .combinedClickable(
                                            onClick = { onTapSlot(idx, existing) },
                                            onLongClick = { onLongPressSlot(idx) },
                                        )
                                        .padding(6.dp),
                                contentAlignment = Alignment.BottomStart,
                            ) {
                                Text(
                                    text = "#$idx",
                                    fontSize = 11.sp,
                                    fontWeight = FontWeight.Bold,
                                    color =
                                        if (isActive) MaterialTheme.colorScheme.onPrimary
                                        else MaterialTheme.colorScheme.onSurfaceVariant,
                                    modifier = Modifier.align(Alignment.TopStart),
                                )
                                Text(
                                    text = existing?.name ?: "empty",
                                    style = MaterialTheme.typography.labelSmall,
                                    color =
                                        if (isActive) MaterialTheme.colorScheme.onPrimary
                                        else MaterialTheme.colorScheme.onSurface,
                                    maxLines = 1,
                                    overflow = TextOverflow.Ellipsis,
                                )
                            }
                        }
                    }
                }
            }

            item(key = "sec_new") {
                Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                    ZuneSectionTitle("new & rediscovery")
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
                        LazyRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                            items(sections.newlyAdded, key = { "new_${it.albumUid}" }) { item ->
                                QuickplayAlbumTile(
                                    summary = item,
                                    album = resolveCoverAlbum(item.albumUid),
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
            ZuneBoundCoverTile(target = album, fallbackLabel = summary.albumTitle)
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
private fun ZuneBoundCoverTile(target: Any?, fallbackLabel: String) {
    val context = LocalContext.current
    if (target == null) {
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
            when (target) {
                is Album -> coverView.bind(target)
                is Artist -> coverView.bind(target)
                is Playlist -> coverView.bind(target)
                is Song -> coverView.bind(target)
            }
        },
    )
}
