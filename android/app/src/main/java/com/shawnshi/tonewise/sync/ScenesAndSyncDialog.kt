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

import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ElevatedCard
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.rotate
import androidx.compose.ui.platform.ComposeView
import androidx.compose.ui.platform.ViewCompositionStrategy
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import androidx.fragment.app.activityViewModels
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.lifecycleScope
import com.google.android.material.bottomsheet.BackportBottomSheetDialogFragment
import dagger.hilt.android.AndroidEntryPoint
import javax.inject.Inject
import kotlinx.coroutines.launch
import com.shawnshi.tonewise.R
import com.shawnshi.tonewise.music.MusicRepository
import com.shawnshi.tonewise.playback.PlaybackViewModel
import com.shawnshi.tonewise.ui.DynamicAccentController
import com.shawnshi.tonewise.ui.TonewiseComposeTheme
import com.shawnshi.tonewise.ui.UISettings
import com.shawnshi.tonewise.util.showToast

@AndroidEntryPoint
class ScenesAndSyncDialog : BackportBottomSheetDialogFragment() {
    private val playbackModel: PlaybackViewModel by activityViewModels()
    @Inject lateinit var lanSyncRepository: LanSyncRepository
    @Inject lateinit var sessionSlotStore: SessionSlotStore
    @Inject lateinit var musicRepository: MusicRepository
    @Inject lateinit var lastFmRepository: LastFmRepository
    @Inject lateinit var uiSettings: UISettings
    @Inject lateinit var dynamicAccentController: DynamicAccentController

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
                val hosts by lanSyncRepository.discoveredHosts.collectAsStateWithLifecycle()
                val pairedHost by lanSyncRepository.pairedHost.collectAsStateWithLifecycle()
                val syncStatus by lanSyncRepository.syncStatus.collectAsStateWithLifecycle()
                val syncProgress by lanSyncRepository.syncProgress.collectAsStateWithLifecycle()
                val slots by sessionSlotStore.slots.collectAsStateWithLifecycle()
                val lastFmState by lastFmRepository.state.collectAsStateWithLifecycle()
                val activeAccent by dynamicAccentController.activeAccentArgb.collectAsStateWithLifecycle()

