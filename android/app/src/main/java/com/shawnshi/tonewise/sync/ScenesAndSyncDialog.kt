/*
 * Copyright (c) 2026 Tonewise Project
 * ScenesAndSyncDialog.kt is part of Tonewise.
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
 
package com.shawnshi.tonewise.sync

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
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
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
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.ComposeView
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.ViewCompositionStrategy
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.core.graphics.toColorInt
import androidx.fragment.app.activityViewModels
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.google.android.material.bottomsheet.BackportBottomSheetDialogFragment
import dagger.hilt.android.AndroidEntryPoint
import javax.inject.Inject
import kotlinx.coroutines.launch
import com.shawnshi.tonewise.R
import com.shawnshi.tonewise.music.MusicRepository
import com.shawnshi.tonewise.playback.PlaybackViewModel
import com.shawnshi.tonewise.playback.ui.clock.DeskClockDialog
import com.shawnshi.tonewise.util.showToast
import org.oxycblt.musikr.tag.Name

@AndroidEntryPoint
class ScenesAndSyncDialog : BackportBottomSheetDialogFragment() {
    private val playbackModel: PlaybackViewModel by activityViewModels()
    @Inject lateinit var lanSyncRepository: LanSyncRepository
    @Inject lateinit var sessionSlotStore: SessionSlotStore
    @Inject lateinit var musicRepository: MusicRepository

    override fun onStart() {
        super.onStart()
        lanSyncRepository.startDiscovery()
    }

    override fun onStop() {
        lanSyncRepository.stopDiscovery()
        super.onStop()
    }

    override fun onCreateView(
        inflater: LayoutInflater,
        container: ViewGroup?,
        savedInstanceState: Bundle?,
    ): View {
        return ComposeView(requireContext()).apply {
            setViewCompositionStrategy(ViewCompositionStrategy.DisposeOnViewTreeLifecycleDestroyed)
            setContent {
                val scenes by lanSyncRepository.scenes.collectAsStateWithLifecycle()
                val hosts by lanSyncRepository.discoveredHosts.collectAsStateWithLifecycle()
                val syncStatus by lanSyncRepository.syncStatus.collectAsStateWithLifecycle()
                val slots by sessionSlotStore.slots.collectAsStateWithLifecycle()
                var selectedScene by remember { mutableStateOf("all") }
                var selectedVocals by remember { mutableStateOf("all") }
                var selectedEnergy by remember { mutableStateOf("all") }
                val matchedSongs =
                    remember(selectedScene, selectedVocals, selectedEnergy, musicRepository.library) {
                        lanSyncRepository.matchSongsByDjFilter(
                            selectedScene,
                            selectedVocals,
                            selectedEnergy,
                        )
                    }

                TonewiseSyncTheme {
                    ScenesAndSyncSheet(
                        scenes = scenes,
                        discoveredHosts = hosts,
                        syncStatus = syncStatus,
                        sessionSlots = slots,
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
                                dismiss()
                            } else {
                                requireContext().showToast(R.string.lbl_scene_no_matches)
                            }
                        },
                        onLaunchScene = { banner ->
                            val songs = lanSyncRepository.matchSongsForScene(banner)
                            if (songs.isNotEmpty()) {
                                sessionSlotStore.setActiveScene(banner.title)
                                lanSyncRepository.scheduleM3u8WriteBack(banner.title, songs)
                                playbackModel.play(songs)
                                dismiss()
                            } else {
                                requireContext().showToast(R.string.lbl_scene_no_matches)
                            }
                        },
                        onSaveSlot = { slotIndex ->
                            val queue = playbackModel.pagerQueue.value
                            val uids = queue.queue.map { it.uid.toString() }
                            if (uids.isEmpty()) {
                                requireContext().showToast(R.string.lbl_slot_empty_queue)
                            } else {
                                val title =
                                    (playbackModel.song.value?.name as? Name.Known)?.raw
                                        ?: "Session #$slotIndex"
                                sessionSlotStore.saveSlot(
                                    SessionSlotItem(
                                        slotIndex = slotIndex,
                                        name = title,
                                        sceneId = null,
                                        updatedAtMs = System.currentTimeMillis(),
                                        queueTrackUids = uids,
                                        currentIndex = queue.index,
                                        positionMs = playbackModel.positionDs.value * 100L,
                                    )
                                )
                                lanSyncRepository.scheduleM3u8WriteBack(title, queue.queue)
                            }
                        },
                        onRestoreSlot = { slot ->
                            val allSongs =
                                musicRepository.library?.songs?.associateBy { it.uid.toString() }
                                    ?: emptyMap()
                            val resolved = slot.queueTrackUids.mapNotNull { allSongs[it] }
                            if (resolved.isNotEmpty()) {
                                sessionSlotStore.setActiveScene(slot.name, slot.slotIndex)
                                lanSyncRepository.scheduleM3u8WriteBack(slot.name, resolved)
                                playbackModel.restoreSessionQueue(
                                    songs = resolved,
                                    index = slot.currentIndex.coerceIn(0, resolved.lastIndex),
                                    positionMs = slot.positionMs,
                                )
                                dismiss()
                            } else {
                                requireContext().showToast(R.string.lbl_scene_no_matches)
                            }
                        },
                        onSyncUrl = { url -> lanSyncRepository.syncWithHost(url) },
                        onOpenDeskClock = {
                            val fm = parentFragmentManager
                            dismiss()
                            DeskClockDialog.newInstance().show(fm, DeskClockDialog.TAG)
                        },
                    )
                }
            }
        }
    }

    companion object {
        const val TAG = "ScenesAndSyncDialog"

        fun newInstance() = ScenesAndSyncDialog()
    }
}

@Composable
private fun TonewiseSyncTheme(content: @Composable () -> Unit) {
    val context = LocalContext.current
    val dark = isSystemInDarkTheme()
    val colorScheme =
        when {
            Build.VERSION.SDK_INT >= Build.VERSION_CODES.S -> {
                if (dark) dynamicDarkColorScheme(context) else dynamicLightColorScheme(context)
            }
            dark -> darkColorScheme()
            else -> lightColorScheme()
        }
    MaterialTheme(colorScheme = colorScheme, content = content)
}

@Composable
private fun ScenesAndSyncSheet(
    scenes: List<SceneBanner>,
    discoveredHosts: List<DiscoveredSyncHost>,
    syncStatus: String,
    sessionSlots: List<SessionSlotItem>,
    selectedScene: String,
    selectedVocals: String,
    selectedEnergy: String,
    matchedCount: Int,
    onSelectScene: (String) -> Unit,
    onSelectVocals: (String) -> Unit,
    onSelectEnergy: (String) -> Unit,
    onPlayMatched: () -> Unit,
    onLaunchScene: (SceneBanner) -> Unit,
    onSaveSlot: (Int) -> Unit,
    onRestoreSlot: (SessionSlotItem) -> Unit,
    onSyncUrl: suspend (String) -> Unit,
    onOpenDeskClock: () -> Unit,
) {
    val scope = rememberCoroutineScope()
    var manualUrl by remember { mutableStateOf("http://192.168.1.100:45820") }

    Surface(modifier = Modifier.fillMaxWidth(), color = MaterialTheme.colorScheme.surface) {
        Column(
            modifier =
                Modifier.fillMaxWidth()
                    .verticalScroll(rememberScrollState())
                    .navigationBarsPadding()
                    .padding(20.dp),
            verticalArrangement = Arrangement.spacedBy(14.dp),
        ) {
            Text(
                text = stringResource(R.string.lbl_scenes_and_sync),
                style = MaterialTheme.typography.titleLarge,
                fontWeight = FontWeight.SemiBold,
            )

            // 1. 5D Scene Banner Feed
            Text(
                text = stringResource(R.string.lbl_scenes_5d_feed),
                style = MaterialTheme.typography.titleSmall,
                color = MaterialTheme.colorScheme.primary,
            )
            for (scene in scenes) {
                val accent =
                    runCatching { Color(scene.accentHex.toColorInt()) }
                        .getOrElse { MaterialTheme.colorScheme.primary }
                ElevatedCard(
                    modifier = Modifier.fillMaxWidth().clickable { onLaunchScene(scene) },
                    shape = RoundedCornerShape(14.dp),
                ) {
                    Row(
                        modifier = Modifier.fillMaxWidth().padding(14.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Box(
                            modifier =
                                Modifier.width(6.dp)
                                    .height(42.dp)
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
                            if (scene.subtitle.isNotEmpty()) {
                                Text(
                                    text = scene.subtitle,
                                    style = MaterialTheme.typography.bodySmall,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                                )
                            }
                        }
                    }
                }
            }

            HorizontalDivider()

            // 1b. Interactive DJ Tag Matrix Filter
            Text(
                text = stringResource(R.string.lbl_dj_filter),
                style = MaterialTheme.typography.titleSmall,
                color = MaterialTheme.colorScheme.primary,
            )
            DjTagFilterRow(
                label = stringResource(R.string.lbl_dj_scene),
                options = listOf("all", "focus", "coding", "relax", "energy", "pop"),
                selected = selectedScene,
                onSelect = onSelectScene,
            )
            DjTagFilterRow(
                label = stringResource(R.string.lbl_dj_vocals),
                options = listOf("all", "vocal", "instrumental", "unknown"),
                selected = selectedVocals,
                onSelect = onSelectVocals,
            )
            DjTagFilterRow(
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

            HorizontalDivider()

            // 2. 5-Slot Persistent Queues & Handoff
            Text(
                text = stringResource(R.string.lbl_handoff_slots),
                style = MaterialTheme.typography.titleSmall,
                color = MaterialTheme.colorScheme.primary,
            )
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                for (slotIdx in 1..5) {
                    val existing = sessionSlots.firstOrNull { it.slotIndex == slotIdx }
                    OutlinedButton(
                        modifier = Modifier.weight(1f),
                        onClick = {
                            if (existing != null) onRestoreSlot(existing) else onSaveSlot(slotIdx)
                        },
                    ) {
                        Text(text = if (existing != null) "▶$slotIdx" else "+$slotIdx")
                    }
                }
            }
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                OutlinedButton(
                    modifier = Modifier.weight(1f),
                    onClick = { onSaveSlot(1) },
                ) {
                    Text(text = stringResource(R.string.lbl_save_slot_1))
                }
                val slot1 = sessionSlots.firstOrNull { it.slotIndex == 1 }
                if (slot1 != null) {
                    Text(
                        modifier = Modifier.weight(1.5f).align(Alignment.CenterVertically),
                        text = "${slot1.name} (${slot1.positionMs / 1000}s)",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }

            HorizontalDivider()

            // 3. LAN SyncHost Discovery & Incremental Sync
            Text(
                text = stringResource(R.string.lbl_lan_sync),
                style = MaterialTheme.typography.titleSmall,
                color = MaterialTheme.colorScheme.primary,
            )
            for (host in discoveredHosts) {
                ElevatedCard(modifier = Modifier.fillMaxWidth()) {
                    Row(
                        modifier = Modifier.fillMaxWidth().padding(12.dp),
                        horizontalArrangement = Arrangement.SpaceBetween,
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Column(modifier = Modifier.weight(1f)) {
                            Text(host.serviceName, style = MaterialTheme.typography.bodyMedium)
                            Text(
                                host.baseUrl,
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        }
                        Button(onClick = { scope.launch { onSyncUrl(host.baseUrl) } }) {
                            Text(stringResource(R.string.lbl_sync_now))
                        }
                    }
                }
            }

            OutlinedTextField(
                value = manualUrl,
                onValueChange = { manualUrl = it },
                label = { Text(stringResource(R.string.lbl_sync_host_url)) },
                singleLine = true,
                modifier = Modifier.fillMaxWidth(),
            )
            Button(
                onClick = { scope.launch { onSyncUrl(manualUrl) } },
                modifier = Modifier.fillMaxWidth(),
            ) {
                Text(stringResource(R.string.lbl_sync_now))
            }
            Text(
                text = syncStatus,
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )

            OutlinedButton(
                onClick = onOpenDeskClock,
                modifier = Modifier.fillMaxWidth(),
            ) {
                Text(stringResource(R.string.lbl_open_desk_clock))
            }
        }
    }
}

@Composable
private fun DjTagFilterRow(
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
