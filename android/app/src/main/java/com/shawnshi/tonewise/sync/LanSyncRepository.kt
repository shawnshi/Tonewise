/*
 * Copyright (c) 2026 Tonewise Project
 * LanSyncRepository.kt is part of Tonewise.
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

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.os.Build
import android.os.Environment
import android.util.Base64
import dagger.hilt.android.qualifiers.ApplicationContext
import java.io.File
import java.net.HttpURLConnection
import java.net.URL
import java.net.URLEncoder
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicBoolean
import javax.inject.Inject
import javax.inject.Singleton
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import com.shawnshi.tonewise.detail.MusicBioRepository
import com.shawnshi.tonewise.music.MusicRepository
import com.shawnshi.tonewise.playback.persist.PlayCountRepository
import com.shawnshi.tonewise.playback.ui.WaveformRepository
import org.json.JSONArray
import org.json.JSONObject
import org.oxycblt.musikr.Song
import org.oxycblt.musikr.core.TonewiseCore
import org.oxycblt.musikr.fs.Components
import org.oxycblt.musikr.tag.Name
import timber.log.Timber as L

data class DiscoveredSyncHost(val serviceName: String, val host: String, val port: Int) {
    val baseUrl: String
        get() = "http://$host:$port"
}

data class PairedHostInfo(
    val baseUrl: String,
    val hostName: String,
    val deviceId: String,
    val token: String,
)

data class SyncProgressState(
    val isRunning: Boolean = false,
    val phase: String = "",
    val completedTracks: Int = 0,
    val totalTracks: Int = 0,
    val downloadedBytes: Long = 0L,
    val totalBytes: Long = 0L,
    val currentTrackTitle: String = "",
)

data class SceneBanner(
    val id: String,
    val title: String,
    val subtitle: String,
    val accentHex: String,
    val rawSceneJson: String,
)

data class TagMutationRecord(
    val trackUid: String,
    val field: String,
    val value: String,
    val updatedAtMs: Long,
    val deviceId: String,
)

enum class FocusPillState {
    NONE,
    INCLUDE,
    EXCLUDE;

    fun next(): FocusPillState =
        when (this) {
            NONE -> INCLUDE
            INCLUDE -> EXCLUDE
            EXCLUDE -> NONE
        }
}

data class FocusFilterSpec(
    val scenes: Map<String, FocusPillState> = emptyMap(),
    val vocals: Map<String, FocusPillState> = emptyMap(),
    val energies: Map<String, FocusPillState> = emptyMap(),
    val tempos: Map<String, FocusPillState> = emptyMap(),
    val moods: Map<String, FocusPillState> = emptyMap(),
    val sortArc: String = "flat",
    val unplayedDaysGte: Int? = null,
) {
    val isEmpty: Boolean
        get() =
            scenes.values.all { it == FocusPillState.NONE } &&
                vocals.values.all { it == FocusPillState.NONE } &&
                energies.values.all { it == FocusPillState.NONE } &&
                tempos.values.all { it == FocusPillState.NONE } &&
                moods.values.all { it == FocusPillState.NONE } &&
                unplayedDaysGte == null
}

@Singleton
class LanSyncRepository
@Inject
constructor(
    @ApplicationContext private val context: Context,
    private val musicRepository: MusicRepository,
    private val playCountRepository: PlayCountRepository,
    private val sessionSlotStore: SessionSlotStore,
    private val waveformRepository: WaveformRepository,
    private val musicBioRepository: MusicBioRepository,
    private val managedSyncStorage: ManagedSyncStorage,
    private val lastFmRepository: LastFmRepository,
) {
    private val nsdManager = context.getSystemService(Context.NSD_SERVICE) as? NsdManager
    private val syncDb: TonewiseSyncDatabase by lazy { TonewiseSyncDatabase.getInstance(context) }
    private var activeDiscoveryListener: NsdManager.DiscoveryListener? = null
    private val syncInFlight = AtomicBoolean(false)

    private val _discoveredHosts = MutableStateFlow<List<DiscoveredSyncHost>>(emptyList())
    val discoveredHosts: StateFlow<List<DiscoveredSyncHost>> = _discoveredHosts

    private val _pairedHost = MutableStateFlow(loadPairedHost())
    val pairedHost: StateFlow<PairedHostInfo?> = _pairedHost

    private val _scenes = MutableStateFlow(loadCachedOrDefaultScenes())
    val scenes: StateFlow<List<SceneBanner>> = _scenes

    private val _syncStatus = MutableStateFlow("局域网同步就绪（正在发现同一 Wi-Fi 下的电脑…）")
    val syncStatus: StateFlow<String> = _syncStatus

    private val _syncProgress = MutableStateFlow(SyncProgressState())
    val syncProgress: StateFlow<SyncProgressState> = _syncProgress

    private val pendingMutationsList = loadPendingMutations().toMutableList()
    private val lwwOverrides = ConcurrentHashMap<String, String>(loadLwwOverrides())
    @Volatile private var lwwVersion: Long = 0L
    private val tracksCacheLock = Any()
    @Volatile private var cachedTracksKey: TracksCacheKey? = null
    @Volatile private var cachedTracksJson: String = "[]"

    private val _pendingMutationCount = MutableStateFlow(pendingMutationsList.size)
    val pendingMutationCount: StateFlow<Int> = _pendingMutationCount

    private data class TracksCacheKey(
        val songsIdentityHash: Int,
        val songCount: Int,
        val lwwVersion: Long,
        val playCountVersion: Long,
    )

    private val writeBackScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    @Volatile private var pendingWriteBackJob: Job? = null

    fun scheduleM3u8WriteBack(sceneName: String, songs: List<Song>) {
        pendingWriteBackJob?.cancel()
        val snapshot = songs.toList()
        val safeName = sanitizePlaylistFilename(sceneName)
        pendingWriteBackJob =
            writeBackScope.launch {
                delay(2_000L)
                val payload = formatM3u8Content(snapshot).toByteArray(Charsets.UTF_8)
                runCatching {
                    val internalDir = File(context.filesDir, ".tonewise/playlists")
                    internalDir.mkdirs()
                    File(internalDir, "$safeName.m3u8").writeBytes(payload)
                }
                runCatching {
                    @Suppress("DEPRECATION")
                    val musicDir =
                        Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_MUSIC)
                    val extDir = File(musicDir, ".tonewise/playlists")
                    if (extDir.exists() || extDir.mkdirs()) {
                        File(extDir, "$safeName.m3u8").writeBytes(payload)
                    }
                }
                L.d("Debounced .m3u8 write-back flushed for $safeName (${snapshot.size} tracks)")
            }
    }

    fun startDiscovery() {
        val manager = nsdManager ?: return
        if (activeDiscoveryListener != null) return

        val listener =
            object : NsdManager.DiscoveryListener {
                override fun onDiscoveryStarted(regType: String) {
                    L.d("NSD discovery started for $regType")
                }

                override fun onServiceFound(serviceInfo: NsdServiceInfo) {
                    if (serviceInfo.serviceType.contains("_tonewise._tcp")) {
                        @Suppress("DEPRECATION")
                        manager.resolveService(
                            serviceInfo,
                            object : NsdManager.ResolveListener {
                                override fun onResolveFailed(info: NsdServiceInfo, errorCode: Int) {
                                    L.w("NSD resolve failed: $errorCode")
                                }

                                override fun onServiceResolved(resolved: NsdServiceInfo) {
                                    @Suppress("DEPRECATION")
                                    val host = resolved.host?.hostAddress ?: return
                                    val entry =
                                        DiscoveredSyncHost(
                                            resolved.serviceName,
                                            host,
                                            resolved.port,
                                        )
                                    val updated =
                                        (_discoveredHosts.value.filterNot {
                                            it.host == entry.host && it.port == entry.port
                                        } + entry)
                                    _discoveredHosts.value = updated
                                }
                            },
                        )
                    }
                }

                override fun onServiceLost(serviceInfo: NsdServiceInfo) {
                    _discoveredHosts.value =
                        _discoveredHosts.value.filterNot { it.serviceName == serviceInfo.serviceName }
                }

                override fun onDiscoveryStopped(serviceType: String) {}

                override fun onStartDiscoveryFailed(serviceType: String, errorCode: Int) {
                    activeDiscoveryListener = null
                }

                override fun onStopDiscoveryFailed(serviceType: String, errorCode: Int) {}
            }

        activeDiscoveryListener = listener
        runCatching {
            manager.discoverServices("_tonewise._tcp.", NsdManager.PROTOCOL_DNS_SD, listener)
        }.onFailure { activeDiscoveryListener = null }
    }

    fun stopDiscovery() {
        val listener = activeDiscoveryListener ?: return
        activeDiscoveryListener = null
        runCatching { nsdManager?.stopServiceDiscovery(listener) }
    }

    suspend fun pairWithHost(baseUrl: String, pairingCode: String): Boolean =
        withContext(Dispatchers.IO) {
            val cleanBase = normalizeBaseUrl(baseUrl)
            if (cleanBase.isBlank() || pairingCode.trim().length != 6) {
                _syncStatus.value = "请输入正确的电脑地址和 6 位配对码"
                return@withContext false
            }
            _syncStatus.value = "正在与 $cleanBase 配对…"
            try {
                val deviceId = getOrCreateStableDeviceId()
                val deviceName = Build.MODEL?.takeIf { it.isNotBlank() } ?: "Android Phone"
                val pairResp = LanSyncV2Client.pair(cleanBase, pairingCode.trim(), deviceId, deviceName)
                val info =
                    PairedHostInfo(
                        baseUrl = cleanBase,
                        hostName = pairResp.hostName,
                        deviceId = pairResp.deviceId,
                        token = pairResp.token,
                    )
                savePairedHost(info)
                _pairedHost.value = info
                _syncStatus.value = "配对成功：已绑定「${pairResp.hostName}」，可以开始同步"
                true
            } catch (e: LanSyncAuthException) {
                _syncStatus.value = "配对失败：6 位配对码不正确"
                false
            } catch (e: Exception) {
                L.w(e, "Pairing failed")
                _syncStatus.value = "连接失败：${e.message ?: "请检查网络与 Windows 防火墙端口 45820"}"
                false
            }
        }

    fun unpairHost() {
        clearPairedHost()
        _pairedHost.value = null
        _syncStatus.value = "已解除电脑配对"
    }

    suspend fun syncWithHost(baseUrl: String) {
        withContext(Dispatchers.IO) {
            if (!syncInFlight.compareAndSet(false, true)) {
                return@withContext
            }
            val cleanBase = normalizeBaseUrl(baseUrl)
            if (cleanBase.isBlank()) {
                _syncStatus.value = "请先选择或填写电脑同步地址"
                syncInFlight.set(false)
                return@withContext
            }

            val paired = _pairedHost.value
            val token =
                paired
                    ?.takeIf { it.baseUrl.equals(cleanBase, ignoreCase = true) }
                    ?.token
                    ?.takeIf { it.isNotBlank() }
            if (token == null) {
                _syncStatus.value = "该电脑尚未配对：请先输入电脑端显示的 6 位配对码"
                syncInFlight.set(false)
                return@withContext
            }

            _syncStatus.value = "正在连接 $cleanBase 获取同步清单…"
            _syncProgress.value =
                SyncProgressState(isRunning = true, phase = "正在连接电脑并检查发送队列…")
            try {
                val outbox = LanSyncV2Client.fetchOutbox(cleanBase, token)

                val existingSynced = syncDb.syncedTrackDao().getAll().associateBy { it.trackUid }
                val songsSnapshot = musicRepository.library?.songs.orEmpty()
                val songsByRelSuffix = buildSongsByRelSuffixMap(songsSnapshot)

                val pendingDownloads = ArrayList<OutboxTrackDto>()
                var skippedExisting = 0

                for (track in outbox.tracks) {
                    val outboxJson = JSONArray(track.sourceOutboxIds).toString()
                    val recorded = existingSynced[track.trackUid]
                    if (recorded != null && managedSyncStorage.existsInManagedStorage(recorded)) {
                        if (recorded.sourceOutboxIdsJson != outboxJson) {
                            syncDb.syncedTrackDao().upsert(recorded.copy(sourceOutboxIdsJson = outboxJson))
                        }
                        skippedExisting++
                        continue
                    }

                    val existingLocalSong = findMatchingSongByRelPath(track.targetRelPath, songsByRelSuffix)
                    if (existingLocalSong != null &&
                        (track.fileSize <= 0L || existingLocalSong.size == track.fileSize)
                    ) {
                        syncDb.syncedTrackDao().upsert(
                            SyncedTrackEntity(
                                trackUid = track.trackUid,
                                relativePath = track.targetRelPath,
                                documentUri = existingLocalSong.uri.toString(),
                                fileSize = existingLocalSong.size,
                                mtimeMs = track.mtimeMs,
                                sourceOutboxIdsJson = outboxJson,
                                syncedAtMs = System.currentTimeMillis(),
                            )
                        )
                        cacheFingerprintAlias(existingLocalSong, track.trackUid)
                        skippedExisting++
                        continue
                    }

                    pendingDownloads.add(track)
                }

                val partsDir = File(context.cacheDir, "lan_sync_parts")
                val totalDownloadBytes = pendingDownloads.sumOf { it.fileSize.coerceAtLeast(0L) }
                var completedBytesBeforeCurrent = 0L
                var downloadedCount = 0
                val failedTrackUids = HashSet<String>()
                var firstDownloadError: String? = null

                for ((index, track) in pendingDownloads.withIndex()) {
                    _syncProgress.value =
                        SyncProgressState(
                            isRunning = true,
                            phase = "正在下载曲目 (${index + 1}/${pendingDownloads.size})",
                            completedTracks = index,
                            totalTracks = pendingDownloads.size,
                            downloadedBytes = completedBytesBeforeCurrent,
                            totalBytes = totalDownloadBytes,
                            currentTrackTitle = track.title,
                        )
                    val partFile = File(partsDir, "${sanitizeKey(track.trackUid)}.part")
                    try {
                        val fileUrl = LanSyncV2Client.buildTrackFileUrl(cleanBase, track.trackUid)
                        LanSyncV2Client.downloadFileResumable(
                            url = fileUrl,
                            token = token,
                            partFile = partFile,
                            expectedSize = track.fileSize,
                        ) { trackDownloaded, _ ->
                            _syncProgress.value =
                                SyncProgressState(
                                    isRunning = true,
                                    phase = "正在下载曲目 (${index + 1}/${pendingDownloads.size})",
                                    completedTracks = index,
                                    totalTracks = pendingDownloads.size,
                                    downloadedBytes = completedBytesBeforeCurrent + trackDownloaded,
                                    totalBytes = totalDownloadBytes,
                                    currentTrackTitle = track.title,
                                )
                        }

                        val committed = managedSyncStorage.commitAudioFile(track, partFile)
                        syncDb.syncedTrackDao().upsert(
                            SyncedTrackEntity(
                                trackUid = track.trackUid,
                                relativePath = committed.relativePath,
                                documentUri = committed.documentUri,
                                fileSize = committed.fileSize,
                                mtimeMs = track.mtimeMs,
                                sourceOutboxIdsJson = JSONArray(track.sourceOutboxIds).toString(),
                                syncedAtMs = System.currentTimeMillis(),
                            )
                        )
                        downloadedCount++
                        completedBytesBeforeCurrent += track.fileSize.coerceAtLeast(0L)
                    } catch (authErr: LanSyncAuthException) {
                        throw authErr
                    } catch (dlErr: Exception) {
                        L.w(dlErr, "Track download paused/failed for ${track.title} (${track.trackUid})")
                        failedTrackUids.add(track.trackUid)
                        if (firstDownloadError == null) {
                            firstDownloadError = dlErr.message
                        }
                    }
                }

                // Download missing album covers once per album directory
                val attemptedCoverDirs = HashSet<String>()
                for (track in outbox.tracks) {
                    if (!track.hasCover || track.trackUid in failedTrackUids) continue
                    val dirKey = track.targetRelPath.substringBeforeLast('/', "")
                    if (!attemptedCoverDirs.add(dirKey)) continue
                    runCatching {
                        val coverUrl = LanSyncV2Client.buildTrackCoverUrl(cleanBase, track.trackUid)
                        val bytes = LanSyncV2Client.downloadCoverIfPresent(coverUrl, token)
                        if (bytes != null && bytes.isNotEmpty()) {
                            managedSyncStorage.commitAlbumCoverIfMissing(track.targetRelPath, bytes)
                        }
                    }.onFailure { L.w(it, "Album cover fetch skipped for $dirKey") }
                }

                // Commit playlists received from PC outbox
                for (pl in outbox.playlists) {
                    managedSyncStorage.commitPlaylistM3u8(pl.name, pl.relPaths)
                    savePlaylistMergeBase(pl.name, pl.relPaths)
                }

                _syncProgress.value =
                    _syncProgress.value.copy(phase = "正在双向同步播放记录、评分与歌单…")

                // Best-effort v1 metadata assets (scenes / waveforms / encyclopedia)
                runCatching {
                    val statusObj = JSONObject(httpGetString("$cleanBase/api/v1/status"))
                    val scenesRaw = httpGetString("$cleanBase/api/v1/scenes")
                    cacheFile("scenes_5d.json", scenesRaw.toByteArray(Charsets.UTF_8))
                    _scenes.value = applySceneOrder(parseScenesJson(scenesRaw))

                    if (statusObj.optBoolean("has_waveforms", false)) {
                        val remoteBytes = statusObj.optLong("waveforms_bytes", 0L)
                        val localWf = File(context.filesDir, ".tonewise/waveforms.bin")
                        if (!localWf.isFile || (remoteBytes > 0L && localWf.length() != remoteBytes)) {
                            val wfBytes = httpGetBytes("$cleanBase/api/v1/waveforms")
                            cacheFile("waveforms.bin", wfBytes)
                            waveformRepository.invalidateArchiveCache()
                        }
                    }
                    if (statusObj.optBoolean("has_encyclopedia", false)) {
                        val encJson = httpGetString("$cleanBase/api/v1/encyclopedia")
                        musicBioRepository.importSyncedEncyclopediaJson(encJson)
                    }
                }.onFailure { L.w(it, "Optional v1 metadata sync partial skip") }

                // Unified S0-fixed /api/v2/report push & pull
                val completedCopyOutboxIds =
                    outbox.items
                        .filter { item ->
                            item.mode.equals("copy", ignoreCase = true) &&
                                outbox.tracks
                                    .filter { item.id in it.sourceOutboxIds }
                                    .none { it.trackUid in failedTrackUids }
                        }
                        .map { it.id }

                val (pushedCounts, pushedMutations) =
                    pushV2ReportAndApplyCounts(
                        cleanBase = cleanBase,
                        token = token,
                        deviceId = paired.deviceId,
                        completedOutboxIds = completedCopyOutboxIds,
                    )

                // Sync local .m3u8 playlists (3-way merge using PC's real ours) & handoff slots
                val mergedPlaylists = syncPlaylistsWithHost(cleanBase)
                runCatching {
                    val mergedSlotsJson =
                        httpPostJson("$cleanBase/api/v1/handoff", sessionSlotStore.exportSlotsJson())
                    sessionSlotStore.mergeRemoteJson(mergedSlotsJson)
                }

                // Trigger Musikr rescan so downloaded files and playlists appear in mobile library
                if (downloadedCount > 0 || outbox.playlists.isNotEmpty()) {
                    musicRepository.requestIndex(withCache = true)
                    applyOutboxPlaylistsIfAlreadyIndexed(outbox.playlists)
                }

                _syncStatus.value =
                    buildString {
                        if (failedTrackUids.isEmpty()) {
                            append("同步完成（").append(outbox.hostName).append("）：")
                        } else {
                            append("部分完成（").append(failedTrackUids.size).append(" 首待续传）：")
                        }
                        append("新增 ").append(downloadedCount).append(" 首")
                        if (skippedExisting > 0) {
                            append(" · 已有 ").append(skippedExisting).append(" 首")
                        }
                        if (outbox.playlists.isNotEmpty()) {
                            append(" · 歌单 ").append(outbox.playlists.size).append(" 个")
                        }
                        append(" · 上报播放 ").append(pushedCounts).append(" 首 · 标签 ")
                            .append(pushedMutations).append(" 条 · 合并歌单 ").append(mergedPlaylists).append(" 个")
                        if (firstDownloadError != null) {
                            append("（").append(firstDownloadError).append("）")
                        }
                    }
            } catch (authErr: LanSyncAuthException) {
                L.w(authErr, "LAN Sync v2 unauthorized")
                unpairHost()
                _syncStatus.value = "配对已失效，请重新输入电脑端 6 位配对码配对"
            } catch (e: Exception) {
                L.w(e, "LAN sync failed")
                _syncStatus.value = "同步失败：${e.message ?: "无法连接到电脑"}"
            } finally {
                _syncProgress.value = SyncProgressState()
                syncInFlight.set(false)
            }
        }
    }

    suspend fun fetchAndApplyHeaderPatch(
        baseUrl: String,
        song: Song,
        expectedOldHeaderHex: String,
    ): Boolean =
        withContext(Dispatchers.IO) {
            val cleanBase = baseUrl.trim().trimEnd('/')
            val trackUid = song.uid.toString()
            val encodedUid = URLEncoder.encode(trackUid, "UTF-8")
            val raw = httpGetString("$cleanBase/api/v1/patch/$encodedUid")
            val obj = JSONObject(raw)
            val b64 = obj.optString("header_base64", "")
            if (b64.isBlank()) return@withContext false
            val headerBytes = Base64.decode(b64, Base64.DEFAULT)
            context.contentResolver.openFileDescriptor(song.uri, "rw")?.use { pfd ->
                TonewiseCore.applyHeaderPatch(pfd, expectedOldHeaderHex, headerBytes)
                true
            } ?: false
        }

    suspend fun fetchAndApplyHeaderPatchToFile(
        baseUrl: String,
        trackUid: String,
        targetFile: File,
        expectedOldHeaderHex: String,
    ): Boolean =
        withContext(Dispatchers.IO) {
            val cleanBase = baseUrl.trim().trimEnd('/')
            val encodedUid = URLEncoder.encode(trackUid, "UTF-8")
            val raw = httpGetString("$cleanBase/api/v1/patch/$encodedUid")
            val obj = JSONObject(raw)
            val b64 = obj.optString("header_base64", "")
            if (b64.isBlank()) return@withContext false
            val headerBytes = Base64.decode(b64, Base64.DEFAULT)
            TonewiseCore.applyHeaderPatchPath(
                targetFile.absolutePath,
                expectedOldHeaderHex,
                headerBytes,
            )
            true
        }

    fun getEffectiveDjScenes(song: Song): List<String> {
        val rawOverride = getLwwOverride(song.uid.toString(), "dj_scene")
        if (rawOverride != null) {
            return rawOverride
                .split(';', ',', '/', '\u0000')
                .map { it.trim().lowercase() }
                .filter { it.isNotEmpty() }
                .distinct()
        }
        return song.djScenes
    }

    fun getEffectiveDjVocals(song: Song): String =
        getLwwOverride(song.uid.toString(), "dj_vocals") ?: (song.djVocals ?: "unknown")

    fun getEffectiveDjEnergy(song: Song): String =
        getLwwOverride(song.uid.toString(), "dj_energy") ?: (song.djEnergy ?: "unknown")

    fun getEffectiveDjTempo(song: Song): String? =
        getLwwOverride(song.uid.toString(), "tempo")?.takeIf { it.isNotBlank() }
            ?: getLwwOverride(song.uid.toString(), "tempo_bucket")?.takeIf { it.isNotBlank() }
            ?: song.tempoBucket

    fun getEffectiveMoods(song: Song): List<String> {
        val rawOverride = getLwwOverride(song.uid.toString(), "mood")
        if (rawOverride != null) {
            return rawOverride
                .split(';', ',', '/', '\u0000')
                .map { it.trim() }
                .filter { it.isNotEmpty() }
                .distinct()
        }
        return song.moods
    }

    fun getEffectiveMoodClusters(song: Song): List<String> =
        getEffectiveMoods(song).mapNotNull(::resolveMoodClusterId).distinct()

    fun isLoved(song: Song): Boolean = playCountRepository.isLoved(song)

    fun enqueueTagMutation(song: Song, field: String, value: String) {
        val uid = song.uid.toString()
        val cleanField = field.trim().lowercase()
        val cleanValue = value.trim()
        val now = System.currentTimeMillis()
        val deviceId = Build.MODEL ?: "android"

        lwwOverrides[overrideKey(uid, cleanField)] = cleanValue

        if (cleanField == "rating") {
            val ratingNum = cleanValue.toIntOrNull() ?: 4
            val isNowLoved = ratingNum >= 5
            playCountRepository.setLovedByUid(uid, isNowLoved)
            lastFmRepository.onLovedChanged(song, isNowLoved)
        }

        val pendingSize =
            synchronized(this) {
                pendingMutationsList.removeAll { it.trackUid == uid && it.field == cleanField }
                pendingMutationsList.add(
                    TagMutationRecord(
                        trackUid = uid,
                        field = cleanField,
                        value = cleanValue,
                        updatedAtMs = now,
                        deviceId = deviceId,
                    )
                )
                lwwVersion += 1
                pendingMutationsList.size
            }
        _pendingMutationCount.value = pendingSize

        val overrideEntity = LwwOverrideEntity(uid, cleanField, cleanValue)
        val mutationEntity = PendingTagMutationEntity(uid, cleanField, cleanValue, now, deviceId)
        writeBackScope.launch {
            syncDb.lwwMutationDao().upsertOverride(overrideEntity)
            syncDb.lwwMutationDao().upsertPendingMutation(mutationEntity)
            waveformRepository.getOrComputeAudioFrameBlake3(song)
        }
    }

    fun countSongsForScenes(scenes: List<SceneBanner>): Map<String, Int> {
        val songs = musicRepository.library?.songs ?: return emptyMap()
        if (songs.isEmpty()) return scenes.associate { it.id to 0 }
        val tracksJson = getOrBuildTracksJson(songs)
        val counts = HashMap<String, Int>(scenes.size)
        for (scene in scenes) {
            counts[scene.id] =
                runCatching { TonewiseCore.evaluateScene(scene.rawSceneJson, tracksJson).size }
                    .getOrDefault(0)
        }
        return counts
    }

    fun matchSongsForScene(scene: SceneBanner): List<Song> {
        val songs = musicRepository.library?.songs ?: return emptyList()
        val matchedUids =
            runCatching { TonewiseCore.evaluateScene(scene.rawSceneJson, getOrBuildTracksJson(songs)) }
                .getOrElse {
                    L.w(it, "Scene evaluation failed for ${scene.id}")
                    return emptyList()
                }
        val byUid = songs.associateBy { it.uid.toString() }
        return matchedUids.mapNotNull { byUid[it] }
    }

    fun matchSongsByFocus(
        spec: FocusFilterSpec,
        scopedSongUids: Set<String>? = null,
    ): List<Song> {
        val songs = musicRepository.library?.songs ?: return emptyList()
        val sceneJson = buildFocusFilterSceneJson(spec)
        val matched =
            if (sceneJson == null) {
                songs.toList()
            } else {
                val matchedUids =
                    runCatching { TonewiseCore.evaluateScene(sceneJson, getOrBuildTracksJson(songs)) }
                        .getOrElse {
                            L.w(it, "Focus filter evaluation failed")
                            return emptyList()
                        }
                val byUid = songs.associateBy { it.uid.toString() }
                matchedUids.mapNotNull { byUid[it] }
            }
        return if (scopedSongUids == null) {
            matched
        } else {
            matched.filter { it.uid.toString() in scopedSongUids }
        }
    }

    fun matchSongsByDjFilter(
        scene: String?,
        vocals: String?,
        energy: String?,
        excludeVocal: Boolean = false,
        tempoBucket: String? = null,
        moodCluster: String? = null,
        sortArc: String = "flat",
    ): List<Song> {
        val songs = musicRepository.library?.songs ?: return emptyList()
        val sceneJson =
            buildDjFilterSceneJson(
                scene = scene,
                vocals = vocals,
                energy = energy,
                excludeVocal = excludeVocal,
                tempoBucket = tempoBucket,
                moodCluster = moodCluster,
                sortArc = sortArc,
            ) ?: return songs.toList()
        val matchedUids =
            runCatching { TonewiseCore.evaluateScene(sceneJson, getOrBuildTracksJson(songs)) }
                .getOrElse {
                    L.w(it, "DJ filter evaluation failed")
                    return emptyList()
                }
        val byUid = songs.associateBy { it.uid.toString() }
        return matchedUids.mapNotNull { byUid[it] }
    }

    private fun getOrBuildTracksJson(songs: Collection<Song>): String {
        val key =
            TracksCacheKey(
                songsIdentityHash = System.identityHashCode(songs),
                songCount = songs.size,
                lwwVersion = lwwVersion,
                playCountVersion = playCountRepository.updates.value,
            )
        if (cachedTracksKey == key) {
            return cachedTracksJson
        }
        return synchronized(tracksCacheLock) {
            val recheckKey =
                TracksCacheKey(
                    songsIdentityHash = System.identityHashCode(songs),
                    songCount = songs.size,
                    lwwVersion = lwwVersion,
                    playCountVersion = playCountRepository.updates.value,
                )
            if (cachedTracksKey == recheckKey) {
                return cachedTracksJson
            }
            val built = buildTracksJson(songs)
            cachedTracksKey = recheckKey
            cachedTracksJson = built
            built
        }
    }

    private fun buildTracksJson(songs: Collection<Song>): String {
        val tracksJson = JSONArray()
        val nowMs = System.currentTimeMillis()
        for (song in songs) {
            val uid = song.uid.toString()
            val genres = song.genres.mapNotNull { (it.name as? Name.Known)?.raw }.firstOrNull() ?: ""
            val snap = playCountRepository.getSnapshot(song)
            val playCount = snap.playCount
            val lastPlayedMs = snap.lastPlayedMs
            val unplayedDays =
                when {
                    playCount == 0 -> 120
                    lastPlayedMs > 0L -> ((nowMs - lastPlayedMs).coerceAtLeast(0L) / 86_400_000L).toInt()
                    else -> 1
                }
            val rating =
                getLwwOverride(uid, "rating")?.toIntOrNull()
                    ?: if (snap.loved) 5 else 4
            val obj =
                JSONObject()
                    .put("track_uid", uid)
                    .put("title", song.name.raw)
                    .put("dj_scene", JSONArray(getEffectiveDjScenes(song)))
                    .put("dj_vocals", getEffectiveDjVocals(song))
                    .put("dj_energy", getEffectiveDjEnergy(song))
                    .put("tempo_bucket", getEffectiveDjTempo(song) ?: JSONObject.NULL)
                    .put("moods", JSONArray(getEffectiveMoods(song)))
                    .put("genre", genres)
                    .put("rating", rating)
                    .put("unplayed_days", unplayedDays)
            tracksJson.put(obj)
        }
        return tracksJson.toString()
    }

    private fun pushV2ReportAndApplyCounts(
        cleanBase: String,
        token: String,
        deviceId: String,
        completedOutboxIds: List<String>,
    ): Pair<Int, Int> {
        val songs = musicRepository.library?.songs.orEmpty()
        val songsByUid = songs.associateBy { it.uid.toString() }
        val fpByLocalUid =
            syncDb.audioFingerprintDao().getAll().associateBy { it.trackUid }.toMutableMap()
        val syncedAll = syncDb.syncedTrackDao().getAll()
        val syncedByPcUid = syncedAll.associateBy { it.trackUid }
        val songByPcUid = HashMap<String, Song>()

        // Reverse map already cached blake3 & synced relative paths -> local Song
        for ((localUid, fp) in fpByLocalUid) {
            val s = songsByUid[localUid] ?: continue
            songByPcUid[fp.audioFrameBlake3Hex.lowercase()] = s
        }
        val songsByRel = buildSongsByRelSuffixMap(songs)
        for (synced in syncedAll) {
            val match = findMatchingSongByRelPath(synced.relativePath, songsByRel) ?: continue
            songByPcUid.putIfAbsent(synced.trackUid.lowercase(), match)
            cacheFingerprintAlias(match, synced.trackUid)
        }

        fun resolvePcUid(song: Song): String? {
            val localUid = song.uid.toString()
            fpByLocalUid[localUid]?.audioFrameBlake3Hex?.takeIf { it.length == 64 }?.let {
                return it
            }
            val songRel = song.path.components.components.joinToString("/").replace('\\', '/').lowercase()
            for (synced in syncedAll) {
                val syncedRel = synced.relativePath.replace('\\', '/').lowercase()
                if (songRel == syncedRel || songRel.endsWith("/$syncedRel")) {
                    return synced.trackUid
                }
            }
            return waveformRepository.getOrComputeAudioFrameBlake3(song)?.also { b3 ->
                songByPcUid[b3.lowercase()] = song
                syncDb.audioFingerprintDao().getByUid(localUid)?.let { fpByLocalUid[localUid] = it }
            }
        }

        // 1. Delta-only play counts (never send 0 delta, never fake current time for historical counts)
        val playDeltas = ArrayList<PlayDeltaPayload>()
        val pushedWatermarks = ArrayList<Pair<String, Int>>()
        for (song in songs) {
            val localUid = song.uid.toString()
            val snap = playCountRepository.getSnapshot(song)
            val alreadySynced = playCountRepository.getLanSyncedCount(localUid)
            val delta = snap.playCount - alreadySynced
            if (delta <= 0) continue

            val pcUid = resolvePcUid(song) ?: localUid
            val primaryArtist =
                song.artists
                    .mapNotNull { (it.name as? Name.Known)?.raw }
                    .firstOrNull { it.isNotBlank() }
                    ?: ""
            val albumName = (song.album.name as? Name.Known)?.raw ?: ""
            playDeltas.add(
                PlayDeltaPayload(
                    eventId = "$deviceId:$localUid:${snap.playCount}:${snap.lastPlayedMs}",
                    trackUid = pcUid,
                    artist = primaryArtist,
                    title = song.name.raw,
                    album = albumName,
                    playCountDelta = delta,
                    lastPlayedTsMs = snap.lastPlayedMs,
                    deviceId = deviceId,
                )
            )
            pushedWatermarks.add(localUid to snap.playCount)
        }

        // 2. Pending LWW mutations keyed by blake3 + ack key
        val batch = snapshotPendingMutations()
        val keyByRecord = HashMap<TagMutationRecord, String>(batch.size)
        val mutPayloads = ArrayList<MutationPayload>(batch.size)
        for (m in batch) {
            val song = songsByUid[m.trackUid]
            val pcUid =
                when {
                    isHex64(m.trackUid) -> m.trackUid
                    song != null -> resolvePcUid(song) ?: m.trackUid
                    else -> m.trackUid
                }
            val mutKey = "${m.trackUid}|${m.field}|${m.updatedAtMs}"
            keyByRecord[m] = mutKey
            val artist =
                song?.artists?.mapNotNull { (it.name as? Name.Known)?.raw }?.firstOrNull().orEmpty()
            val title = song?.name?.raw.orEmpty()
            mutPayloads.add(
                MutationPayload(
                    mutationKey = mutKey,
                    trackUid = pcUid,
                    artist = artist,
                    title = title,
                    field = m.field,
                    value = m.value,
                    updatedAtMs = m.updatedAtMs,
                    deviceId = m.deviceId,
                )
            )
        }

        val reportResp =
            LanSyncV2Client.postReport(
                baseUrl = cleanBase,
                token = token,
                deviceId = deviceId,
                playDeltas = playDeltas,
                mutations = mutPayloads,
                completedOutboxIds = completedOutboxIds,
            )

        // Advance LAN play high-water marks for pushed songs
        for ((localUid, pushedCount) in pushedWatermarks) {
            playCountRepository.markLanSyncedCount(localUid, pushedCount)
        }

        // Only remove mutations explicitly acknowledged by PC (never silent loss!)
        val acknowledged =
            LanSyncV2Client.filterAcknowledgedMutations(
                batch = batch,
                keyByRecord = keyByRecord,
                ackKeys = reportResp.acknowledgedMutationKeys,
            )
        if (acknowledged.isNotEmpty()) {
            removeSyncedMutations(acknowledged)
        }

        // Merge returned PC effective play counts & loved stars back into mobile ledger
        for ((pcUid, remoteCount) in reportResp.effectiveCounts) {
            if (remoteCount <= 0) continue
            val matchedSong = songByPcUid[pcUid.lowercase()] ?: songsByUid[pcUid]
            val localUid = matchedSong?.uid?.toString()
            if (localUid != null) {
                playCountRepository.mergeRemotePlayCountAndSync(localUid, remoteCount)
            } else if (syncedByPcUid.containsKey(pcUid)) {
                playCountRepository.mergeRemotePlayCountAndSync(pcUid, remoteCount)
            }
        }
        for (lovedPcUid in reportResp.lovedUids) {
            val matchedSong = songByPcUid[lovedPcUid.lowercase()] ?: songsByUid[lovedPcUid]
            if (matchedSong != null && !playCountRepository.isLoved(matchedSong)) {
                playCountRepository.setLoved(matchedSong, true)
            }
        }

        return playDeltas.size to acknowledged.size
    }

    @Synchronized
    private fun snapshotPendingMutations(): List<TagMutationRecord> = pendingMutationsList.toList()

    @Synchronized
    private fun removeSyncedMutations(synced: List<TagMutationRecord>) {
        if (synced.isEmpty()) return
        pendingMutationsList.removeAll(synced.toSet())
        savePendingMutations()
        _pendingMutationCount.value = pendingMutationsList.size
    }

    private fun syncPlaylistsWithHost(baseUrl: String): Int {
        val dir = File(context.filesDir, ".tonewise/playlists")
        if (!dir.isDirectory) return 0
        val m3uFiles = dir.listFiles { f -> f.isFile && f.extension.equals("m3u8", ignoreCase = true) }
            ?: return 0
        var syncedCount = 0
        for (file in m3uFiles) {
            runCatching {
                val name = file.nameWithoutExtension
                val localEntries =
                    file.readLines(Charsets.UTF_8)
                        .map { it.trim() }
                        .filter { it.isNotEmpty() && !it.startsWith("#") }
                        .map(::normalizePlaylistEntryForPc)
                        .filter { it.isNotEmpty() }
                if (localEntries.isEmpty()) return@runCatching
                val baseEntries =
                    loadPlaylistMergeBase(name)
                        .map(::normalizePlaylistEntryForPc)
                        .filter { it.isNotEmpty() }
                val reqJson =
                    JSONObject()
                        .put("playlist_name", name)
                        .put("base", JSONArray(baseEntries))
                        .put("ours", JSONArray(baseEntries))
                        .put("theirs", JSONArray(localEntries))
                        .toString()
                val respRaw = httpPostJson("$baseUrl/api/v1/playlists/merge", reqJson)
                val respObj = JSONObject(respRaw)
                val conflicts = respObj.optJSONArray("conflicts")
                if (conflicts == null || conflicts.length() == 0) {
                    val mergedArr = respObj.optJSONArray("merged") ?: JSONArray()
                    val mergedPaths = List(mergedArr.length()) { idx -> mergedArr.getString(idx) }
                    managedSyncStorage.commitPlaylistM3u8(name, mergedPaths)
                    savePlaylistMergeBase(name, mergedPaths)
                    syncedCount++
                }
            }.onFailure { L.w(it, "Playlist 3-way merge failed for ${file.name}") }
        }
        return syncedCount
    }

    private suspend fun applyOutboxPlaylistsIfAlreadyIndexed(playlists: List<OutboxPlaylistDto>) {
        if (playlists.isEmpty()) return
        val lib = musicRepository.library ?: return
        val songsByRel = buildSongsByRelSuffixMap(lib.songs)
        for (pl in playlists) {
            runCatching {
                val matchedSongs =
                    pl.relPaths.mapNotNull { rel -> findMatchingSongByRelPath(rel, songsByRel) }
                if (matchedSongs.isEmpty()) return@runCatching
                val existing = musicRepository.library?.findPlaylistByName(pl.name)
                if (existing == null) {
                    musicRepository.createPlaylist(pl.name, matchedSongs)
                } else if (existing.songs.map { it.uid } != matchedSongs.map { it.uid }) {
                    musicRepository.rewritePlaylist(existing, matchedSongs)
                }
            }.onFailure { L.w(it, "Direct playlist apply skipped for ${pl.name}") }
        }
    }

    private fun buildSongsByRelSuffixMap(songs: Collection<Song>): List<Pair<String, Song>> =
        songs.map { song ->
            val pathStr =
                song.path.components.components.joinToString("/").replace('\\', '/').lowercase()
            pathStr to song
        }

    private fun findMatchingSongByRelPath(
        targetRelPath: String,
        indexedSongs: List<Pair<String, Song>>,
    ): Song? {
        val cleanRel =
            LanSyncV2Client.sanitizeRelativePathSegments(targetRelPath)
                .joinToString("/")
                .lowercase()
        if (cleanRel.isEmpty()) return null
        return indexedSongs.firstOrNull { (fullRel, _) ->
            fullRel == cleanRel ||
                fullRel == "tonewise/$cleanRel" ||
                fullRel.endsWith("/tonewise/$cleanRel") ||
                fullRel.endsWith("/$cleanRel")
        }?.second
    }

    private fun cacheFingerprintAlias(song: Song, blake3Hex: String) {
        if (!isHex64(blake3Hex)) return
        val localUid = song.uid.toString()
        if (syncDb.audioFingerprintDao().getByUid(localUid) != null) return
        syncDb.audioFingerprintDao().upsert(
            AudioFingerprintCacheEntity(
                trackUid = localUid,
                fileSize = song.size,
                audioFrameBlake3Hex = blake3Hex.lowercase(),
                headerBlake3Hex = "",
                headerSize = 0L,
                updatedAtMs = System.currentTimeMillis(),
            )
        )
    }

    private fun normalizePlaylistEntryForPc(raw: String): String {
        val s = raw.trim().replace('\\', '/')
        val lower = s.lowercase()
        val tonewiseMarker = "/tonewise/"
        val markerIdx = lower.indexOf(tonewiseMarker)
        if (markerIdx >= 0) {
            return s.substring(markerIdx + tonewiseMarker.length).trimStart('/')
        }
        if (lower.startsWith("tonewise/")) {
            return s.substring("tonewise/".length).trimStart('/')
        }
        val musicMarker = "/music/"
        val musicIdx = lower.indexOf(musicMarker)
        if (musicIdx >= 0) {
            return s.substring(musicIdx + musicMarker.length).trimStart('/')
        }
        return s.trimStart('/')
    }

    private fun loadPairedHost(): PairedHostInfo? {
        val kv = syncDb.syncKvDao()
        val url = kv.getValue(KEY_PAIRED_HOST_URL)?.takeIf { it.isNotBlank() } ?: return null
        val token = kv.getValue(KEY_PAIRED_TOKEN)?.takeIf { it.isNotBlank() } ?: return null
        val hostName = kv.getValue(KEY_PAIRED_HOST_NAME) ?: url
        val deviceId = kv.getValue(KEY_PAIRED_DEVICE_ID) ?: getOrCreateStableDeviceId()
        return PairedHostInfo(url, hostName, deviceId, token)
    }

    private fun savePairedHost(info: PairedHostInfo) {
        val kv = syncDb.syncKvDao()
        kv.putValue(SyncKvEntity(KEY_PAIRED_HOST_URL, info.baseUrl))
        kv.putValue(SyncKvEntity(KEY_PAIRED_HOST_NAME, info.hostName))
        kv.putValue(SyncKvEntity(KEY_PAIRED_DEVICE_ID, info.deviceId))
        kv.putValue(SyncKvEntity(KEY_PAIRED_TOKEN, info.token))
    }

    private fun clearPairedHost() {
        val kv = syncDb.syncKvDao()
        kv.putValue(SyncKvEntity(KEY_PAIRED_TOKEN, ""))
    }

    private fun getOrCreateStableDeviceId(): String {
        val kv = syncDb.syncKvDao()
        kv.getValue(KEY_STABLE_DEVICE_ID)?.takeIf { it.isNotBlank() }?.let { return it }
        val modelPrefix =
            (Build.MODEL ?: "android").replace(Regex("[^A-Za-z0-9._-]+"), "-").take(20)
        val generated = "${modelPrefix}_${UUID.randomUUID().toString().replace("-", "").take(10)}"
        kv.putValue(SyncKvEntity(KEY_STABLE_DEVICE_ID, generated))
        return generated
    }

    private fun normalizeBaseUrl(raw: String): String {
        val trimmed = raw.trim().trimEnd('/')
        if (trimmed.isEmpty()) return ""
        return if (trimmed.startsWith("http://", ignoreCase = true) ||
            trimmed.startsWith("https://", ignoreCase = true)
        ) {
            trimmed
        } else {
            "http://$trimmed"
        }
    }

    private fun sanitizeKey(uid: String): String =
        uid.replace(Regex("[^A-Za-z0-9._-]+"), "_").take(80)

    private fun isHex64(s: String): Boolean =
        s.length == 64 && s.all { it in '0'..'9' || it in 'a'..'f' || it in 'A'..'F' }

    private fun overrideKey(uid: String, field: String): String = "$uid||$field"

    private fun getLwwOverride(uid: String, field: String): String? =
        lwwOverrides[overrideKey(uid, field)]

    private fun saveLwwOverrides() {
        val entities =
            lwwOverrides.entries.mapNotNull { (k, v) ->
                val parts = k.split("||", limit = 2)
                if (parts.size == 2) LwwOverrideEntity(parts[0], parts[1], v) else null
            }
        if (entities.isNotEmpty()) {
            syncDb.lwwMutationDao().upsertOverrides(entities)
        }
    }

    private fun loadLwwOverrides(): Map<String, String> =
        syncDb.lwwMutationDao().getAllOverrides().associate {
            overrideKey(it.trackUid, it.field) to it.value
        }

    private fun savePendingMutations() {
        val dao = syncDb.lwwMutationDao()
        val snapshot = pendingMutationsList.toList()
        syncDb.runInTransaction {
            val existing = dao.getAllPendingMutations()
            val keepKeys = snapshot.mapTo(HashSet()) { it.trackUid to it.field }
            for (item in existing) {
                if ((item.trackUid to item.field) !in keepKeys) {
                    dao.deletePendingMutation(item.trackUid, item.field)
                }
            }
            if (snapshot.isNotEmpty()) {
                dao.upsertPendingMutations(
                    snapshot.map {
                        PendingTagMutationEntity(
                            trackUid = it.trackUid,
                            field = it.field,
                            value = it.value,
                            updatedAtMs = it.updatedAtMs,
                            deviceId = it.deviceId,
                        )
                    }
                )
            }
        }
    }

    private fun loadPendingMutations(): List<TagMutationRecord> =
        syncDb.lwwMutationDao().getAllPendingMutations().map {
            TagMutationRecord(
                trackUid = it.trackUid,
                field = it.field,
                value = it.value,
                updatedAtMs = it.updatedAtMs,
                deviceId = it.deviceId,
            )
        }

    private fun loadPlaylistMergeBase(name: String): List<String> {
        val raw = syncDb.syncKvDao().getValue("m3u8_base_$name") ?: return emptyList()
        return runCatching {
                val arr = JSONArray(raw)
                List(arr.length()) { idx -> arr.getString(idx) }
            }
            .getOrDefault(emptyList())
    }

    private fun savePlaylistMergeBase(name: String, lines: List<String>) {
        syncDb.syncKvDao().putValue(SyncKvEntity("m3u8_base_$name", JSONArray(lines).toString()))
    }

    private fun cacheFile(name: String, bytes: ByteArray) {
        val dir = File(context.filesDir, ".tonewise")
        if (!dir.exists()) dir.mkdirs()
        File(dir, name).writeBytes(bytes)
    }

    fun moveScene(fromIndex: Int, toIndex: Int) {
        val current = _scenes.value.toMutableList()
        if (fromIndex !in current.indices || toIndex !in current.indices || fromIndex == toIndex) {
            return
        }
        val moved = current.removeAt(fromIndex)
        current.add(toIndex, moved)
        _scenes.value = current
        val orderJson = JSONArray(current.map { it.id }).toString()
        writeBackScope.launch {
            syncDb.syncKvDao().putValue(SyncKvEntity(KEY_SCENE_ORDER, orderJson))
        }
    }

    private fun applySceneOrder(list: List<SceneBanner>): List<SceneBanner> {
        val raw = syncDb.syncKvDao().getValue(KEY_SCENE_ORDER) ?: return list
        val savedIds =
            runCatching {
                    val arr = JSONArray(raw)
                    List(arr.length()) { idx -> arr.getString(idx) }
                }
                .getOrNull() ?: return list
        if (savedIds.isEmpty()) return list
        val byId = list.associateBy { it.id }
        val ordered = ArrayList<SceneBanner>(list.size)
        val seen = HashSet<String>(list.size)
        for (id in savedIds) {
            val match = byId[id] ?: continue
            if (seen.add(id)) ordered.add(match)
        }
        for (item in list) {
            if (seen.add(item.id)) ordered.add(item)
        }
        return ordered
    }

    private fun loadCachedOrDefaultScenes(): List<SceneBanner> {
        val cached = File(context.filesDir, ".tonewise/scenes_5d.json")
        val raw =
            if (cached.isFile) {
                runCatching { cached.readText(Charsets.UTF_8) }.getOrNull()
            } else {
                null
            } ?: DEFAULT_SCENES_JSON
        return applySceneOrder(runCatching { parseScenesJson(raw) }.getOrDefault(emptyList()))
    }

    private fun parseScenesJson(raw: String): List<SceneBanner> {
        val root = JSONObject(raw)
        val arr = root.optJSONArray("scenes") ?: return emptyList()
        val list = ArrayList<SceneBanner>(arr.length())
        for (i in 0 until arr.length()) {
            val obj = arr.getJSONObject(i)
            if (obj.optBoolean("deleted", false)) continue
            list.add(
                SceneBanner(
                    id = obj.getString("id"),
                    title = obj.getString("title"),
                    subtitle = humanizeSceneSubtitle(obj.optString("subtitle", "")),
                    accentHex = obj.optString("accent_hex", "#005EB8"),
                    rawSceneJson = obj.toString(),
                )
            )
        }
        return list
    }

    private fun humanizeSceneSubtitle(raw: String): String {
        val clean = raw.trim()
        return when {
            clean.equals("DJ_SCENE = focus", ignoreCase = true) -> "器乐与低干扰声场 · 平直叙事"
            clean.equals("DJ_SCENE = coding", ignoreCase = true) -> "作品成组漫游 · 沉浸式编码背景"
            clean.equals("DJ_SCENE = relax", ignoreCase = true) -> "能量渐弱曲线 · 夜间慢速收束"
            clean.equals("DJ_SCENE = energy", ignoreCase = true) -> "能量递进弧线 · 高动态节拍"
            clean.equals("DJ_SCENE = pop", ignoreCase = true) -> "人声与旋律精选 · 明快律动"
            else -> clean
        }
    }

    private fun httpGetString(url: String): String = String(httpGetBytes(url), Charsets.UTF_8)

    private fun httpGetBytes(url: String): ByteArray {
        val conn = (URL(url).openConnection() as HttpURLConnection).apply {
            connectTimeout = 5_000
            readTimeout = 15_000
            requestMethod = "GET"
        }
        return try {
            if (conn.responseCode !in 200..299) {
                throw IllegalStateException("HTTP ${conn.responseCode} for $url")
            }
            conn.inputStream.readBytes()
        } finally {
            conn.disconnect()
        }
    }

    private fun httpPostJson(url: String, body: String): String {
        val conn = (URL(url).openConnection() as HttpURLConnection).apply {
            connectTimeout = 5_000
            readTimeout = 15_000
            requestMethod = "POST"
            doOutput = true
            setRequestProperty("Content-Type", "application/json; charset=utf-8")
        }
        return try {
            conn.outputStream.use { it.write(body.toByteArray(Charsets.UTF_8)) }
            if (conn.responseCode !in 200..299) {
                throw IllegalStateException("HTTP ${conn.responseCode} for $url")
            }
            String(conn.inputStream.readBytes(), Charsets.UTF_8)
        } finally {
            conn.disconnect()
        }
    }

    data class M3u8Entry(
        val durationMs: Long,
        val title: String,
        val artists: List<String>,
        val pathComponents: Components,
    )

    companion object {
        private const val KEY_SCENE_ORDER = "scene_order_ids"
        private const val KEY_LWW_OVERRIDES = "lww_overrides"
        private const val KEY_PENDING_MUTATIONS = "pending_mutations"
        private const val KEY_PAIRED_HOST_URL = "lan_v2_host_url"
        private const val KEY_PAIRED_HOST_NAME = "lan_v2_host_name"
        private const val KEY_PAIRED_DEVICE_ID = "lan_v2_device_id"
        private const val KEY_PAIRED_TOKEN = "lan_v2_token"
        private const val KEY_STABLE_DEVICE_ID = "lan_v2_stable_device_id"

        private fun escapeJson(value: String): String =
            buildString {
                for (ch in value) {
                    when (ch) {
                        '"' -> append("\\\"")
                        '\\' -> append("\\\\")
                        '\n' -> append("\\n")
                        '\r' -> append("\\r")
                        '\t' -> append("\\t")
                        else -> append(ch)
                    }
                }
            }

        private fun predicateJsonStr(field: String, op: String, value: String): String =
            """{"predicate":{"field":"${escapeJson(field)}","op":"${escapeJson(op)}","values":["${escapeJson(value)}"]}}"""

        private fun predicateJsonList(
            field: String,
            op: String,
            values: Collection<String>,
        ): String {
            val serialized = values.joinToString(",") { "\"${escapeJson(it)}\"" }
            return """{"predicate":{"field":"${escapeJson(field)}","op":"${escapeJson(op)}","values":[$serialized]}}"""
        }

        private val MOOD_SYNONYM_TO_CLUSTER: Map<String, String> =
            mapOf(
                "calm_relaxed" to "calm_relaxed",
                "calm" to "calm_relaxed",
                "chill" to "calm_relaxed",
                "mellow" to "calm_relaxed",
                "soothing" to "calm_relaxed",
                "peaceful" to "calm_relaxed",
                "serene" to "calm_relaxed",
                "tranquil" to "calm_relaxed",
                "gentle" to "calm_relaxed",
                "relaxed" to "calm_relaxed",
                "ambient" to "calm_relaxed",
                "meditative" to "calm_relaxed",
                "bright_joyful" to "bright_joyful",
                "cheerful" to "bright_joyful",
                "happy" to "bright_joyful",
                "upbeat" to "bright_joyful",
                "bright" to "bright_joyful",
                "sunny" to "bright_joyful",
                "playful" to "bright_joyful",
                "joyful" to "bright_joyful",
                "optimistic" to "bright_joyful",
                "warm_bright" to "bright_joyful",
                "uplifting" to "bright_joyful",
                "deep_introspective" to "deep_introspective",
                "melancholic" to "deep_introspective",
                "melancholy" to "deep_introspective",
                "contemplative" to "deep_introspective",
                "introspective" to "deep_introspective",
                "somber" to "deep_introspective",
                "wistful" to "deep_introspective",
                "reflective" to "deep_introspective",
                "nostalgic" to "deep_introspective",
                "brooding" to "deep_introspective",
                "solemn" to "deep_introspective",
                "warm_romantic" to "warm_romantic",
                "romantic" to "warm_romantic",
                "intimate" to "warm_romantic",
                "warm" to "warm_romantic",
                "tender" to "warm_romantic",
                "sentimental" to "warm_romantic",
                "dreamy" to "warm_romantic",
                "sensual" to "warm_romantic",
                "nostalgic_warm" to "warm_romantic",
                "lyrical" to "warm_romantic",
                "high_tension" to "high_tension",
                "intense" to "high_tension",
                "energetic" to "high_tension",
                "dramatic" to "high_tension",
                "driving" to "high_tension",
                "epic" to "high_tension",
                "aggressive" to "high_tension",
                "passionate" to "high_tension",
                "fierce" to "high_tension",
                "urgent" to "high_tension",
                "dynamic" to "high_tension",
            )

        fun resolveMoodClusterId(rawMood: String): String? =
            MOOD_SYNONYM_TO_CLUSTER[rawMood.trim().lowercase()]

        fun buildFocusFilterSceneJson(spec: FocusFilterSpec): String? {
            val predicates = ArrayList<String>()
            fun addFacet(field: String, map: Map<String, FocusPillState>) {
                val includes =
                    map.entries
                        .filter { it.value == FocusPillState.INCLUDE }
                        .map { it.key.trim() }
                        .filter { it.isNotEmpty() }
                if (includes.isNotEmpty()) {
                    predicates.add(predicateJsonList(field, "in", includes))
                }
                val excludes =
                    map.entries
                        .filter { it.value == FocusPillState.EXCLUDE }
                        .map { it.key.trim() }
                        .filter { it.isNotEmpty() }
                if (excludes.isNotEmpty()) {
                    predicates.add(predicateJsonList(field, "not_in", excludes))
                }
            }
            addFacet("dj_scene", spec.scenes)
            addFacet("dj_vocals", spec.vocals)
            addFacet("dj_energy", spec.energies)
            addFacet("tempo_bucket", spec.tempos)
            addFacet("mood_cluster", spec.moods)
            spec.unplayedDaysGte?.let { days ->
                predicates.add(
                    """{"predicate":{"field":"unplayed_days_gte","op":"gte","values":[$days]}}"""
                )
            }
            val effectiveArc = spec.sortArc.ifBlank { "flat" }
            if (predicates.isEmpty() && (effectiveArc == "flat" || effectiveArc == "work_shuffle")) {
                return null
            }
            return """{"id":"00000000-0000-4000-8000-000000000003","title":"Focus Boolean Funnel","updated_at_ms":0,"sort_arc":"${escapeJson(effectiveArc)}","tempo_null_passthrough":true,"rule":{"all":[${predicates.joinToString(",")}]}}"""
        }

        fun buildDjFilterSceneJson(
            scene: String?,
            vocals: String?,
            energy: String?,
            excludeVocal: Boolean = false,
            tempoBucket: String? = null,
            moodCluster: String? = null,
            sortArc: String = "flat",
        ): String? {
            val predicates = ArrayList<String>()
            scene?.takeIf { it.isNotBlank() && it != "all" }?.let {
                predicates.add(predicateJsonStr("dj_scene", "in", it))
            }
            if (excludeVocal) {
                predicates.add("""{"not":${predicateJsonStr("dj_vocals", "eq", "vocal")}}""")
            } else {
                vocals?.takeIf { it.isNotBlank() && it != "all" }?.let {
                    predicates.add(predicateJsonStr("dj_vocals", "eq", it))
                }
            }
            energy?.takeIf { it.isNotBlank() && it != "all" }?.let {
                predicates.add(predicateJsonStr("dj_energy", "eq", it))
            }
            tempoBucket?.takeIf { it.isNotBlank() && it != "all" }?.let {
                predicates.add(predicateJsonStr("tempo_bucket", "eq", it))
            }
            moodCluster?.takeIf { it.isNotBlank() && it != "all" }?.let {
                predicates.add(predicateJsonStr("mood_cluster", "in", it))
            }
            val effectiveArc = sortArc.ifBlank { "flat" }
            if (predicates.isEmpty() && (effectiveArc == "flat" || effectiveArc == "work_shuffle")) {
                return null
            }
            return """{"id":"00000000-0000-4000-8000-000000000002","title":"DJ 5D Matrix Filter","updated_at_ms":0,"sort_arc":"${escapeJson(effectiveArc)}","tempo_null_passthrough":true,"rule":{"all":[${predicates.joinToString(",")}]}}"""
        }

        fun sanitizePlaylistFilename(raw: String): String =
            raw.trim().ifEmpty { "Default" }.replace(Regex("[\\\\/:*?\"<>|\\s]+"), "_")

        fun formatM3u8Content(songs: List<Song>): String =
            formatM3u8Entries(
                songs.map { song ->
                    M3u8Entry(
                        durationMs = song.durationMs,
                        title = song.name.raw,
                        artists = song.artists.mapNotNull { (it.name as? Name.Known)?.raw },
                        pathComponents = song.path.components,
                    )
                }
            )

        fun formatM3u8Entries(entries: List<M3u8Entry>): String {
            val sb = StringBuilder()
            sb.append("#EXTM3U\n")
            for (entry in entries) {
                val secs = (entry.durationMs / 1000L).coerceAtLeast(0L)
                val artist = entry.artists.filter { it.isNotBlank() }.joinToString(", ")
                val extinf = if (artist.isNotEmpty()) "$artist - ${entry.title}" else entry.title
                sb.append("#EXTINF:").append(secs).append(',').append(extinf).append('\n')
                val relPath = entry.pathComponents.components.joinToString("/").replace('\\', '/')
                sb.append(relPath).append('\n')
            }
            return sb.toString()
        }

        private const val DEFAULT_SCENES_JSON = """
            {
              "schema_version": 1,
              "scenes": [
                {
                  "id": "11111111-1111-4111-8111-111111111111",
                  "title": "Focus · 深度专注",
                  "subtitle": "器乐与低干扰声场 · 平直叙事",
                  "accent_hex": "#2B5B84",
                  "updated_at_ms": 1763251200000,
                  "sort_arc": "flat",
                  "tempo_null_passthrough": true,
                  "rule": {
                    "all": [
                      { "predicate": { "field": "dj_scene", "op": "in", "values": ["focus"] } }
                    ]
                  }
                },
                {
                  "id": "22222222-2222-4222-8222-222222222222",
                  "title": "Coding · 心流编程",
                  "subtitle": "作品成组漫游 · 沉浸式编码背景",
                  "accent_hex": "#3B7A57",
                  "updated_at_ms": 1763251200000,
                  "sort_arc": "work_shuffle",
                  "tempo_null_passthrough": true,
                  "rule": {
                    "all": [
                      { "predicate": { "field": "dj_scene", "op": "in", "values": ["coding"] } }
                    ]
                  }
                },
                {
                  "id": "33333333-3333-4333-8333-333333333333",
                  "title": "Relax · 松弛休憩",
                  "subtitle": "能量渐弱曲线 · 夜间慢速收束",
                  "accent_hex": "#5B6E8C",
                  "updated_at_ms": 1763251200000,
                  "sort_arc": "energy_desc_sleep",
                  "tempo_null_passthrough": true,
                  "rule": {
                    "all": [
                      { "predicate": { "field": "dj_scene", "op": "in", "values": ["relax"] } }
                    ]
                  }
                },
                {
                  "id": "55555555-5555-4555-8555-555555555555",
                  "title": "Energy · 高能律动",
                  "subtitle": "能量递进弧线 · 高动态节拍",
                  "accent_hex": "#C85A32",
                  "updated_at_ms": 1763251200000,
                  "sort_arc": "energy_asc",
                  "tempo_null_passthrough": true,
                  "rule": {
                    "all": [
                      { "predicate": { "field": "dj_scene", "op": "in", "values": ["energy"] } }
                    ]
                  }
                },
                {
                  "id": "66666666-6666-4666-8666-666666666666",
                  "title": "Pop · 流行精选",
                  "subtitle": "人声与旋律精选 · 明快律动",
                  "accent_hex": "#9E4770",
                  "updated_at_ms": 1763251200000,
                  "sort_arc": "flat",
                  "tempo_null_passthrough": true,
                  "rule": {
                    "all": [
                      { "predicate": { "field": "dj_scene", "op": "in", "values": ["pop"] } }
                    ]
                  }
                },
                {
                  "id": "44444444-4444-4444-8444-444444444444",
                  "title": "沉睡馆藏唤醒",
                  "subtitle": "尚未播放或超过 90 天未重温的本地曲库",
                  "accent_hex": "#8B5E3C",
                  "updated_at_ms": 1763251200000,
                  "sort_arc": "rediscovery_90d",
                  "tempo_null_passthrough": true,
                  "rule": {
                    "all": [
                      { "predicate": { "field": "unplayed_days_gte", "op": "gte", "values": [90] } }
                    ]
                  }
                }
              ]
            }
        """
    }
}