                TonewiseComposeTheme(
                    roundMode = uiSettings.roundMode,
                    dynamicAccentArgb = if (uiSettings.enableDynamicCoverAccent) activeAccent else null,
                ) {
                    SyncHubSheet(
                        discoveredHosts = hosts,
                        pairedHost = pairedHost,
                        syncStatus = syncStatus,
                        syncProgress = syncProgress,
                        hasWritableMusicSafGrant = lanSyncRepository.hasWritableMusicSafGrant,
                        sessionSlots = slots,
                        lastFmState = lastFmState,
                        onPairHost = { url, code -> lanSyncRepository.pairWithHost(url, code) },
                        onUnpairHost = { lanSyncRepository.unpairHost() },
                        onSaveSlot = { slotIndex ->
                            val queue = playbackModel.pagerQueue.value
                            val snapshotSongs = queue.queue.toList()
                            if (snapshotSongs.isEmpty()) {
                                requireContext().showToast(R.string.lbl_slot_empty_queue)
                            } else {
                                val title =
                                    playbackModel.song.value?.name?.raw ?: "Session #$slotIndex"
                                val curIdx = queue.index
                                val posMs = playbackModel.positionDs.value * 100L
                                val nowMs = System.currentTimeMillis()
                                viewLifecycleOwner.lifecycleScope.launch {
                                    val canonicalUids =
                                        lanSyncRepository.resolveCanonicalTrackUids(snapshotSongs)
                                    sessionSlotStore.saveSlot(
                                        SessionSlotItem(
                                            slotIndex = slotIndex,
                                            name = title,
                                            sceneId = null,
                                            updatedAtMs = nowMs,
                                            queueTrackUids = canonicalUids,
                                            currentIndex = curIdx,
                                            positionMs = posMs,
                                        )
                                    )
                                    lanSyncRepository.scheduleM3u8WriteBack(title, snapshotSongs)
                                }
                            }
                        },
                        onRestoreSlot = { slot ->
                            viewLifecycleOwner.lifecycleScope.launch {
                                val resolved = lanSyncRepository.resolveSlotSongs(slot)
                                if (!isAdded) return@launch
                                if (resolved.isNotEmpty()) {
                                    sessionSlotStore.setActiveScene(slot.name, slot.slotIndex)
                                    playbackModel.restoreSessionQueue(
                                        songs = resolved,
                                        index = slot.currentIndex.coerceIn(0, resolved.lastIndex),
                                        positionMs = slot.positionMs,
                                    )
                                    dismiss()
                                } else {
                                    requireContext().showToast(R.string.lbl_scene_no_matches)
                                }
                            }
                        },
                        onSyncUrl = { url -> lanSyncRepository.startSync(url) },
                        onCancelSync = { lanSyncRepository.cancelSync() },
                        onSaveLastFm = { apiEnabled, broadcastEnabled, key, secret, user, sk ->
                            lastFmRepository.updateConfig(
                                apiEnabled = apiEnabled,
                                broadcastEnabled = broadcastEnabled,
                                apiKey = key,
                                sharedSecret = secret,
                                username = user,
                                sessionKey = sk,
                            )
                        },
                        onAuthLastFm = { key, secret, user, pass ->
                            lastFmRepository.authenticateMobileSession(key, secret, user, pass)
                        },
                        onStartLastFmBrowserAuth = { key, secret ->
                            val url = lastFmRepository.startBrowserAuth(key, secret)
                            if (url != null) {
                                runCatching {
                                    startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(url)))
                                }
                            }
                        },
                        onCompleteLastFmBrowserAuth = { key, secret ->
                            lastFmRepository.completeBrowserAuth(key, secret)
                        },
                        onSyncLastFmHistory = { lastFmRepository.syncLocalPlayHistory() },
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
private fun SyncHubSheet(
    discoveredHosts: List<DiscoveredSyncHost>,
    pairedHost: PairedHostInfo?,
    syncStatus: String,
    syncProgress: SyncProgressState,
    hasWritableMusicSafGrant: Boolean,
    sessionSlots: List<SessionSlotItem>,
    lastFmState: LastFmState,
    onPairHost: suspend (String, String) -> Boolean,
    onUnpairHost: () -> Unit,
    onSaveSlot: (Int) -> Unit,
    onRestoreSlot: (SessionSlotItem) -> Unit,
    onSyncUrl: (String) -> Unit,
    onCancelSync: () -> Unit,
    onSaveLastFm: (Boolean, Boolean, String, String, String, String) -> Unit,
    onAuthLastFm: suspend (String, String, String, String) -> Boolean,
    onStartLastFmBrowserAuth: suspend (String, String) -> Unit,
    onCompleteLastFmBrowserAuth: suspend (String, String) -> Boolean,
    onSyncLastFmHistory: suspend () -> Int,
) {
    val scope = rememberCoroutineScope()
    var manualUrl by
        remember(pairedHost?.baseUrl) {
            mutableStateOf(pairedHost?.baseUrl ?: discoveredHosts.firstOrNull()?.baseUrl ?: "")
        }
    var pairingCode by remember { mutableStateOf("") }
    var showRepairPanel by remember(pairedHost) { mutableStateOf(pairedHost == null) }
    var showUnpairConfirm by remember { mutableStateOf(false) }
    var slotPendingOverwrite by remember { mutableStateOf<Pair<Int, SessionSlotItem>?>(null) }
    var apiEnabled by remember(lastFmState.apiEnabled) { mutableStateOf(lastFmState.apiEnabled) }
    var broadcastEnabled by remember(lastFmState.broadcastEnabled) {
        mutableStateOf(lastFmState.broadcastEnabled)
    }
    var apiKey by remember(lastFmState.apiKey) { mutableStateOf(lastFmState.apiKey) }
    var sharedSecret by remember(lastFmState.sharedSecret) { mutableStateOf(lastFmState.sharedSecret) }
    var username by remember(lastFmState.username) { mutableStateOf(lastFmState.username) }
    var sessionKey by remember(lastFmState.sessionKey) { mutableStateOf(lastFmState.sessionKey) }
    var password by remember { mutableStateOf("") }
    var showLastFmConfig by
        remember(lastFmState.sessionKey) {
            mutableStateOf(!lastFmState.hasValidApiSession && lastFmState.sessionKey.isBlank())
        }

    LaunchedEffect(lastFmState.sessionKey) {
        if (lastFmState.sessionKey.isNotEmpty()) {
            password = ""
        }
    }
    LaunchedEffect(discoveredHosts) {
        if (manualUrl.isBlank() && discoveredHosts.isNotEmpty()) {
            manualUrl = discoveredHosts.first().baseUrl
        }
    }

    if (showUnpairConfirm) {
        AlertDialog(
            onDismissRequest = { showUnpairConfirm = false },
            title = { Text(stringResource(R.string.lbl_lan_sync_unpair)) },
            text = { Text(pairedHost?.hostName ?: pairedHost?.baseUrl ?: "") },
            confirmButton = {
                TextButton(
                    onClick = {
                        showUnpairConfirm = false
                        onUnpairHost()
                    }
                ) {
                    Text(stringResource(android.R.string.ok))
                }
            },
            dismissButton = {
                TextButton(onClick = { showUnpairConfirm = false }) {
                    Text(stringResource(android.R.string.cancel))
                }
            },
        )
    }

    if (slotPendingOverwrite != null) {
        val (slotIdx, existingSlot) = slotPendingOverwrite!!
        AlertDialog(
            onDismissRequest = { slotPendingOverwrite = null },
            title = { Text(stringResource(R.string.lbl_save_current_slot)) },
            text = { Text("#$slotIdx · ${existingSlot.name}") },
            confirmButton = {
                TextButton(
                    onClick = {
                        slotPendingOverwrite = null
                        onSaveSlot(slotIdx)
                    }
                ) {
                    Text(stringResource(android.R.string.ok))
                }
            },
            dismissButton = {
                TextButton(onClick = { slotPendingOverwrite = null }) {
                    Text(stringResource(android.R.string.cancel))
                }
            },
        )
    }

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

            // 1. 5-Slot Persistent Queues & Handoff
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
                    val populated = SessionSlotStore.isSlotPopulated(existing)
                    OutlinedButton(
                        modifier = Modifier.weight(1f),
                        onClick = {
                            if (populated && existing != null) {
                                onRestoreSlot(existing)
                            } else {
                                onSaveSlot(slotIdx)
                            }
                        },
                    ) {
                        Text(text = if (populated) "▶$slotIdx" else "+$slotIdx")
                    }
                }
            }
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                val slot1 = sessionSlots.firstOrNull { it.slotIndex == 1 }
                OutlinedButton(
                    modifier = Modifier.weight(1f),
                    onClick = {
                        if (slot1 != null && SessionSlotStore.isSlotPopulated(slot1)) {
                            slotPendingOverwrite = 1 to slot1
                        } else {
                            onSaveSlot(1)
                        }
                    },
                ) {
                    Text(text = stringResource(R.string.lbl_save_slot_1))
                }
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

            // 2. Last.fm Scrobble & Local Play History Sync (compact summary + collapsible config)
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(
                    text =
                        if (lastFmState.username.isNotBlank()) {
                            "${stringResource(R.string.lbl_lastfm_title)} · ${lastFmState.username}"
                        } else {
                            stringResource(R.string.lbl_lastfm_title)
                        },
                    style = MaterialTheme.typography.titleSmall,
                    color = MaterialTheme.colorScheme.primary,
                    modifier = Modifier.weight(1f),
                )
                TextButton(onClick = { showLastFmConfig = !showLastFmConfig }) {
                    Text(stringResource(R.string.lbl_lastfm_config_toggle))
                    Icon(
                        painter = painterResource(R.drawable.ic_down_24),
                        contentDescription = null,
                        modifier = Modifier.rotate(if (showLastFmConfig) 180f else 0f),
                    )
                }
            }
            if (showLastFmConfig) {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Text(
                        text = stringResource(R.string.lbl_lastfm_api_enable),
                        style = MaterialTheme.typography.bodyMedium,
                        modifier = Modifier.weight(1f),
                    )
                    Switch(
                        checked = apiEnabled,
                        onCheckedChange = { checked ->
                            apiEnabled = checked
                            onSaveLastFm(checked, broadcastEnabled, apiKey, sharedSecret, username, sessionKey)
                        },
                    )
                }
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Text(
                        text = stringResource(R.string.lbl_lastfm_broadcast_enable),
                        style = MaterialTheme.typography.bodyMedium,
                        modifier = Modifier.weight(1f),
                    )
                    Switch(
                        checked = broadcastEnabled,
                        onCheckedChange = { checked ->
                            broadcastEnabled = checked
                            onSaveLastFm(apiEnabled, checked, apiKey, sharedSecret, username, sessionKey)
                        },
                    )
                }
                OutlinedTextField(
                    value = apiKey,
                    onValueChange = { apiKey = it },
                    label = { Text(stringResource(R.string.lbl_lastfm_api_key)) },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth(),
                )
                OutlinedTextField(
                    value = sharedSecret,
                    onValueChange = { sharedSecret = it },
                    label = { Text(stringResource(R.string.lbl_lastfm_shared_secret)) },
                    singleLine = true,
                    visualTransformation = PasswordVisualTransformation(),
                    modifier = Modifier.fillMaxWidth(),
                )
                OutlinedTextField(
                    value = username,
                    onValueChange = { username = it },
                    label = { Text(stringResource(R.string.lbl_lastfm_username)) },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth(),
                )
                OutlinedTextField(
                    value = password,
                    onValueChange = { password = it },
                    label = { Text(stringResource(R.string.lbl_lastfm_password)) },
                    singleLine = true,
                    visualTransformation = PasswordVisualTransformation(),
                    modifier = Modifier.fillMaxWidth(),
                )
                OutlinedTextField(
                    value = sessionKey,
                    onValueChange = { sessionKey = it },
                    label = { Text(stringResource(R.string.lbl_lastfm_session_key)) },
                    singleLine = true,
                    visualTransformation = PasswordVisualTransformation(),
                    modifier = Modifier.fillMaxWidth(),
                )
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    OutlinedButton(
                        onClick = {
                            scope.launch {
                                onAuthLastFm(apiKey, sharedSecret, username, password)
                            }
                        },
                        modifier = Modifier.weight(1f),
                    ) {
                        Text(stringResource(R.string.lbl_lastfm_auth))
                    }
                    OutlinedButton(
                        onClick = {
                            onSaveLastFm(apiEnabled, broadcastEnabled, apiKey, sharedSecret, username, sessionKey)
                        },
                        modifier = Modifier.weight(1f),
                    ) {
                        Text(stringResource(R.string.lbl_lastfm_save))
                    }
                }
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    OutlinedButton(
                        onClick = { scope.launch { onStartLastFmBrowserAuth(apiKey, sharedSecret) } },
                        modifier = Modifier.weight(1f),
                    ) {
                        Text(stringResource(R.string.lbl_lastfm_browser_auth))
                    }
                    OutlinedButton(
                        onClick = { scope.launch { onCompleteLastFmBrowserAuth(apiKey, sharedSecret) } },
                        modifier = Modifier.weight(1f),
                    ) {
                        Text(stringResource(R.string.lbl_lastfm_complete_auth))
                    }
                }
            }
            Button(
                onClick = { scope.launch { onSyncLastFmHistory() } },
                modifier = Modifier.fillMaxWidth(),
            ) {
                Text(stringResource(R.string.lbl_lastfm_sync_now))
            }
            Text(
                text =
                    if (lastFmState.pendingLoveCount > 0) {
                        "${stringResource(R.string.fmt_lastfm_stats_with_loved, lastFmState.pendingCount, lastFmState.pendingLoveCount, lastFmState.syncedPlayCountTotal)} · ${lastFmState.statusMessage}"
                    } else {
                        "${stringResource(R.string.fmt_lastfm_stats, lastFmState.pendingCount, lastFmState.syncedPlayCountTotal)} · ${lastFmState.statusMessage}"
                    },
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )

            HorizontalDivider()

            // 3. LAN SyncHost Discovery, Pairing & Content Sync
            Text(
                text = stringResource(R.string.lbl_lan_sync),
                style = MaterialTheme.typography.titleSmall,
                color = MaterialTheme.colorScheme.primary,
            )

            if (pairedHost != null) {
                ElevatedCard(modifier = Modifier.fillMaxWidth()) {
                    Column(
                        modifier = Modifier.fillMaxWidth().padding(12.dp),
                        verticalArrangement = Arrangement.spacedBy(8.dp),
                    ) {
                        Row(
                            modifier = Modifier.fillMaxWidth(),
                            horizontalArrangement = Arrangement.SpaceBetween,
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            Column(modifier = Modifier.weight(1f)) {
                                Text(
                                    text = "${stringResource(R.string.lbl_lan_sync_paired_host)}：${pairedHost.hostName}",
                                    style = MaterialTheme.typography.bodyMedium,
                                    fontWeight = FontWeight.SemiBold,
                                )
                                Text(
                                    text = pairedHost.baseUrl,
                                    style = MaterialTheme.typography.bodySmall,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                                )
                            }
                            Button(
                                enabled = !syncProgress.isRunning,
                                onClick = { onSyncUrl(pairedHost.baseUrl) },
                            ) {
                                Text(stringResource(R.string.lbl_sync_now))
                            }
                        }
                        Row(
                            modifier = Modifier.fillMaxWidth(),
                            horizontalArrangement = Arrangement.spacedBy(8.dp),
                        ) {
                            TextButton(onClick = { showRepairPanel = !showRepairPanel }) {
                                Text(stringResource(R.string.lbl_lan_sync_re_pair))
                            }
                            TextButton(enabled = !syncProgress.isRunning, onClick = { showUnpairConfirm = true }) {
                                Text(stringResource(R.string.lbl_lan_sync_unpair))
                            }
                        }
                    }
                }
            }

            for (host in discoveredHosts) {
                val isPairedToThis =
                    pairedHost?.baseUrl?.equals(host.baseUrl, ignoreCase = true) == true
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
                        Button(
                            enabled = !syncProgress.isRunning,
                            onClick = {
                                if (isPairedToThis) {
                                    onSyncUrl(host.baseUrl)
                                } else {
                                    manualUrl = host.baseUrl
                                    showRepairPanel = true
                                }
                            },
                        ) {
                            Text(
                                if (isPairedToThis) {
                                    stringResource(R.string.lbl_sync_now)
                                } else {
                                    stringResource(R.string.lbl_lan_sync_pair)
                                }
                            )
                        }
                    }
                }
            }

            if (pairedHost == null || showRepairPanel) {
                OutlinedTextField(
                    value = manualUrl,
                    onValueChange = { manualUrl = it },
                    label = { Text(stringResource(R.string.lbl_sync_host_url)) },
                    placeholder = { Text(stringResource(R.string.hint_lan_sync_url)) },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth(),
                )
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    OutlinedTextField(
                        value = pairingCode,
                        onValueChange = { input ->
                            pairingCode = input.filter { it.isDigit() }.take(6)
                        },
                        label = { Text(stringResource(R.string.lbl_lan_sync_pairing_code)) },
                        singleLine = true,
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                        modifier = Modifier.weight(1f),
                    )
                    Button(
                        enabled =
                            !syncProgress.isRunning &&
                                manualUrl.isNotBlank() &&
                                pairingCode.length == 6,
                        onClick = {
                            scope.launch {
                                val ok = onPairHost(manualUrl, pairingCode)
                                if (ok) {
                                    pairingCode = ""
                                    showRepairPanel = false
                                }
                            }
                        },
                    ) {
                        Text(stringResource(R.string.lbl_lan_sync_pair))
                    }
                }
            }

            if (syncProgress.isRunning) {
                if (syncProgress.totalBytes > 0L) {
                    val fraction =
                        (syncProgress.downloadedBytes.toFloat() / syncProgress.totalBytes.toFloat())
                            .coerceIn(0f, 1f)
                    LinearProgressIndicator(
                        progress = { fraction },
                        modifier = Modifier.fillMaxWidth(),
                    )
                } else {
                    LinearProgressIndicator(modifier = Modifier.fillMaxWidth())
                }
                val progressDetail =
                    buildString {
                        append(syncProgress.phase)
                        if (syncProgress.currentTrackTitle.isNotBlank()) {
                            append(" · ").append(syncProgress.currentTrackTitle)
                        }
                        if (syncProgress.totalBytes > 0L) {
                            val downMb = syncProgress.downloadedBytes / (1024.0 * 1024.0)
                            val totalMb = syncProgress.totalBytes / (1024.0 * 1024.0)
                            append(String.format(" (%.1f / %.1f MB)", downMb, totalMb))
                        }
                    }
                Text(
                    text = progressDetail,
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.primary,
                )
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.End,
                ) {
                    TextButton(onClick = onCancelSync) {
                        Text("取消同步")
                    }
                }
            }

            Text(
                text = syncStatus,
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )

            if (!hasWritableMusicSafGrant) {
                Text(
                    text = stringResource(R.string.lbl_usb_saf_grant_hint),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.secondary,
                )
            }
        }
    }
}
