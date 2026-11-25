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
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
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
import com.shawnshi.tonewise.music.MusicViewModel
import com.shawnshi.tonewise.music.resolve
import com.shawnshi.tonewise.music.resolveNames
import com.shawnshi.tonewise.playback.PlaySong
import com.shawnshi.tonewise.playback.PlaybackViewModel
import com.shawnshi.tonewise.playback.persist.PlayCountRepository
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
    private val musicModel: MusicViewModel by activityViewModels()
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
                val indexingState by musicModel.indexingState.collectAsStateWithLifecycle()
                val playUpdates by playCountRepository.updates.collectAsStateWithLifecycle()
                val activeAccent by dynamicAccentController.activeAccentArgb.collectAsStateWithLifecycle()
                val sections by homeModel.quickplaySections.collectAsStateWithLifecycle()

                LaunchedEffect(indexingState, playUpdates) {
                    homeModel.refreshQuickplaySections(indexingState, playUpdates)
                }

                TonewiseComposeTheme(
                    roundMode = uiSettings.roundMode,
                    dynamicAccentArgb = if (uiSettings.enableDynamicCoverAccent) activeAccent else null,
                ) {
                    QuickplayContent(
                        pins = pins,
                        sections = sections,
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
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun QuickplayContent(
    pins: List<QuickplayPinRecord>,
    sections: QuickplaySections,
    resolveCoverAlbum: (String) -> Album?,
    resolvePinCoverTarget: (QuickplayPinRecord) -> Any?,
    onTapPin: (QuickplayPinRecord) -> Unit,
    onUnpin: (QuickplayPinRecord) -> Unit,
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
                                        val target = resolvePinCoverTarget(pin)
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
