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
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.net.Uri
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.net.wifi.WifiManager
import android.os.Build
import android.os.Environment
import android.util.Base64
import com.shawnshi.tonewise.playback.service.WorkShuffleOrder
import dagger.hilt.android.qualifiers.ApplicationContext
import java.io.File
import java.io.IOException
import java.net.Inet4Address
import java.net.InetAddress
import java.net.SocketTimeoutException
import java.net.URI
import java.net.URLEncoder
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong
import java.util.concurrent.atomic.AtomicReference
import javax.inject.Inject
import javax.inject.Singleton
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.sync.withPermit
import kotlinx.coroutines.withContext
import com.shawnshi.tonewise.detail.MusicBioRepository
import com.shawnshi.tonewise.music.MusicRepository
import com.shawnshi.tonewise.playback.dsp.DspRepository
import com.shawnshi.tonewise.playback.persist.PlayCountRepository
import com.shawnshi.tonewise.playback.state.PlaybackStateManager
import com.shawnshi.tonewise.playback.ui.WaveformRepository
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
    val hostId: String = "",
)

enum class SyncTransportMode {
    LAN,
    USB,
}

data class SyncProgressState(
    val isRunning: Boolean = false,
    val phase: String = "",
    val completedTracks: Int = 0,
    val totalTracks: Int = 0,
    val downloadedBytes: Long = 0L,
    val totalBytes: Long = 0L,
    val currentTrackTitle: String = "",
)

data class PendingLargePruneState(
    val count: Int,
    val localAudioCount: Int,
    val ratio: Double,
    val baseUrl: String,
    val transport: SyncTransportMode,
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
    private val playbackStateManager: PlaybackStateManager,
    private val dspRepository: DspRepository,
) {
    private val nsdManager = context.getSystemService(Context.NSD_SERVICE) as? NsdManager
    private val syncDb: TonewiseSyncDatabase by lazy { TonewiseSyncDatabase.getInstance(context) }
    private val lwwStore = LwwMutationStore({ syncDb }, { context })
    private var activeDiscoveryListener: NsdManager.DiscoveryListener? = null
    private val resolveLock = Any()
    private val pendingResolveQueue = ArrayDeque<NsdServiceInfo>()
    private var resolveInFlight = false
    private var multicastLock: WifiManager.MulticastLock? = null
    private val syncInFlight = AtomicBoolean(false)
    private val presetSlotsSeeding = AtomicBoolean(false)
    private val syncScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    @Volatile private var activeSyncJob: Job? = null
    @Volatile private var activeCancelToken: LanHttpClient.CancelToken? = null

    private data class PendingPlaylistReconcile(
        val playlist: OutboxPlaylistDto,
        var remainingAttempts: Int = 3,
    )

    private val pendingOutboxPlaylists = ConcurrentHashMap<String, PendingPlaylistReconcile>()
    private val playlistReconcileMutex = Mutex()

    private val _discoveredHosts = MutableStateFlow<List<DiscoveredSyncHost>>(emptyList())
    val discoveredHosts: StateFlow<List<DiscoveredSyncHost>> = _discoveredHosts

    private val _pairedHost = MutableStateFlow<PairedHostInfo?>(null)
    val pairedHost: StateFlow<PairedHostInfo?> = _pairedHost

    private val _scenes = MutableStateFlow(loadCachedOrDefaultScenes())
    val scenes: StateFlow<List<SceneBanner>> = _scenes

    private val _syncStatus = MutableStateFlow("局域网同步就绪（正在发现同一 Wi-Fi 下的电脑…）")
    val syncStatus: StateFlow<String> = _syncStatus

    private val _syncProgress = MutableStateFlow(SyncProgressState())
    val syncProgress: StateFlow<SyncProgressState> = _syncProgress

    private val _pendingLargePrune = MutableStateFlow<PendingLargePruneState?>(null)
    val pendingLargePrune: StateFlow<PendingLargePruneState?> = _pendingLargePrune

    private val tracksCacheLock = Any()
    @Volatile private var cachedTracksKey: TracksCacheKey? = null
    @Volatile private var cachedTracksJson: String = "[]"

    val pendingMutationCount: StateFlow<Int>
        get() = lwwStore.pendingMutationCount

    private data class TracksCacheKey(
        val songsIdentityHash: Int,
        val songCount: Int,
        val lwwVersion: Long,
        val playCountVersion: Long,
    )

    private val writeBackScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val pendingWriteBackJobs = ConcurrentHashMap<String, Job>()
    private val pendingRemoteLedgerLock = Any()
    private val pendingRemoteCounts = HashMap<String, Int>()
    private val pendingRemoteLoved = HashSet<String>()
    private val pendingRemoteUnloved = HashSet<String>()
    private val pendingEvictTrackMtimes = HashMap<String, Long>()
    private var pendingRemoteReconcileRemaining = 0

    val hasWritableMusicSafGrant: Boolean
        get() = managedSyncStorage.hasWritableMusicSafGrant()

    fun requestLibraryRescan() {
        syncScope.launch {
            runCatching {
                val stale = syncDb.syncedTrackDao().getAll().filter { !managedSyncStorage.existsInManagedStorage(it) }
                for (entity in stale) {
                    syncDb.syncedTrackDao().deleteByUid(entity.trackUid)
                }
            }
            musicRepository.requestIndex(withCache = true)
        }
    }

    init {
        LanHttpClient.lanConnectionOpener = opener@{ targetUrl ->
            if (LanHttpClient.isLoopbackUrl(targetUrl.toString())) {
                return@opener null
            }
            val cm = context.getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager
                ?: return@opener null
            val wifiNet =
                cm.allNetworks.firstOrNull { net ->
                    val caps = cm.getNetworkCapabilities(net) ?: return@firstOrNull false
                    caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) &&
                        caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN)
                } ?: return@opener null
            wifiNet.openConnection(targetUrl, java.net.Proxy.NO_PROXY) as? java.net.HttpURLConnection
        }
        syncScope.launch {
            runCatching { syncDb.ensureLegacyMigrated(context) }
            runCatching { lwwStore.ensureLoadedOnBackground(context) }
            val loadedHost = runCatching { lwwStore.loadPairedHost() }.getOrNull()
            if (_pairedHost.value == null && loadedHost != null) {
                _pairedHost.value = loadedHost
            }
            _scenes.value = loadCachedOrDefaultScenes()
            runCatching {
                LanHttpClient.pruneStalePartFiles(File(context.cacheDir, "lan_sync_parts"))
            }
        }
        musicRepository.addUpdateListener(
            object : MusicRepository.UpdateListener {
                override fun onMusicChanges(changes: MusicRepository.Changes) {
                    if (changes.deviceLibrary) {
                        writeBackScope.launch {
                            ensurePresetRelaySlotsSeeded()
                        }
                        if (pendingOutboxPlaylists.isNotEmpty()) {
                            writeBackScope.launch {
                                reconcilePendingOutboxPlaylists()
                            }
                        }
                        val hasPendingLedger =
                            synchronized(pendingRemoteLedgerLock) {
                                pendingRemoteCounts.isNotEmpty() ||
                                    pendingRemoteLoved.isNotEmpty() ||
                                    pendingRemoteUnloved.isNotEmpty() ||
                                    pendingEvictTrackMtimes.isNotEmpty()
                            }
                        if (hasPendingLedger) {
                            writeBackScope.launch {
                                reconcilePendingRemoteLedger()
                            }
                        }
                    }
                }
            }
        )
    }

    fun scheduleM3u8WriteBack(sceneName: String, songs: List<Song>) {
        val snapshot = songs.toList()
        val safeName = sanitizePlaylistFilename(sceneName)
        val jobKey = safeName.lowercase()
        pendingWriteBackJobs.remove(jobKey)?.cancel()
        val legacyName = legacyUnderscoredPlaylistFilename(sceneName)
        val cleanupLegacy = !legacyName.equals(safeName, ignoreCase = true)
        pendingWriteBackJobs[jobKey] =
            writeBackScope.launch {
                delay(2_000L)
                val payload = formatM3u8Content(snapshot, sceneName).toByteArray(Charsets.UTF_8)
                runCatching {
                    val internalDir = File(context.filesDir, ".tonewise/playlists")
                    internalDir.mkdirs()
                    File(internalDir, "$safeName.m3u8").writeBytes(payload)
                    if (cleanupLegacy) {
                        File(internalDir, "$legacyName.m3u8").takeIf { it.isFile }?.delete()
                    }
                }
                runCatching {
                    @Suppress("DEPRECATION")
                    val musicDir =
                        Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_MUSIC)
                    val extDir = File(musicDir, ".tonewise/playlists")
                    if (extDir.exists() || extDir.mkdirs()) {
                        File(extDir, "$safeName.m3u8").writeBytes(payload)
                        if (cleanupLegacy) {
                            File(extDir, "$legacyName.m3u8").takeIf { it.isFile }?.delete()
                        }
                    }
                }
                L.d("Debounced .m3u8 write-back flushed for $safeName (${snapshot.size} tracks)")
            }
    }

    private fun acquireMulticastLock() {
        runCatching {
            if (multicastLock == null) {
                val wifi =
                    context.applicationContext.getSystemService(Context.WIFI_SERVICE) as? WifiManager
                multicastLock =
                    wifi?.createMulticastLock("Tonewise-mDNS-Discovery")?.apply {
                        setReferenceCounted(false)
                    }
            }
            if (multicastLock?.isHeld == false) {
                multicastLock?.acquire()
            }
        }.onFailure { L.d("MulticastLock acquire skipped: ${it.message}") }
    }

    private fun releaseMulticastLock() {
        runCatching {
            if (multicastLock?.isHeld == true) {
                multicastLock?.release()
            }
        }
    }

    private fun enqueueResolveLocked(manager: NsdManager, serviceInfo: NsdServiceInfo) {
        val name = serviceInfo.serviceName
        if (pendingResolveQueue.none { it.serviceName == name }) {
            pendingResolveQueue.addLast(serviceInfo)
        }
        if (!resolveInFlight) {
            drainResolveQueueLocked(manager)
        }
    }

    private fun drainResolveQueueLocked(manager: NsdManager) {
        if (activeDiscoveryListener == null) {
            pendingResolveQueue.clear()
            resolveInFlight = false
            return
        }
        val next = pendingResolveQueue.removeFirstOrNull()
        if (next == null) {
            resolveInFlight = false
            return
        }
        resolveInFlight = true
        try {
            @Suppress("DEPRECATION")
            manager.resolveService(
                next,
                object : NsdManager.ResolveListener {
                    override fun onResolveFailed(info: NsdServiceInfo, errorCode: Int) {
                        L.w("NSD resolve failed (${info.serviceName}): $errorCode")
                        synchronized(resolveLock) {
                            resolveInFlight = false
                            drainResolveQueueLocked(manager)
                        }
                    }

                    override fun onServiceResolved(resolved: NsdServiceInfo) {
                        if (activeDiscoveryListener != null) {
                            val host = selectPreferredResolvedHost(resolved)
                            if (!host.isNullOrBlank()) {
                                val entry =
                                    DiscoveredSyncHost(
                                        resolved.serviceName,
                                        host,
                                        resolved.port,
                                    )
                                val updated =
                                    _discoveredHosts.value.filterNot {
                                        it.host == entry.host && it.port == entry.port
                                    } + entry
                                _discoveredHosts.value = updated
                            }
                        }
                        synchronized(resolveLock) {
                            resolveInFlight = false
                            drainResolveQueueLocked(manager)
                        }
                    }
                },
            )
        } catch (t: Throwable) {
            L.w(t, "NSD resolveService failed synchronously")
            resolveInFlight = false
            drainResolveQueueLocked(manager)
        }
    }

    fun startDiscovery() {
        val manager = nsdManager ?: return
        if (activeDiscoveryListener != null) return
        acquireMulticastLock()

        val listener =
            object : NsdManager.DiscoveryListener {
                override fun onDiscoveryStarted(regType: String) {
                    L.d("NSD discovery started for $regType")
                }

                override fun onServiceFound(serviceInfo: NsdServiceInfo) {
                    if (serviceInfo.serviceType.contains("_tonewise._tcp")) {
                        synchronized(resolveLock) {
                            if (activeDiscoveryListener != null) {
                                enqueueResolveLocked(manager, serviceInfo)
                            }
                        }
                    }
                }

                override fun onServiceLost(serviceInfo: NsdServiceInfo) {
                    _discoveredHosts.value =
                        _discoveredHosts.value.filterNot { it.serviceName == serviceInfo.serviceName }
                }

                override fun onDiscoveryStopped(serviceType: String) {
                    synchronized(resolveLock) {
                        pendingResolveQueue.clear()
                        resolveInFlight = false
                    }
                    releaseMulticastLock()
                }

                override fun onStartDiscoveryFailed(serviceType: String, errorCode: Int) {
                    activeDiscoveryListener = null
                    synchronized(resolveLock) {
                        pendingResolveQueue.clear()
                        resolveInFlight = false
                    }
                    releaseMulticastLock()
                }

                override fun onStopDiscoveryFailed(serviceType: String, errorCode: Int) {}
            }

        activeDiscoveryListener = listener
        runCatching {
            manager.discoverServices("_tonewise._tcp.", NsdManager.PROTOCOL_DNS_SD, listener)
        }.onFailure {
            activeDiscoveryListener = null
            releaseMulticastLock()
        }
    }

    fun stopDiscovery() {
        val listener = activeDiscoveryListener ?: return
        activeDiscoveryListener = null
        synchronized(resolveLock) {
            pendingResolveQueue.clear()
            resolveInFlight = false
        }
        runCatching { nsdManager?.stopServiceDiscovery(listener) }
        releaseMulticastLock()
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
                syncDb.ensureLegacyMigrated(context)
                lwwStore.ensureLoadedOnBackground(context)
                val deviceId = lwwStore.getOrCreateStableDeviceId()
                val deviceName = Build.MODEL?.takeIf { it.isNotBlank() } ?: "Android Phone"
                val pairResp = LanSyncV2Client.pair(cleanBase, pairingCode.trim(), deviceId, deviceName)
                val info =
                    PairedHostInfo(
                        baseUrl = cleanBase,
                        hostName = pairResp.hostName,
                        deviceId = pairResp.deviceId,
                        token = pairResp.token,
                        hostId = pairResp.hostId,
                    )
                lwwStore.savePairedHost(info)
                _pairedHost.value = info
                _syncStatus.value = "配对成功：已绑定「${pairResp.hostName}」，可以开始同步"
                true
            } catch (e: LanSyncRateLimitException) {
                _syncStatus.value = "配对尝试过于频繁，请等待 ${e.retryAfterSeconds} 秒后再试"
                false
            } catch (e: LanSyncAuthException) {
                _syncStatus.value = "配对失败：6 位配对码不正确"
                false
            } catch (e: Exception) {
                L.w(e, "Pairing failed")
                _syncStatus.value = formatConnectionError(cleanBase, e, vpnActive = isVpnActive())
                false
            }
        }

    fun unpairHost(notifyRemote: Boolean = true) {
        val previous = _pairedHost.value
        lwwStore.clearPairedHost()
        _pairedHost.value = null
        _syncStatus.value = "已解除电脑配对"
        if (notifyRemote && previous != null && previous.token.isNotBlank()) {
            syncScope.launch {
                runCatching { LanSyncV2Client.unpair(previous.baseUrl, previous.token) }
            }
        }
    }

    fun startSync(baseUrl: String) {
        val cleanBase = normalizeBaseUrl(baseUrl)
        if (cleanBase.isBlank()) {
            _syncStatus.value = "请先选择或填写电脑同步地址"
            return
        }
        val startedViaService =
            runCatching {
                LanSyncForegroundService.startSync(context, cleanBase)
                true
            }.getOrDefault(false)
        if (!startedViaService) {
            syncScope.launch { syncWithHost(cleanBase) }
        }
    }

    fun cancelSync() {
        activeCancelToken?.cancel()
        activeSyncJob?.cancel()
        _syncStatus.value = "同步已取消（已下载曲目与断点缓存已保留，可随时续传）"
    }

    @Volatile private var localLargePruneConfirmedOnce: Boolean = false

    fun confirmLargePruneLocally() {
        localLargePruneConfirmedOnce = true
    }

    fun dismissPendingLargePrune() {
        _pendingLargePrune.value = null
        localLargePruneConfirmedOnce = false
    }

    fun confirmLargePruneAndResync() {
        val pending = _pendingLargePrune.value
        localLargePruneConfirmedOnce = true
        _pendingLargePrune.value = null
        val targetUrl =
            pending?.baseUrl?.takeIf { it.isNotBlank() }
                ?: _pairedHost.value?.baseUrl.orEmpty()
        if (targetUrl.isNotBlank()) {
            startSync(targetUrl)
        }
    }

    suspend fun syncWithHost(baseUrl: String) {
        syncWithHost(baseUrl = baseUrl, transport = SyncTransportMode.LAN)
    }

    suspend fun syncWithHost(
        baseUrl: String,
        transport: SyncTransportMode,
        usbNonce: String? = null,
        expectedHostId: String? = null,
        allowUsbAutoPair: Boolean = false,
    ) {
        withContext(Dispatchers.IO) {
            if (!syncInFlight.compareAndSet(false, true)) {
                return@withContext
            }
            managedSyncStorage.invalidatePathCache()
            val cleanBase = normalizeBaseUrl(baseUrl)
            if (cleanBase.isBlank()) {
                _syncStatus.value = "请先选择或填写电脑同步地址"
                syncInFlight.set(false)
                return@withContext
            }

            syncDb.ensureLegacyMigrated(context)
            lwwStore.ensureLoadedOnBackground(context)

            val transportTag = if (transport == SyncTransportMode.USB) "usb" else "lan"
            val transportLabel = if (transport == SyncTransportMode.USB) "USB 直连" else "局域网"
            _syncStatus.value = "正在通过$transportLabel 连接 $cleanBase 获取同步清单…"
            _syncProgress.value =
                SyncProgressState(isRunning = true, phase = "正在连接电脑并检查发送队列…")

            LanHttpClient.CancelToken.withCoroutineCancelToken { cancelToken ->
                activeSyncJob = currentCoroutineContext()[Job]
                activeCancelToken = cancelToken
                var activePaired: PairedHostInfo? = null
                var activeToken: String? = null
                var lastProgressPushMs = 0L

                fun pushProgress(
                    phase: String,
                    isRunning: Boolean,
                    completedTracks: Int = 0,
                    totalTracks: Int = 0,
                    downloadedBytes: Long = 0L,
                    totalBytes: Long = 0L,
                    currentTrackTitle: String = "",
                    addedCount: Int = 0,
                    updatedCount: Int = 0,
                    skippedCount: Int = 0,
                    deletedCount: Int = 0,
                    blockedForeignPaths: List<String> = emptyList(),
                    pendingLargePruneCount: Int = 0,
                    localAudioCount: Int = 0,
                    pendingLargePruneRatio: Double = 0.0,
                    errorMessage: String? = null,
                    completed: Boolean = false,
                    force: Boolean = false,
                ) {
                    val p = activePaired ?: return
                    val tok = activeToken ?: return
                    val nowMs = System.currentTimeMillis()
                    if (!force && isRunning && nowMs - lastProgressPushMs < 500L) {
                        return
                    }
                    lastProgressPushMs = nowMs
                    val ctrl =
                        runCatching {
                            LanSyncV2Client.postSyncProgress(
                                baseUrl = cleanBase,
                                token = tok,
                                payload =
                                    SyncSessionProgressPayload(
                                        deviceId = p.deviceId,
                                        transport = transportTag,
                                        phase = phase,
                                        isRunning = isRunning,
                                        completedTracks = completedTracks,
                                        totalTracks = totalTracks,
                                        downloadedBytes = downloadedBytes,
                                        totalBytes = totalBytes,
                                        currentTrackTitle = currentTrackTitle,
                                        addedCount = addedCount,
                                        updatedCount = updatedCount,
                                        skippedCount = skippedCount,
                                        deletedCount = deletedCount,
                                        blockedForeignPaths = blockedForeignPaths,
                                        pendingLargePruneCount = pendingLargePruneCount,
                                        localAudioCount = localAudioCount,
                                        pendingLargePruneRatio = pendingLargePruneRatio,
                                        errorMessage = errorMessage,
                                        completed = completed,
                                    ),
                                cancelToken = if (isRunning) cancelToken else null,
                            )
                        }.getOrNull()
                    if (isRunning && ctrl?.cancelRequested == true) {
                        cancelToken.cancel()
                        cancelToken.throwIfCancelled()
                    }
                }

                try {
                    cancelToken.throwIfCancelled()
                    val pingInfo = LanSyncV2Client.pingInfo(cleanBase, cancelToken)
                    if (transport == SyncTransportMode.USB && !pingInfo.ok) {
                        throw IOException("无法通过 USB 隧道连接电脑端同步服务（$cleanBase 未响应）")
                    }
                    if (!expectedHostId.isNullOrBlank() &&
                        pingInfo.ok &&
                        pingInfo.hostId.isNotBlank() &&
                        !pingInfo.hostId.equals(expectedHostId.trim(), ignoreCase = true)
                    ) {
                        throw IOException("目标电脑身份不匹配，已中止 USB 同步")
                    }

                    var paired =
                        _pairedHost.value ?: lwwStore.loadPairedHost()?.also { _pairedHost.value = it }
                    val verifiedHostId = pingInfo.hostId.takeIf { pingInfo.ok && it.isNotBlank() }
                    if (paired != null &&
                        paired.hostId.isNotBlank() &&
                        verifiedHostId != null &&
                        !paired.hostId.equals(verifiedHostId, ignoreCase = true)
                    ) {
                        throw IOException("目标电脑身份 (${pingInfo.hostName}) 与已配对主机不匹配，已拒绝自动重配对")
                    }
                    var token = resolveTokenForHost(paired, cleanBase, verifiedHostId = verifiedHostId)

                    if (paired != null && token != null && paired.hostId.isBlank() && verifiedHostId != null) {
                        paired = paired.copy(hostId = verifiedHostId)
                        lwwStore.savePairedHost(paired)
                        _pairedHost.value = paired
                    }

                    suspend fun performUsbAutoPair(): Pair<PairedHostInfo, String> {
                        val nonce = usbNonce?.trim().orEmpty()
                        if (nonce.isEmpty()) {
                            throw IOException("缺少 USB 自动配对令牌")
                        }
                        val deviceId = lwwStore.getOrCreateStableDeviceId()
                        val deviceName = Build.MODEL?.takeIf { it.isNotBlank() } ?: "Android Phone"
                        val pairResp =
                            LanSyncV2Client.pairViaUsb(
                                baseUrl = cleanBase,
                                nonce = nonce,
                                deviceId = deviceId,
                                deviceName = deviceName,
                                cancelToken = cancelToken,
                            )
                        val preservedUrl =
                            paired?.baseUrl?.takeIf { it.isNotBlank() && !LanHttpClient.isLoopbackUrl(it) }
                                ?: cleanBase
                        val info =
                            PairedHostInfo(
                                baseUrl = preservedUrl,
                                hostName = pairResp.hostName,
                                deviceId = pairResp.deviceId,
                                token = pairResp.token,
                                hostId = pairResp.hostId,
                            )
                        lwwStore.savePairedHost(info)
                        _pairedHost.value = info
                        return info to pairResp.token
                    }

                    if (paired == null || token == null) {
                        if (paired == null &&
                            transport == SyncTransportMode.USB &&
                            allowUsbAutoPair &&
                            !usbNonce.isNullOrBlank() &&
                            LanHttpClient.isLoopbackUrl(cleanBase)
                        ) {
                            val (newPaired, newToken) = performUsbAutoPair()
                            paired = newPaired
                            token = newToken
                        } else {
                            _syncStatus.value = "该电脑尚未配对或身份未验证：请先在同步面板重新配对 6 位配对码"
                            return@withCoroutineCancelToken
                        }
                    }

                    activePaired = paired
                    activeToken = token
                    pushProgress(phase = "正在获取电脑端同步列表…", isRunning = true, force = true)

                    managedSyncStorage.invalidatePathCache()
                    val outbox =
                        try {
                            LanSyncV2Client.fetchOutbox(cleanBase, token, cancelToken)
                        } catch (authErr: LanSyncAuthException) {
                            // Security guard: never silently overwrite an existing paired host token on 401.
                            throw IOException("电脑端鉴权已失效（401），已拒绝静默重配；请手动解除配对后重新输入配对码", authErr)
                        }
                    val (uniqueOutboxTracks, clientDuplicates) =
                        LanSyncV2Client.deduplicateOutboxTracks(outbox.tracks)
                    val totalDeduplicated = outbox.duplicateTrackCount + clientDuplicates

                    val allSyncedEntities = syncDb.syncedTrackDao().getAll()
                    val existingSyncedByUid = allSyncedEntities.associateBy { it.trackUid.lowercase() }
                    val existingSyncedByRel =
                        allSyncedEntities.mapNotNull { entity ->
                            val normRel =
                                LanSyncV2Client.sanitizeRelativePathSegments(entity.relativePath)
                                    .joinToString("/")
                                    .lowercase()
                            normRel.takeIf { it.isNotEmpty() }?.let { it to entity }
                        }.toMap()

                    val songsSnapshot = musicRepository.library?.songs.orEmpty()
                    val songRelIndex = PlaylistM3u8Helper.buildSongRelPathIndex(songsSnapshot)
                    val cachedFpByUid =
                        syncDb.audioFingerprintDao().getAll().associateBy { it.trackUid }.toMutableMap()

                    data class PendingSyncDownload(val track: OutboxTrackDto, val forceOverwrite: Boolean)
                    val pendingDownloads = ArrayList<PendingSyncDownload>()
                    val confirmedTrackUids = HashSet<String>()
                    val overwrittenTrackMtimes = HashMap<String, Long>()
                    var skippedExisting = 0
                    val upToDateEntities = ArrayList<SyncedTrackEntity>()

                    for (track in uniqueOutboxTracks) {
                        cancelToken.throwIfCancelled()
                        val outboxJson = TonewiseCore.stringifyJson(track.sourceOutboxIds)
                        val normTargetRel =
                            LanSyncV2Client.sanitizeRelativePathSegments(track.targetRelPath)
                                .joinToString("/")
                                .lowercase()
                        val recorded =
                            existingSyncedByUid[track.trackUid.lowercase()]
                                ?: existingSyncedByRel[normTargetRel]

                        val recordedFile = recorded?.let { managedSyncStorage.inspectExistingManagedFile(it) }
                        val existingLocalSong =
                            if (recordedFile == null) {
                                PlaylistM3u8Helper.findMatchingSongByRelPath(track.targetRelPath, songRelIndex)
                            } else {
                                null
                            }
                        val managedRelFile =
                            if (recordedFile == null && existingLocalSong == null) {
                                managedSyncStorage.findExistingManagedFileByRelPath(track.targetRelPath)
                            } else {
                                null
                            }

                        val exists = recordedFile != null || existingLocalSong != null || managedRelFile != null
                        val localDocUri =
                            recordedFile?.documentUri
                                ?: existingLocalSong?.uri?.toString()
                                ?: managedRelFile?.documentUri
                        val localFileSize =
                            recordedFile?.fileSize
                                ?: existingLocalSong?.size
                                ?: managedRelFile?.fileSize
                                ?: 0L
                        val localMtimeMs =
                            recordedFile?.mtimeMs?.takeIf { it > 0L }
                                ?: recorded?.mtimeMs?.takeIf { recordedFile != null && recorded.fileSize == localFileSize }
                                ?: existingLocalSong?.modifiedMs
                                ?: managedRelFile?.mtimeMs
                                ?: 0L

                        var localHeaderHash =
                            if (recorded != null && recordedFile != null &&
                                (recorded.fileSize <= 0L || recorded.fileSize == localFileSize)
                            ) {
                                recorded.headerBlake3Hex
                            } else {
                                ""
                            }
                        var localAudioHash =
                            if (recorded != null && recordedFile != null &&
                                (recorded.fileSize <= 0L || recorded.fileSize == localFileSize)
                            ) {
                                recorded.audioFrameBlake3Hex
                            } else {
                                ""
                            }

                        if (localHeaderHash.isBlank() && existingLocalSong != null) {
                            val cachedFp =
                                cachedFpByUid[existingLocalSong.uid.toString()]?.takeIf {
                                    isFingerprintCacheFresh(it, localFileSize, localMtimeMs)
                                }
                            if (cachedFp != null) {
                                localHeaderHash = cachedFp.headerBlake3Hex
                                localAudioHash = cachedFp.audioFrameBlake3Hex
                            }
                        }

                        if (exists &&
                            localHeaderHash.isBlank() &&
                            track.headerBlake3.isNotBlank() &&
                            (track.fileSize <= 0L || localFileSize == track.fileSize) &&
                            !localDocUri.isNullOrBlank()
                        ) {
                            computeLocalAudioFingerprintSafe(localDocUri)?.let { (hdr, aud) ->
                                localHeaderHash = hdr
                                localAudioHash = aud
                            }
                        }

                        when (
                            LanSyncV2Client.evaluateLocalTrackFreshness(
                                remote = track,
                                exists = exists,
                                localFileSize = localFileSize,
                                localMtimeMs = localMtimeMs,
                                localHeaderBlake3 = localHeaderHash,
                                localAudioFrameBlake3 = localAudioHash,
                            )
                        ) {
                            LocalSyncState.UP_TO_DATE -> {
                                upToDateEntities.add(
                                    SyncedTrackEntity(
                                        trackUid = track.trackUid,
                                        relativePath = recorded?.relativePath ?: track.targetRelPath,
                                        documentUri = localDocUri,
                                        fileSize = if (localFileSize > 0L) localFileSize else track.fileSize,
                                        mtimeMs = track.mtimeMs.takeIf { it > 0L } ?: localMtimeMs,
                                        sourceOutboxIdsJson = outboxJson,
                                        syncedAtMs = recorded?.syncedAtMs?.takeIf { it > 0L } ?: System.currentTimeMillis(),
                                        headerBlake3Hex = track.headerBlake3.ifBlank { localHeaderHash },
                                        audioFrameBlake3Hex = track.audioFrameBlake3.ifBlank { localAudioHash },
                                    )
                                )
                                if (existingLocalSong != null) {
                                    cacheFingerprintAlias(existingLocalSong, track.trackUid)
                                }
                                confirmedTrackUids.add(track.trackUid)
                                skippedExisting++
                            }
                            LocalSyncState.OUTDATED -> {
                                pendingDownloads.add(PendingSyncDownload(track, forceOverwrite = true))
                            }
                            LocalSyncState.MISSING -> {
                                pendingDownloads.add(PendingSyncDownload(track, forceOverwrite = false))
                            }
                        }
                    }

                    if (upToDateEntities.isNotEmpty()) {
                        syncDb.syncedTrackDao().upsertAll(upToDateEntities)
                    }

                    val partsDir = File(context.cacheDir, "lan_sync_parts")
                    runCatching { LanHttpClient.pruneStalePartFiles(partsDir) }
                    val totalDownloadBytes = pendingDownloads.sumOf { it.track.fileSize.coerceAtLeast(0L) }
                    var completedBytesBeforeCurrent = 0L
                    var downloadedCount = 0
                    var updatedExistingCount = 0
                    val failedTrackUids = ConcurrentHashMap.newKeySet<String>()
                    val firstErrorRef = AtomicReference<String?>(null)

                    if (pendingDownloads.isNotEmpty() || uniqueOutboxTracks.any { it.hasCover }) {
                        val sharedPhase =
                            AtomicReference(
                                if (transport == SyncTransportMode.USB) "准备 USB 同步…" else "正在准备下载…",
                            )
                        val sharedCompletedTracks = AtomicInteger(0)
                        val sharedCompletedBytes = AtomicLong(0L)
                        val sharedCurrentTitle = AtomicReference("")
                        val sharedAddedCount = AtomicInteger(0)
                        val sharedUpdatedCount = AtomicInteger(0)
                        val fastPushInFlightBytes = AtomicLong(0L)
                        val inFlightTrackBytes = ConcurrentHashMap<String, Long>()
                        val fastPushCoveredDirs = HashSet<String>()

                        fun computeAggregateBytes(): Long =
                            (sharedCompletedBytes.get() + fastPushInFlightBytes.get() + inFlightTrackBytes.values.sum())
                                .coerceAtLeast(0L)

                        fun emitLocalTransferProgress() {
                            _syncProgress.value =
                                SyncProgressState(
                                    isRunning = true,
                                    phase = sharedPhase.get(),
                                    completedTracks = sharedCompletedTracks.get(),
                                    totalTracks = pendingDownloads.size,
                                    downloadedBytes = computeAggregateBytes(),
                                    totalBytes = totalDownloadBytes,
                                    currentTrackTitle = sharedCurrentTitle.get(),
                                )
                        }

                        var remainingDownloads: List<PendingSyncDownload> = pendingDownloads

                        coroutineScope {
                            val tickerJob =
                                launch(Dispatchers.IO) {
                                    while (!cancelToken.isCancelled) {
                                        delay(600L)
                                        cancelToken.throwIfCancelled()
                                        pushProgress(
                                            phase = sharedPhase.get(),
                                            isRunning = true,
                                            completedTracks = sharedCompletedTracks.get(),
                                            totalTracks = pendingDownloads.size,
                                            downloadedBytes = computeAggregateBytes(),
                                            totalBytes = totalDownloadBytes,
                                            currentTrackTitle = sharedCurrentTitle.get(),
                                            addedCount = sharedAddedCount.get(),
                                            updatedCount = sharedUpdatedCount.get(),
                                            skippedCount = skippedExisting,
                                            force = true,
                                        )
                                    }
                                }

                            try {
                                if (pendingDownloads.isNotEmpty()) {
                                    emitLocalTransferProgress()
                                    pushProgress(
                                        phase = sharedPhase.get(),
                                        isRunning = true,
                                        completedTracks = 0,
                                        totalTracks = pendingDownloads.size,
                                        downloadedBytes = 0L,
                                        totalBytes = totalDownloadBytes,
                                        currentTrackTitle = pendingDownloads.first().track.title,
                                        addedCount = downloadedCount,
                                        updatedCount = updatedExistingCount,
                                        skippedCount = skippedExisting,
                                        force = true,
                                    )
                                }

                                // Pillar 1: USB native ADB sync: fast-push when writing to standard primary Music/
                                if (transport == SyncTransportMode.USB &&
                                    pendingDownloads.isNotEmpty() &&
                                    managedSyncStorage.isStandardPrimaryMusicStorage()
                                ) {
                                    cancelToken.throwIfCancelled()
                                    val fastPushTracks =
                                        pendingDownloads.map { pending ->
                                            val rel =
                                                LanSyncV2Client.sanitizeRelativePathSegments(pending.track.targetRelPath)
                                                    .joinToString("/")
                                                    .ifBlank { "Unsorted/${sanitizeKey(pending.track.trackUid)}.flac" }
                                            UsbFastPushTrackItem(
                                                trackUid = pending.track.trackUid,
                                                relPath = rel,
                                                fileSize = pending.track.fileSize,
                                                mtimeMs = pending.track.mtimeMs,
                                            )
                                        }
                                    val coverCandidates =
                                        uniqueOutboxTracks
                                            .asSequence()
                                            .filter { it.hasCover }
                                            .mapNotNull { t ->
                                                val segments = LanSyncV2Client.sanitizeRelativePathSegments(t.targetRelPath)
                                                val albumDir = segments.dropLast(1).joinToString("/")
                                                if (albumDir.isBlank()) null else (albumDir to t.trackUid)
                                            }
                                            .distinctBy { it.first.lowercase() }
                                            .map { (dir, uid) ->
                                                UsbFastPushCoverItem(albumDir = dir, sampleTrackUid = uid)
                                            }
                                            .toList()
                                    @Suppress("DEPRECATION")
                                    val remoteMusicRoot =
                                        Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_MUSIC)
                                            ?.absolutePath
                                            ?.takeIf { it.startsWith("/storage/") }
                                            ?: "/storage/emulated/0/Music"

                                    sharedPhase.set("USB 直传准备中…")
                                    emitLocalTransferProgress()

                                    val pushResult =
                                        LanSyncV2Client.requestUsbFastPush(
                                            baseUrl = cleanBase,
                                            token = token,
                                            tracks = fastPushTracks,
                                            covers = coverCandidates,
                                            remoteMusicRoot = remoteMusicRoot,
                                            cancelToken = cancelToken,
                                            onProgress = { ev ->
                                                val speedSuffix =
                                                    if (ev.speedMbps > 0.0) {
                                                        String.format(java.util.Locale.US, " (%.1f MB/s)", ev.speedMbps)
                                                    } else {
                                                        ""
                                                    }
                                                sharedPhase.set(
                                                    "USB 直传 (${ev.completedTracks}/${ev.totalTracks})$speedSuffix",
                                                )
                                                sharedCompletedTracks.set(ev.completedTracks)
                                                fastPushInFlightBytes.set(ev.bytesTransferred.coerceAtLeast(0L))
                                                sharedCurrentTitle.set(
                                                    ev.currentRelPath?.substringAfterLast('/') ?: "",
                                                )
                                                emitLocalTransferProgress()
                                            },
                                        )

                                    fastPushInFlightBytes.set(0L)
                                    if (pushResult.cancelledByHost) {
                                        cancelToken.cancel()
                                        throw CancellationException("Sync cancelled by PC")
                                    }

                                    if (pushResult.usedDirectPush && pushResult.pushedTracks.isNotEmpty()) {
                                        managedSyncStorage.invalidatePathCache()
                                        val pushedRelPaths = pushResult.pushedTracks.map { it.relPath }
                                        managedSyncStorage.triggerMediaScanForRelPaths(pushedRelPaths)

                                        val pendingByUid = pendingDownloads.associateBy { it.track.trackUid.lowercase() }
                                        val pushedEntities = ArrayList<SyncedTrackEntity>(pushResult.pushedTracks.size)
                                        val pushedUidsLower = HashSet<String>(pushResult.pushedTracks.size)
                                        val nowMs = System.currentTimeMillis()
                                        var pushedBytesTotal = 0L

                                        for (pushed in pushResult.pushedTracks) {
                                            val uidLower = pushed.trackUid.lowercase()
                                            val pending = pendingByUid[uidLower] ?: continue
                                            val track = pending.track
                                            pushedUidsLower.add(uidLower)
                                            confirmedTrackUids.add(track.trackUid)
                                            pushedBytesTotal += track.fileSize.coerceAtLeast(pushed.fileSize).coerceAtLeast(0L)
                                            if (pending.forceOverwrite) {
                                                updatedExistingCount++
                                                overwrittenTrackMtimes[uidLower] =
                                                    track.mtimeMs.takeIf { it > 0L } ?: nowMs
                                            } else {
                                                downloadedCount++
                                            }
                                            pushedEntities.add(
                                                SyncedTrackEntity(
                                                    trackUid = track.trackUid,
                                                    relativePath = pushed.relPath,
                                                    documentUri = null,
                                                    fileSize = pushed.fileSize.takeIf { it > 0L } ?: track.fileSize,
                                                    mtimeMs = track.mtimeMs,
                                                    sourceOutboxIdsJson = TonewiseCore.stringifyJson(track.sourceOutboxIds),
                                                    syncedAtMs = nowMs,
                                                    headerBlake3Hex = track.headerBlake3,
                                                    audioFrameBlake3Hex = track.audioFrameBlake3,
                                                )
                                            )
                                        }

                                        if (pushedEntities.isNotEmpty()) {
                                            syncDb.syncedTrackDao().upsertAll(pushedEntities)
                                        }

                                        completedBytesBeforeCurrent += pushedBytesTotal
                                        sharedCompletedBytes.set(completedBytesBeforeCurrent)
                                        sharedCompletedTracks.set(pushedUidsLower.size)
                                        sharedAddedCount.set(downloadedCount)
                                        sharedUpdatedCount.set(updatedExistingCount)
                                        emitLocalTransferProgress()

                                        if (pushResult.error == null &&
                                            coverCandidates.isNotEmpty() &&
                                            (pushResult.coversPushed + pushResult.coversSkipped) >= coverCandidates.size
                                        ) {
                                            for (c in coverCandidates) {
                                                fastPushCoveredDirs.add(c.albumDir.lowercase())
                                            }
                                        }

                                        remainingDownloads =
                                            pendingDownloads.filter {
                                                it.track.trackUid.lowercase() !in pushedUidsLower
                                            }
                                    } else if (pushResult.usedDirectPush &&
                                        pushResult.error == null &&
                                        coverCandidates.isNotEmpty() &&
                                        (pushResult.coversPushed + pushResult.coversSkipped) >= coverCandidates.size
                                    ) {
                                        for (c in coverCandidates) {
                                            fastPushCoveredDirs.add(c.albumDir.lowercase())
                                        }
                                    }
                                }

                                // Pillar 2: Bounded concurrent HTTP download + verify -> sequential SAF/MediaStore committer
                                if (remainingDownloads.isNotEmpty()) {
                                    data class DownloadedPendingItem(
                                        val pending: PendingSyncDownload,
                                        val partFile: File,
                                        val accountBytes: Long,
                                    )

                                    val httpConcurrency = if (transport == SyncTransportMode.USB) 2 else 3
                                    val downloadSem = Semaphore(httpConcurrency)
                                    val commitChannel = Channel<DownloadedPendingItem>(capacity = 2)
                                    val baseCompletedBeforeHttp = sharedCompletedTracks.get()

                                    val consumerJob =
                                        launch(Dispatchers.IO) {
                                            val upsertBatch = ArrayList<SyncedTrackEntity>(16)
                                            for (item in commitChannel) {
                                                val pending = item.pending
                                                val track = pending.track
                                                val uidLower = track.trackUid.lowercase()
                                                try {
                                                    cancelToken.throwIfCancelled()
                                                    val committed =
                                                        managedSyncStorage.commitAudioFile(
                                                            track = track,
                                                            partFile = item.partFile,
                                                            forceOverwrite = pending.forceOverwrite,
                                                        )
                                                    upsertBatch.add(
                                                        SyncedTrackEntity(
                                                            trackUid = track.trackUid,
                                                            relativePath = committed.relativePath,
                                                            documentUri = committed.documentUri,
                                                            fileSize = committed.fileSize,
                                                            mtimeMs = track.mtimeMs,
                                                            sourceOutboxIdsJson =
                                                                TonewiseCore.stringifyJson(track.sourceOutboxIds),
                                                            syncedAtMs = System.currentTimeMillis(),
                                                            headerBlake3Hex = track.headerBlake3,
                                                            audioFrameBlake3Hex = track.audioFrameBlake3,
                                                        )
                                                    )
                                                    if (upsertBatch.size >= 16) {
                                                        syncDb.syncedTrackDao().upsertAll(upsertBatch)
                                                        upsertBatch.clear()
                                                    }
                                                    confirmedTrackUids.add(track.trackUid)
                                                    val nowMs = System.currentTimeMillis()
                                                    if (pending.forceOverwrite) {
                                                        updatedExistingCount++
                                                        overwrittenTrackMtimes[uidLower] =
                                                            track.mtimeMs.takeIf { it > 0L } ?: nowMs
                                                    } else {
                                                        downloadedCount++
                                                    }
                                                    val nextCompleted = downloadedCount + updatedExistingCount
                                                    sharedAddedCount.set(downloadedCount)
                                                    sharedUpdatedCount.set(updatedExistingCount)
                                                    sharedCompletedTracks.set(nextCompleted.coerceAtMost(pendingDownloads.size))
                                                    completedBytesBeforeCurrent += item.accountBytes
                                                    sharedCompletedBytes.set(completedBytesBeforeCurrent)
                                                    inFlightTrackBytes.remove(uidLower)
                                                    emitLocalTransferProgress()
                                                } catch (ce: CancellationException) {
                                                    inFlightTrackBytes.remove(uidLower)
                                                    throw ce
                                                } catch (authErr: LanSyncAuthException) {
                                                    inFlightTrackBytes.remove(uidLower)
                                                    throw authErr
                                                } catch (commitErr: Exception) {
                                                    inFlightTrackBytes.remove(uidLower)
                                                    L.w(commitErr, "Track commit failed for ${track.title} (${track.trackUid})")
                                                    failedTrackUids.add(track.trackUid)
                                                    firstErrorRef.compareAndSet(null, commitErr.message)
                                                } finally {
                                                    item.partFile.delete()
                                                }
                                            }
                                            if (upsertBatch.isNotEmpty()) {
                                                syncDb.syncedTrackDao().upsertAll(upsertBatch)
                                                upsertBatch.clear()
                                            }
                                        }

                                    try {
                                        val producerCoordinator =
                                            launch {
                                                try {
                                                    coroutineScope {
                                                        for ((relIdx, pending) in remainingDownloads.withIndex()) {
                                                            launch(Dispatchers.IO) {
                                                                downloadSem.withPermit {
                                                                    cancelToken.throwIfCancelled()
                                                                    val track = pending.track
                                                                    val uidLower = track.trackUid.lowercase()
                                                                    val displayIdx = (baseCompletedBeforeHttp + relIdx + 1)
                                                                        .coerceAtMost(pendingDownloads.size)
                                                                    val phaseLabel =
                                                                        if (pending.forceOverwrite) {
                                                                            "正在更新曲目 ($displayIdx/${pendingDownloads.size})"
                                                                        } else {
                                                                            "正在下载曲目 ($displayIdx/${pendingDownloads.size})"
                                                                        }
                                                                    sharedPhase.set(phaseLabel)
                                                                    sharedCurrentTitle.set(track.title)
                                                                    emitLocalTransferProgress()

                                                                    val partFile = File(partsDir, "${sanitizeKey(track.trackUid)}.part")
                                                                    if (pending.forceOverwrite && partFile.exists()) {
                                                                        partFile.delete()
                                                                    }

                                                                    var sentToConsumer = false
                                                                    try {
                                                                        val fileUrl = LanSyncV2Client.buildTrackFileUrl(cleanBase, track.trackUid)
                                                                        var verified = false
                                                                        var lastVerifyError: String? = null
                                                                        for (attempt in 0 until 2) {
                                                                            cancelToken.throwIfCancelled()
                                                                            LanSyncV2Client.downloadFileResumable(
                                                                                url = fileUrl,
                                                                                token = token,
                                                                                partFile = partFile,
                                                                                expectedSize = track.fileSize,
                                                                                onProgress = { trackDownloaded, _ ->
                                                                                    inFlightTrackBytes[uidLower] =
                                                                                        trackDownloaded.coerceAtLeast(0L)
                                                                                    sharedCurrentTitle.set(track.title)
                                                                                    emitLocalTransferProgress()
                                                                                },
                                                                                cancelToken = cancelToken,
                                                                            )
                                                                            if (verifyDownloadedPartIntegrity(partFile, track)) {
                                                                                verified = true
                                                                                break
                                                                            }
                                                                            lastVerifyError = "下载文件校验失败（期望音频/头哈希不匹配）"
                                                                            partFile.delete()
                                                                            inFlightTrackBytes.remove(uidLower)
                                                                            if (attempt == 1) break
                                                                        }
                                                                        if (!verified) {
                                                                            throw IOException(lastVerifyError ?: "下载文件校验失败")
                                                                        }

                                                                        val accountBytes = track.fileSize.coerceAtLeast(partFile.length()).coerceAtLeast(0L)
                                                                        inFlightTrackBytes[uidLower] = accountBytes
                                                                        commitChannel.send(DownloadedPendingItem(pending, partFile, accountBytes))
                                                                        sentToConsumer = true
                                                                    } catch (ce: CancellationException) {
                                                                        inFlightTrackBytes.remove(uidLower)
                                                                        throw ce
                                                                    } catch (authErr: LanSyncAuthException) {
                                                                        inFlightTrackBytes.remove(uidLower)
                                                                        throw authErr
                                                                    } catch (dlErr: Exception) {
                                                                        inFlightTrackBytes.remove(uidLower)
                                                                        L.w(dlErr, "Track download paused/failed for ${track.title} (${track.trackUid})")
                                                                        failedTrackUids.add(track.trackUid)
                                                                        firstErrorRef.compareAndSet(null, dlErr.message)
                                                                    } finally {
                                                                        if (!sentToConsumer && pending.forceOverwrite) {
                                                                            partFile.delete()
                                                                        }
                                                                    }
                                                                }
                                                            }
                                                        }
                                                    }
                                                } finally {
                                                    commitChannel.close()
                                                }
                                            }

                                        producerCoordinator.join()
                                        consumerJob.join()
                                    } finally {
                                        while (true) {
                                            val leftover = commitChannel.tryReceive().getOrNull() ?: break
                                            leftover.partFile.delete()
                                        }
                                    }
                                }

                                // Parallel album cover fetches once per album directory (skip already fast-pushed albums)
                                val coverTasks = ArrayList<Pair<String, OutboxTrackDto>>()
                                val attemptedCoverDirs = HashSet<String>()
                                for (track in uniqueOutboxTracks) {
                                    cancelToken.throwIfCancelled()
                                    if (!track.hasCover || track.trackUid in failedTrackUids) continue
                                    val dirKey = track.targetRelPath.substringBeforeLast('/', "")
                                    if (!attemptedCoverDirs.add(dirKey)) continue
                                    if (dirKey.lowercase() in fastPushCoveredDirs) continue
                                    coverTasks.add(dirKey to track)
                                }

                                if (coverTasks.isNotEmpty()) {
                                    val coverSem = Semaphore(4)
                                    val coverCommitMutex = Mutex()
                                    coroutineScope {
                                        for ((dirKey, track) in coverTasks) {
                                            launch(Dispatchers.IO) {
                                                coverSem.withPermit {
                                                    cancelToken.throwIfCancelled()
                                                    try {
                                                        val coverUrl = LanSyncV2Client.buildTrackCoverUrl(cleanBase, track.trackUid)
                                                        val bytes = LanSyncV2Client.downloadCoverIfPresent(coverUrl, token, cancelToken)
                                                        if (bytes != null && bytes.isNotEmpty()) {
                                                            coverCommitMutex.withLock {
                                                                managedSyncStorage.commitAlbumCoverIfMissing(
                                                                    track.targetRelPath,
                                                                    bytes,
                                                                )
                                                            }
                                                        }
                                                    } catch (ce: CancellationException) {
                                                        throw ce
                                                    } catch (authErr: LanSyncAuthException) {
                                                        throw authErr
                                                    } catch (t: Throwable) {
                                                        L.w(t, "Album cover fetch skipped for $dirKey")
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            } finally {
                                tickerJob.cancel()
                                runCatching { tickerJob.join() }
                            }
                        }
                    }

                    val firstDownloadError = firstErrorRef.get()

                    _syncProgress.value =
                        _syncProgress.value.copy(phase = "正在双向同步播放记录、评分与歌单…")
                    pushProgress(
                        phase = "正在双向同步播放记录、评分与歌单…",
                        isRunning = true,
                        completedTracks = pendingDownloads.size,
                        totalTracks = pendingDownloads.size,
                        downloadedBytes = completedBytesBeforeCurrent,
                        totalBytes = totalDownloadBytes,
                        addedCount = downloadedCount,
                        updatedCount = updatedExistingCount,
                        skippedCount = skippedExisting,
                        force = true,
                    )

                    // Best-effort v1 metadata assets (scenes / waveforms / encyclopedia)
                    try {
                        cancelToken.throwIfCancelled()
                        @Suppress("UNCHECKED_CAST")
                        val statusObj =
                            TonewiseCore.parseJson(httpGetString("$cleanBase/api/v1/status", token, cancelToken))
                                as? Map<String, Any?>
                        val scenesRaw = httpGetString("$cleanBase/api/v1/scenes", token, cancelToken)
                        cacheFile("scenes_5d.json", scenesRaw.toByteArray(Charsets.UTF_8))
                        _scenes.value = lwwStore.applySceneOrder(SceneRuleEvaluator.parseScenesJson(scenesRaw))

                        if (statusObj?.get("has_waveforms") == true) {
                            val remoteSha = (statusObj["waveforms_sha256"] as? String)?.trim().orEmpty()
                            val remoteBytes = (statusObj["waveforms_bytes"] as? Number)?.toLong() ?: 0L
                            val localWf = File(context.filesDir, ".tonewise/waveforms.bin")
                            val localSha = syncDb.syncKvDao().getValue("waveforms_sha256").orEmpty()
                            val needsWaveformDownload =
                                !localWf.isFile ||
                                    (remoteSha.isNotEmpty() && !localSha.equals(remoteSha, ignoreCase = true)) ||
                                    (remoteSha.isEmpty() && remoteBytes > 0L && localWf.length() != remoteBytes)
                            if (needsWaveformDownload) {
                                LanHttpClient.downloadFileToTempAndReplace(
                                    url = "$cleanBase/api/v1/waveforms",
                                    bearerToken = token,
                                    targetFile = localWf,
                                    cancelToken = cancelToken,
                                )
                                if (remoteSha.isNotEmpty()) {
                                    syncDb.syncKvDao().putValue(SyncKvEntity("waveforms_sha256", remoteSha))
                                }
                                waveformRepository.invalidateArchiveCache()
                            }
                        }
                        if (statusObj?.get("has_encyclopedia") == true) {
                            val encJson = httpGetString("$cleanBase/api/v1/encyclopedia", token, cancelToken)
                            musicBioRepository.importSyncedEncyclopediaJson(encJson)
                        }
                        try {
                            val mergedAutoEqJson =
                                httpPostJson(
                                    "$cleanBase/api/v1/auto-eq",
                                    dspRepository.exportAutoEqPolicyJson(),
                                    token,
                                    cancelToken,
                                )
                            dspRepository.mergeRemoteAutoEqPolicyJson(mergedAutoEqJson)
                        } catch (ce: CancellationException) {
                            throw ce
                        } catch (authErr: LanSyncAuthException) {
                            throw authErr
                        } catch (_: Throwable) {
                        }
                    } catch (ce: CancellationException) {
                        throw ce
                    } catch (authErr: LanSyncAuthException) {
                        throw authErr
                    } catch (t: Throwable) {
                        L.w(t, "Optional v1 metadata sync partial skip")
                    }

                    // Unified S0-fixed /api/v2/report push & pull
                    val completedCopyOutboxIds =
                        outbox.items
                            .filter { item ->
                                if (!item.mode.equals("copy", ignoreCase = true)) return@filter false
                                val memberTracks =
                                    uniqueOutboxTracks.filter { item.id in it.sourceOutboxIds }
                                memberTracks.all {
                                    it.trackUid in confirmedTrackUids && it.trackUid !in failedTrackUids
                                }
                            }
                            .map { it.id }

                    cancelToken.throwIfCancelled()
                    val reportSync =
                        pushV2ReportAndApplyCounts(
                            cleanBase = cleanBase,
                            token = token,
                            deviceId = paired.deviceId,
                            completedOutboxIds = completedCopyOutboxIds,
                            cancelToken = cancelToken,
                        )
                    val pushedCounts = reportSync.pushedCounts
                    val pushedMutations = reportSync.pushedMutations

                    if (overwrittenTrackMtimes.isNotEmpty()) {
                        val currentSongs = musicRepository.library?.songs.orEmpty()
                        val currentByUid = currentSongs.associateBy { it.uid.toString() }
                        val currentByPcUid = buildSongByPcUidMap(currentSongs, currentByUid)
                        val unmatchedEvicts =
                            evictStaleOverridesForTracks(overwrittenTrackMtimes, currentByPcUid, currentByUid)
                        if (unmatchedEvicts.isNotEmpty()) {
                            synchronized(pendingRemoteLedgerLock) {
                                pendingEvictTrackMtimes.putAll(unmatchedEvicts)
                                pendingRemoteReconcileRemaining = maxOf(pendingRemoteReconcileRemaining, 3)
                            }
                        }
                    }

                    if (!reportSync.handoffMerged) {
                        try {
                            val mergedSlotsJson =
                                httpPostJson("$cleanBase/api/v1/handoff", sessionSlotStore.exportSlotsJson(), token, cancelToken)
                            sessionSlotStore.mergeRemoteJson(mergedSlotsJson)
                        } catch (ce: CancellationException) {
                            throw ce
                        } catch (authErr: LanSyncAuthException) {
                            throw authErr
                        } catch (_: Throwable) {
                        }
                    }

                    if (!lastFmRepository.state.value.hasValidApiSession) {
                        when (val lfmRes = fetchPairedLastFmSession()) {
                            is PairedLastFmSessionResult.Available -> {
                                lastFmRepository.importPairedDesktopSession(
                                    username = lfmRes.username,
                                    apiKey = lfmRes.apiKey,
                                    sharedSecret = lfmRes.sharedSecret,
                                    sessionKey = lfmRes.sessionKey,
                                )
                            }
                            is PairedLastFmSessionResult.Error -> Unit
                        }
                    }

                    // Playlists sync strictly one-way PC -> Phone via v2 outbox; phone playlists are never synced back to PC.
                    val effectiveOutboxPlaylists = ArrayList<OutboxPlaylistDto>(outbox.playlists.size)
                    for (pl in outbox.playlists) {
                        managedSyncStorage.commitPlaylistM3u8(pl.name, pl.relPaths)
                        effectiveOutboxPlaylists.add(pl)
                    }
                    if (effectiveOutboxPlaylists.isNotEmpty()) {
                        for (pl in effectiveOutboxPlaylists) {
                            pendingOutboxPlaylists[pl.name.lowercase()] =
                                PendingPlaylistReconcile(pl, remainingAttempts = 3)
                        }
                    }

                    // Clean up previously managed playlists that were removed from the PC sync list
                    var removedPlaylistsCount = 0
                    val syncPolicy = outbox.syncPolicy
                    if (syncPolicy?.outboxAuthoritative == true) {
                        val expectedNames =
                            syncPolicy.expectedPlaylistNames
                                .ifEmpty { outbox.playlists.map { it.name } }
                                .map { it.trim() }
                                .filter { it.isNotEmpty() }
                                .toSet()
                        val expectedLower = expectedNames.mapTo(HashSet()) { it.lowercase() }
                        val prevManaged = lwwStore.loadManagedPlaylistNames()
                        val libSnapshot = musicRepository.library
                        for (prevName in prevManaged) {
                            if (prevName.trim().lowercase() !in expectedLower) {
                                managedSyncStorage.deletePlaylistM3u8(prevName)
                                val existingPl =
                                    libSnapshot?.findPlaylistByName(prevName)
                                        ?: libSnapshot?.playlists?.firstOrNull {
                                            it.name.raw.equals(prevName, ignoreCase = true)
                                        }
                                if (existingPl != null) {
                                    runCatching { musicRepository.deletePlaylist(existingPl) }
                                }
                                removedPlaylistsCount++
                            }
                        }
                        lwwStore.saveManagedPlaylistNames(expectedNames)
                    }

                    // Mirror prune phase: execute only after downloads and bidirectional report succeed
                    var deletedCount = 0
                    var blockedForeignPaths = emptyList<String>()
                    var skippedPlayingPaths = emptyList<String>()
                    var pendingLargePruneCount = 0
                    var localAudioCount = 0
                    var pendingLargePruneRatio = 0.0
                    var pruneStatusNote: String? = null

                    if (syncPolicy != null && syncPolicy.mirrorPruneEnabled) {
                        _syncProgress.value =
                            _syncProgress.value.copy(phase = "正在扫描 Music 目录并清理不在列表中的歌曲…")
                        pushProgress(
                            phase = "正在扫描 Music 目录并清理不在列表中的歌曲…",
                            isRunning = true,
                            completedTracks = pendingDownloads.size,
                            totalTracks = pendingDownloads.size,
                            downloadedBytes = completedBytesBeforeCurrent,
                            totalBytes = totalDownloadBytes,
                            addedCount = downloadedCount,
                            updatedCount = updatedExistingCount,
                            skippedCount = skippedExisting,
                            force = true,
                        )

                        val localMusicFiles = managedSyncStorage.listAudioFilesInMusicRoot()
                        val desiredRelPaths = uniqueOutboxTracks.map { it.targetRelPath }
                        val managedRelPaths = syncDb.syncedTrackDao().getAll().map { it.relativePath }.toSet()
                        val syncCompletedCleanly = failedTrackUids.isEmpty() && !cancelToken.isCancelled
                        val effectiveConfirmed =
                            resolveEffectivePruneConfirmed(
                                transport = transport,
                                baseUrl = cleanBase,
                                localConfirmed = localLargePruneConfirmedOnce,
                                remoteConfirmed = syncPolicy.pruneConfirmedForSession,
                            )
                        val effectivePolicy =
                            syncPolicy.copy(pruneConfirmedForSession = effectiveConfirmed)
                        val prunePlan =
                            SyncMirrorPrunePlanner.planPrune(
                                policy = effectivePolicy,
                                desiredTargetRelPaths = desiredRelPaths,
                                localMusicFiles = localMusicFiles,
                                managedRelPaths = managedRelPaths,
                                syncCompletedCleanly = syncCompletedCleanly,
                            )

                        localAudioCount = prunePlan.localAudioCount
                        when (prunePlan.decision) {
                            PruneDecision.PROCEED -> {
                                localLargePruneConfirmedOnce = false
                                _pendingLargePrune.value = null
                                val playingUris = HashSet<String>()
                                val playingNormRels = HashSet<String>()
                                playbackStateManager.currentSong?.let { cur ->
                                    playingUris.add(cur.uri.toString())
                                    val curRel =
                                        cur.path.components.components
                                            .joinToString("/")
                                            .replace('\\', '/')
                                    val normCurRel = SyncMirrorPrunePlanner.normalizeRelPath(curRel)
                                    if (normCurRel.isNotEmpty()) {
                                        playingNormRels.add(normCurRel)
                                    }
                                }
                                val pruneResult =
                                    managedSyncStorage.deleteAudioFilesAndPruneEmptyDirs(
                                        filesToDelete = prunePlan.filesToDelete,
                                        currentlyPlayingUris = playingUris,
                                        currentlyPlayingNormRelPaths = playingNormRels,
                                    )
                                deletedCount = pruneResult.deletedFiles.size
                                blockedForeignPaths = pruneResult.blockedForeignRelPaths
                                skippedPlayingPaths = pruneResult.skippedPlayingRelPaths

                                // Only remove deleted entries from synced_tracks; preserve track_play_ledger & lww_overrides!
                                if (pruneResult.deletedFiles.isNotEmpty()) {
                                    val deletedNormRels =
                                        pruneResult.deletedFiles.mapTo(HashSet()) {
                                            SyncMirrorPrunePlanner.normalizeRelPath(it.relativePath)
                                        }
                                    val deletedUris =
                                        pruneResult.deletedFiles.mapTo(HashSet()) { it.documentUri }
                                    for (entity in syncDb.syncedTrackDao().getAll()) {
                                        val entNormRel = SyncMirrorPrunePlanner.normalizeRelPath(entity.relativePath)
                                        if (entNormRel in deletedNormRels ||
                                            (entity.documentUri != null && entity.documentUri in deletedUris)
                                        ) {
                                            syncDb.syncedTrackDao().deleteByUid(entity.trackUid)
                                        }
                                    }
                                }
                            }
                            PruneDecision.REQUIRE_LARGE_PRUNE_CONFIRMATION -> {
                                pendingLargePruneCount = prunePlan.filesToDelete.size
                                pendingLargePruneRatio = prunePlan.pruneRatio
                                _pendingLargePrune.value =
                                    PendingLargePruneState(
                                        count = pendingLargePruneCount,
                                        localAudioCount = localAudioCount,
                                        ratio = pendingLargePruneRatio,
                                        baseUrl = cleanBase,
                                        transport = transport,
                                    )
                                pruneStatusNote =
                                    "拟清理 ${pendingLargePruneCount}/${localAudioCount} 首（超阈值，请在电脑端或手机同步面板确认后执行）"
                            }
                            PruneDecision.SKIP_EMPTY_OR_UNAUTHORITATIVE -> {
                                pruneStatusNote = "同步列表为空或源目录异常，已熔断跳过清理"
                            }
                            PruneDecision.SKIP_INCOMPLETE_SYNC -> {
                                pruneStatusNote = "存在未完成下载曲目，已保护本地曲库跳过清理"
                            }
                            PruneDecision.SKIP_DISABLED -> Unit
                        }
                    }

                    val hasPendingRemoteLedger =
                        synchronized(pendingRemoteLedgerLock) {
                            pendingRemoteCounts.isNotEmpty() ||
                                pendingRemoteLoved.isNotEmpty() ||
                                pendingRemoteUnloved.isNotEmpty() ||
                                pendingEvictTrackMtimes.isNotEmpty()
                        }
                    if (downloadedCount > 0 ||
                        updatedExistingCount > 0 ||
                        deletedCount > 0 ||
                        effectiveOutboxPlaylists.isNotEmpty()
                    ) {
                        musicRepository.requestIndex(withCache = true)
                        reconcilePendingOutboxPlaylists()
                        reconcilePendingRemoteLedger()
                    } else if (hasPendingRemoteLedger) {
                        reconcilePendingRemoteLedger()
                    }

                    val finalStatus =
                        buildString {
                            if (failedTrackUids.isEmpty()) {
                                append(transportLabel).append("同步完成（").append(outbox.hostName).append("）：")
                            } else {
                                append(transportLabel).append("部分完成（").append(failedTrackUids.size).append(" 首待续传）：")
                            }
                            append("新增 ").append(downloadedCount).append(" 首")
                            if (updatedExistingCount > 0) {
                                append(" · 更新新版 ").append(updatedExistingCount).append(" 首")
                            }
                            if (skippedExisting > 0) {
                                append(" · 已最新跳过 ").append(skippedExisting).append(" 首")
                            }
                            if (deletedCount > 0) {
                                append(" · 清理 ").append(deletedCount).append(" 首")
                            }
                            if (totalDeduplicated > 0) {
                                append(" · 任务去重 ").append(totalDeduplicated).append(" 首")
                            }
                            if (outbox.playlists.isNotEmpty() || removedPlaylistsCount > 0) {
                                append(" · 歌单 ").append(outbox.playlists.size).append(" 个")
                            }
                            append(" · 上报播放 ").append(pushedCounts).append(" 首 · 标签 ")
                                .append(pushedMutations).append(" 条")
                            if (blockedForeignPaths.isNotEmpty()) {
                                append(" · 待授权旧曲 ").append(blockedForeignPaths.size).append(" 首")
                            }
                            if (skippedPlayingPaths.isNotEmpty()) {
                                append(" · 跳过正在播放 ").append(skippedPlayingPaths.size).append(" 首")
                            }
                            if (pruneStatusNote != null) {
                                append(" · ").append(pruneStatusNote)
                            }
                            if (firstDownloadError != null) {
                                append("（").append(firstDownloadError).append("）")
                            }
                        }
                    _syncStatus.value = finalStatus
                    pushProgress(
                        phase = finalStatus,
                        isRunning = false,
                        completedTracks = pendingDownloads.size,
                        totalTracks = pendingDownloads.size,
                        downloadedBytes = completedBytesBeforeCurrent,
                        totalBytes = totalDownloadBytes,
                        addedCount = downloadedCount,
                        updatedCount = updatedExistingCount,
                        skippedCount = skippedExisting,
                        deletedCount = deletedCount,
                        blockedForeignPaths = blockedForeignPaths,
                        pendingLargePruneCount = pendingLargePruneCount,
                        localAudioCount = localAudioCount,
                        pendingLargePruneRatio = pendingLargePruneRatio,
                        completed = true,
                        force = true,
                    )
                } catch (ce: CancellationException) {
                    L.i("$transportLabel sync cancelled")
                    val msg = "同步已取消（已下载曲目与断点缓存已保留，可随时续传）"
                    _syncStatus.value = msg
                    pushProgress(phase = msg, isRunning = false, errorMessage = msg, completed = false, force = true)
                    throw ce
                } catch (authErr: LanSyncAuthException) {
                    L.w(authErr, "$transportLabel Sync v2 unauthorized")
                    unpairHost(notifyRemote = false)
                    val msg = "配对已失效，请重新输入电脑端 6 位配对码配对"
                    _syncStatus.value = msg
                    pushProgress(phase = msg, isRunning = false, errorMessage = msg, completed = false, force = true)
                } catch (e: Exception) {
                    L.w(e, "$transportLabel sync failed")
                    val msg = formatConnectionError(cleanBase, e, vpnActive = isVpnActive())
                    _syncStatus.value = msg
                    pushProgress(phase = msg, isRunning = false, errorMessage = msg, completed = false, force = true)
                } finally {
                    managedSyncStorage.invalidatePathCache()
                    activeCancelToken = null
                    activeSyncJob = null
                    _syncProgress.value = SyncProgressState()
                    syncInFlight.set(false)
                }
            }
        }
    }

    suspend fun fetchAndApplyHeaderPatch(
        baseUrl: String,
        song: Song,
        expectedOldHeaderHex: String,
    ): Boolean =
        withContext(Dispatchers.IO) {
            val cleanBase = normalizeBaseUrl(baseUrl)
            val token =
                resolveTokenForBaseUrl(cleanBase)
                    ?: throw LanSyncAuthException(401, "未配对或目标主机与已配对电脑不匹配")
            val trackUid = song.uid.toString()
            val encodedUid = URLEncoder.encode(trackUid, "UTF-8")
            val raw = httpGetString("$cleanBase/api/v1/patch/$encodedUid", token)
            @Suppress("UNCHECKED_CAST")
            val obj = TonewiseCore.parseJson(raw) as? Map<String, Any?> ?: return@withContext false
            val b64 = obj["header_base64"]?.toString().orEmpty()
            if (b64.isBlank()) return@withContext false
            val headerBytes = Base64.decode(b64, Base64.DEFAULT)
            context.contentResolver.openFileDescriptor(song.uri, "rw")?.use { pfd ->
                val resultJson = TonewiseCore.applyHeaderPatch(pfd, expectedOldHeaderHex, headerBytes)
                isPatchApplied(resultJson)
            } ?: false
        }

    suspend fun fetchAndApplyHeaderPatchToFile(
        baseUrl: String,
        trackUid: String,
        targetFile: File,
        expectedOldHeaderHex: String,
    ): Boolean =
        withContext(Dispatchers.IO) {
            val cleanBase = normalizeBaseUrl(baseUrl)
            val token =
                resolveTokenForBaseUrl(cleanBase)
                    ?: throw LanSyncAuthException(401, "未配对或目标主机与已配对电脑不匹配")
            val encodedUid = URLEncoder.encode(trackUid, "UTF-8")
            val raw = httpGetString("$cleanBase/api/v1/patch/$encodedUid", token)
            @Suppress("UNCHECKED_CAST")
            val obj = TonewiseCore.parseJson(raw) as? Map<String, Any?> ?: return@withContext false
            val b64 = obj["header_base64"]?.toString().orEmpty()
            if (b64.isBlank()) return@withContext false
            val headerBytes = Base64.decode(b64, Base64.DEFAULT)
            val resultJson =
                TonewiseCore.applyHeaderPatchPath(
                    targetFile.absolutePath,
                    expectedOldHeaderHex,
                    headerBytes,
                )
            isPatchApplied(resultJson)
        }

    suspend fun fetchPairedLastFmSession(): PairedLastFmSessionResult =
        withContext(Dispatchers.IO) {
            val paired =
                _pairedHost.value ?: lwwStore.loadPairedHost()?.also { _pairedHost.value = it }
                    ?: return@withContext PairedLastFmSessionResult.Error("请先配对 PC 桌面端")
            val cleanBase = normalizeBaseUrl(paired.baseUrl)
            val token =
                resolveTokenForHost(paired, cleanBase)
                    ?: return@withContext PairedLastFmSessionResult.Error("已配对主机凭据不可用")
            runCatching {
                val raw = httpGetString("$cleanBase/api/v1/lastfm/session", token)
                @Suppress("UNCHECKED_CAST")
                val obj =
                    TonewiseCore.parseJson(raw) as? Map<String, Any?>
                        ?: return@runCatching PairedLastFmSessionResult.Error("无效响应")
                val configured = obj["configured"] as? Boolean ?: false
                if (!configured) {
                    return@runCatching PairedLastFmSessionResult.Error("PC 端尚未授权 Last.fm")
                }
                PairedLastFmSessionResult.Available(
                    username = obj["username"]?.toString().orEmpty(),
                    apiKey = obj["api_key"]?.toString().orEmpty(),
                    sharedSecret = obj["shared_secret"]?.toString().orEmpty(),
                    sessionKey = obj["session_key"]?.toString().orEmpty(),
                )
            }.getOrElse { err ->
                PairedLastFmSessionResult.Error(err.message ?: "网络异常")
            }
        }

    sealed interface PairedLastFmSessionResult {
        data class Available(
            val username: String,
            val apiKey: String,
            val sharedSecret: String,
            val sessionKey: String,
        ) : PairedLastFmSessionResult

        data class Error(val message: String) : PairedLastFmSessionResult
    }

    private fun resolveTokenForBaseUrl(cleanBase: String): String? {
        val paired =
            _pairedHost.value
                ?: run {
                    val mainLooper = runCatching { android.os.Looper.getMainLooper() }.getOrNull()
                    if (mainLooper != null && mainLooper.thread === Thread.currentThread()) null
                    else lwwStore.loadPairedHost()?.also { _pairedHost.value = it }
                }
        return resolveTokenForHost(paired, cleanBase)
    }

    private fun isPatchApplied(resultJson: String): Boolean =
        runCatching {
            @Suppress("UNCHECKED_CAST")
            val obj = TonewiseCore.parseJson(resultJson) as? Map<String, Any?>
            obj?.get("status")?.toString() == "applied"
        }.getOrDefault(false)

    private fun verifyDownloadedPartIntegrity(partFile: File, track: OutboxTrackDto): Boolean {
        val expectedAudio = track.audioFrameBlake3.trim()
        val expectedHeader = track.headerBlake3.trim()
        if (expectedAudio.isEmpty() && expectedHeader.isEmpty()) {
            return true
        }
        val fp = computeLocalAudioFingerprintSafe(partFile.absolutePath) ?: return false
        val (actualHeader, actualAudio) = fp
        if (expectedAudio.isNotEmpty() && !expectedAudio.equals(actualAudio, ignoreCase = true)) {
            return false
        }
        if (expectedHeader.isNotEmpty() && !expectedHeader.equals(actualHeader, ignoreCase = true)) {
            return false
        }
        return true
    }

    fun getEffectiveDjScenes(song: Song): List<String> {
        val rawOverride = lwwStore.getLwwOverride(song.uid.toString(), "dj_scene")
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
        lwwStore.getLwwOverride(song.uid.toString(), "dj_vocals") ?: (song.djVocals ?: "unknown")

    fun getEffectiveDjEnergy(song: Song): String =
        lwwStore.getLwwOverride(song.uid.toString(), "dj_energy") ?: (song.djEnergy ?: "unknown")

    fun getEffectiveDjTempo(song: Song): String? =
        lwwStore.getLwwOverride(song.uid.toString(), "tempo")?.takeIf { it.isNotBlank() }
            ?: lwwStore.getLwwOverride(song.uid.toString(), "tempo_bucket")?.takeIf { it.isNotBlank() }
            ?: song.tempoBucket

    fun getEffectiveMoods(song: Song): List<String> {
        val rawOverride = lwwStore.getLwwOverride(song.uid.toString(), "mood")
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
        val provisionalDeviceId = lwwStore.getCachedStableDeviceId()

        if (cleanField == "rating") {
            val ratingNum = cleanValue.toIntOrNull() ?: 4
            val isNowLoved = ratingNum >= 5
            playCountRepository.setLovedByUid(uid, isNowLoved)
            lastFmRepository.onLovedChanged(song, isNowLoved)
        }

        lwwStore.recordPendingMutation(
            uid = uid,
            field = cleanField,
            value = cleanValue,
            updatedAtMs = now,
            deviceId = provisionalDeviceId,
        )

        val overrideEntity = LwwOverrideEntity(uid, cleanField, cleanValue, now)
        writeBackScope.launch {
            val stableDeviceId = runCatching { lwwStore.getOrCreateStableDeviceId() }.getOrDefault(provisionalDeviceId)
            val mutationEntity = PendingTagMutationEntity(uid, cleanField, cleanValue, now, stableDeviceId)
            lwwStore.persistMutationEntities(overrideEntity, mutationEntity)
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

    fun orderSongsWorkShuffle(
        songs: List<Song>,
        random: kotlin.random.Random = kotlin.random.Random.Default,
    ): List<Song> {
        if (songs.size <= 1) return songs
        val order = WorkShuffleOrder.createWorkAwareOrder(songs, -1, random)
        return order.map { songs[it] }
    }

    suspend fun ensurePresetRelaySlotsSeeded() =
        withContext(Dispatchers.Default) {
            val songs = musicRepository.library?.songs
            if (songs.isNullOrEmpty()) return@withContext
            if (!presetSlotsSeeding.compareAndSet(false, true)) return@withContext
            try {
                sessionSlotStore.ensureLoaded()
                val presetScenes =
                    _scenes.value
                        .filterNot { it.id == "44444444-4444-4444-8444-444444444444" }
                        .take(5)
                if (presetScenes.isEmpty()) return@withContext
                val currentSlots = sessionSlotStore.slots.value
                for (idx in presetScenes.indices) {
                    val slotIdx = idx + 1
                    val existing = currentSlots.firstOrNull { it.slotIndex == slotIdx }
                    if (SessionSlotStore.isSlotPopulated(existing)) continue
                    val presetScene =
                        existing?.sceneId?.let { sid ->
                            presetScenes.firstOrNull {
                                it.id.equals(sid, ignoreCase = true) ||
                                    it.title.startsWith(sid, ignoreCase = true)
                            }
                        } ?: presetScenes[idx]
                    val (_, ordered) =
                        generateAutoDjPlaylist(
                            scene = presetScene,
                            targetCount = AutoDjEngine.DEFAULT_TARGET_COUNT,
                        )
                    if (ordered.isEmpty()) continue
                    val canonicalUids = resolveCanonicalTrackUids(ordered)
                    sessionSlotStore.saveSlot(
                        SessionSlotItem(
                            slotIndex = slotIdx,
                            name = presetScene.title,
                            sceneId = presetScene.id,
                            updatedAtMs = 0L,
                            queueTrackUids = canonicalUids,
                            currentIndex = 0,
                            positionMs = 0L,
                        ),
                        activate = false,
                    )
                }
            } finally {
                presetSlotsSeeding.set(false)
            }
        }

    fun generateAutoDjPlaylist(
        scene: SceneBanner? = null,
        targetCount: Int = AutoDjEngine.DEFAULT_TARGET_COUNT,
        hourOfDay: Int = java.util.Calendar.getInstance().get(java.util.Calendar.HOUR_OF_DAY),
    ): Pair<AutoDjResult, List<Song>> {
        val songs = musicRepository.library?.songs?.toList().orEmpty()
        if (songs.isEmpty()) {
            val emptyRes = AutoDjEngine.generate(emptyList(), hourOfDay = hourOfDay, targetCount = targetCount)
            return emptyRes to emptyList()
        }
        val matchedUids =
            if (scene != null) {
                matchSongsForScene(scene).map { it.uid.toString() }.toSet()
            } else {
                emptySet()
            }
        val sceneCtx =
            scene?.let {
                AutoDjSceneContext(
                    sceneId = it.id,
                    sceneTitle = it.title,
                    matchedUids = matchedUids,
                    sortArc = SceneRuleEvaluator.extractSortArc(it.rawSceneJson),
                )
            }
        val candidates =
            songs.map { song ->
                val uid = song.uid.toString()
                val snap = playCountRepository.getSnapshot(song)
                val rating =
                    lwwStore.getLwwOverride(uid, "rating")?.toIntOrNull()
                        ?: if (snap.loved) 5 else 0
                val artistStr =
                    song.artists
                        .mapNotNull { (it.name as? Name.Known)?.raw }
                        .filter { it.isNotBlank() }
                        .joinToString("; ")
                val workKey = com.shawnshi.tonewise.music.ClassicalWorkKey.workBlockKey(song)
                AutoDjTrackCandidate(
                    trackUid = uid,
                    title = song.name.raw,
                    artist = artistStr,
                    effectivePlayCount = snap.playCount,
                    rating = rating,
                    tempoBpm = song.tempoBpm?.toDouble(),
                    tempoBucket = getEffectiveDjTempo(song),
                    djEnergy = getEffectiveDjEnergy(song),
                    djVocals = getEffectiveDjVocals(song),
                    djScenes = getEffectiveDjScenes(song),
                    moods = getEffectiveMoods(song),
                    lastPlayedTsMs = snap.lastPlayedMs,
                    workKey = workKey,
                    movementNumber = song.movementNumber?.takeIf { it > 0 },
                    discNumber = song.disc?.number ?: 0,
                    trackNumber = song.track ?: 0,
                    relPath = song.path.components.components.joinToString("/"),
                )
            }
        val result =
            AutoDjEngine.generate(
                candidates = candidates,
                hourOfDay = hourOfDay,
                targetCount = targetCount,
                sceneContext = sceneCtx,
            )
        val byUid = songs.associateBy { it.uid.toString() }
        val ordered = result.orderedTrackUids.mapNotNull { byUid[it] }
        return result to ordered
    }

    private fun getOrBuildTracksJson(songs: Collection<Song>): String {
        val key =
            TracksCacheKey(
                songsIdentityHash = System.identityHashCode(songs),
                songCount = songs.size,
                lwwVersion = lwwStore.lwwVersion,
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
                    lwwVersion = lwwStore.lwwVersion,
                    playCountVersion = playCountRepository.updates.value,
                )
            if (cachedTracksKey == recheckKey) {
                return cachedTracksJson
            }
            val nowMs = System.currentTimeMillis()
            val built =
                SceneRuleEvaluator.buildTracksJson(
                    songs = songs,
                    nowMs = nowMs,
                    getDjScenes = ::getEffectiveDjScenes,
                    getDjVocals = ::getEffectiveDjVocals,
                    getDjEnergy = ::getEffectiveDjEnergy,
                    getDjTempo = ::getEffectiveDjTempo,
                    getMoods = ::getEffectiveMoods,
                    getPlayStats = { song ->
                        val snap = playCountRepository.getSnapshot(song)
                        Triple(snap.playCount, snap.lastPlayedMs, snap.loved)
                    },
                    getRatingOverride = { uid -> lwwStore.getLwwOverride(uid, "rating") },
                )
            cachedTracksKey = recheckKey
            cachedTracksJson = built
            built
        }
    }

    private data class ReportSyncResult(
        val pushedCounts: Int,
        val pushedMutations: Int,
        val handoffMerged: Boolean,
    )

    suspend fun resolveCanonicalTrackUids(songs: List<Song>): List<String> =
        withContext(Dispatchers.IO) {
            if (songs.isEmpty()) return@withContext emptyList()
            syncDb.ensureLegacyMigrated(context)
            val fpByLocalUid =
                syncDb.audioFingerprintDao().getAll().associateBy { it.trackUid }.toMutableMap()
            val syncedAll = syncDb.syncedTrackDao().getAll()
            val localToBlake3 = HashMap<String, String>(songs.size)
            val localUids = ArrayList<String>(songs.size)
            for (song in songs) {
                val localUid = song.uid.toString()
                localUids.add(localUid)
                if (localToBlake3.containsKey(localUid)) continue
                val cachedHex =
                    fpByLocalUid[localUid]
                        ?.takeIf { isFingerprintCacheFresh(it, song.size, song.modifiedMs) }
                        ?.audioFrameBlake3Hex
                        ?.takeIf { isHex64(it) }
                if (cachedHex != null) {
                    localToBlake3[localUid] = cachedHex.lowercase()
                    continue
                }
                val songRel =
                    song.path.components.components.joinToString("/").replace('\\', '/').lowercase()
                val syncedMatch =
                    syncedAll.firstOrNull { s ->
                        val sRel = s.relativePath.replace('\\', '/').lowercase()
                        isHex64(s.trackUid) && (songRel == sRel || songRel.endsWith("/$sRel"))
                    }
                if (syncedMatch != null) {
                    cacheFingerprintAlias(song, syncedMatch.trackUid)
                    localToBlake3[localUid] = syncedMatch.trackUid.lowercase()
                    continue
                }
                waveformRepository.getOrComputeAudioFrameBlake3(song)?.takeIf { isHex64(it) }?.let { b3 ->
                    localToBlake3[localUid] = b3.lowercase()
                }
            }
            for ((localUid, fpHex) in localToBlake3) {
                if (syncDb.reanchorTrackByFingerprint(localUid, fpHex)) {
                    playCountRepository.reloadTrackFromDb(localUid)
                    lwwStore.reloadTrackOverridesFromDb(localUid)
                }
            }
            translateLocalUidsToCanonical(localUids, localToBlake3)
        }

    suspend fun resolveSlotSongs(slot: SessionSlotItem): List<Song> =
        withContext(Dispatchers.IO) {
            syncDb.ensureLegacyMigrated(context)
            val songs = musicRepository.library?.songs.orEmpty()
            if (songs.isEmpty() || slot.queueTrackUids.isEmpty()) return@withContext emptyList()
            val songsByUid = songs.associateBy { it.uid.toString() }
            val songByPcUid = buildSongByPcUidMap(songs, songsByUid)
            resolveCanonicalUidsToItems(
                uids = slot.queueTrackUids,
                blake3ToItem = songByPcUid,
                localUidToItem = songsByUid,
            )
        }

    private fun buildSongByPcUidMap(
        songs: Collection<Song>,
        songsByUid: Map<String, Song>,
        fpByLocalUid: Map<String, AudioFingerprintCacheEntity> =
            syncDb.audioFingerprintDao().getAll().associateBy { it.trackUid },
        syncedAll: List<SyncedTrackEntity> = syncDb.syncedTrackDao().getAll(),
    ): HashMap<String, Song> {
        val songByPcUid = HashMap<String, Song>()
        for ((localUid, fp) in fpByLocalUid) {
            val s = songsByUid[localUid] ?: continue
            if (!isFingerprintCacheFresh(fp, s.size, s.modifiedMs)) continue
            val hex = fp.audioFrameBlake3Hex.trim().lowercase()
            if (isHex64(hex)) {
                songByPcUid[hex] = s
            }
        }
        val songRelIndex = PlaylistM3u8Helper.buildSongRelPathIndex(songs)
        for (synced in syncedAll) {
            val match = PlaylistM3u8Helper.findMatchingSongByRelPath(synced.relativePath, songRelIndex) ?: continue
            val pcKey = synced.trackUid.trim().lowercase()
            if (pcKey.isNotEmpty()) {
                songByPcUid.putIfAbsent(pcKey, match)
            }
            cacheFingerprintAlias(match, synced.trackUid)
        }
        return songByPcUid
    }

    private data class PendingRemoteLedgerResult(
        val unmatchedCounts: HashMap<String, Int>,
        val unmatchedLoved: HashSet<String>,
        val unmatchedUnloved: HashSet<String>,
    )

    private fun applyRemoteCountsAndLoved(
        songs: Collection<Song>,
        countsInput: Map<String, Int>,
        lovedInput: Collection<String>,
        unlovedInput: Collection<String>,
        songByPcUid: Map<String, Song>,
        songsByUid: Map<String, Song>,
    ): PendingRemoteLedgerResult {
        val unmatchedCounts = HashMap<String, Int>()
        for ((pcUid, remoteCount) in countsInput) {
            if (remoteCount <= 0) continue
            val matchedSong = songByPcUid[pcUid.lowercase()] ?: songsByUid[pcUid]
            if (matchedSong != null) {
                playCountRepository.mergeRemotePlayCountAndSync(matchedSong.uid.toString(), remoteCount)
            } else {
                unmatchedCounts[pcUid] = remoteCount
            }
        }
        val unmatchedLoved = HashSet<String>()
        for (lovedPcUid in lovedInput) {
            val matchedSong = songByPcUid[lovedPcUid.lowercase()] ?: songsByUid[lovedPcUid]
            if (matchedSong != null) {
                if (!playCountRepository.isLoved(matchedSong) &&
                    !lwwStore.hasPendingMutation(matchedSong.uid.toString(), "rating")
                ) {
                    playCountRepository.setLoved(matchedSong, true)
                }
            } else {
                unmatchedLoved.add(lovedPcUid)
            }
        }
        val unmatchedUnloved = HashSet<String>()
        for (unlovedPcUid in unlovedInput) {
            val matchedSong = songByPcUid[unlovedPcUid.lowercase()] ?: songsByUid[unlovedPcUid]
            if (matchedSong != null) {
                if (playCountRepository.isLoved(matchedSong) &&
                    !lwwStore.hasPendingMutation(matchedSong.uid.toString(), "rating")
                ) {
                    playCountRepository.setLoved(matchedSong, false)
                }
            } else {
                unmatchedLoved.remove(unlovedPcUid)
                unmatchedUnloved.add(unlovedPcUid)
            }
        }
        return PendingRemoteLedgerResult(unmatchedCounts, unmatchedLoved, unmatchedUnloved)
    }

    private fun evictStaleOverridesForTracks(
        trackMtimes: Map<String, Long>,
        songByPcUid: Map<String, Song>,
        songsByUid: Map<String, Song>,
    ): HashMap<String, Long> {
        val unmatched = HashMap<String, Long>()
        for ((pcUidLower, mtimeMs) in trackMtimes) {
            val matchedSong = songByPcUid[pcUidLower] ?: songsByUid[pcUidLower]
            if (matchedSong != null) {
                lwwStore.evictStaleOverridesForTrack(matchedSong.uid.toString(), mtimeMs)
            } else {
                unmatched[pcUidLower] = mtimeMs
            }
        }
        return unmatched
    }

    private fun reconcilePendingRemoteLedger() {
        val countsCopy: Map<String, Int>
        val lovedCopy: Set<String>
        val unlovedCopy: Set<String>
        val evictCopy: Map<String, Long>
        synchronized(pendingRemoteLedgerLock) {
            if (pendingRemoteCounts.isEmpty() &&
                pendingRemoteLoved.isEmpty() &&
                pendingRemoteUnloved.isEmpty() &&
                pendingEvictTrackMtimes.isEmpty()
            ) {
                return
            }
            if (pendingRemoteReconcileRemaining <= 0) {
                pendingRemoteCounts.clear()
                pendingRemoteLoved.clear()
                pendingRemoteUnloved.clear()
                pendingEvictTrackMtimes.clear()
                return
            }
            countsCopy = HashMap(pendingRemoteCounts)
            lovedCopy = HashSet(pendingRemoteLoved)
            unlovedCopy = HashSet(pendingRemoteUnloved)
            evictCopy = HashMap(pendingEvictTrackMtimes)
        }
        val songs = musicRepository.library?.songs.orEmpty()
        if (songs.isEmpty()) return
        val songsByUid = songs.associateBy { it.uid.toString() }
        val songByPcUid = buildSongByPcUidMap(songs, songsByUid)
        val res =
            applyRemoteCountsAndLoved(
                songs = songs,
                countsInput = countsCopy,
                lovedInput = lovedCopy,
                unlovedInput = unlovedCopy,
                songByPcUid = songByPcUid,
                songsByUid = songsByUid,
            )
        val stillEvicts = evictStaleOverridesForTracks(evictCopy, songByPcUid, songsByUid)
        synchronized(pendingRemoteLedgerLock) {
            pendingRemoteCounts.clear()
            pendingRemoteLoved.clear()
            pendingRemoteUnloved.clear()
            pendingEvictTrackMtimes.clear()
            pendingRemoteReconcileRemaining -= 1
            if (pendingRemoteReconcileRemaining > 0) {
                pendingRemoteCounts.putAll(res.unmatchedCounts)
                pendingRemoteLoved.addAll(res.unmatchedLoved)
                pendingRemoteUnloved.addAll(res.unmatchedUnloved)
                pendingEvictTrackMtimes.putAll(stillEvicts)
            }
        }
    }

    private fun pushV2ReportAndApplyCounts(
        cleanBase: String,
        token: String,
        deviceId: String,
        completedOutboxIds: List<String>,
        cancelToken: LanHttpClient.CancelToken? = null,
    ): ReportSyncResult {
        val songs = musicRepository.library?.songs.orEmpty()
        val songsByUid = songs.associateBy { it.uid.toString() }
        val fpByLocalUid =
            syncDb.audioFingerprintDao().getAll().associateBy { it.trackUid }.toMutableMap()
        val syncedAll = syncDb.syncedTrackDao().getAll()
        val songByPcUid = buildSongByPcUidMap(songs, songsByUid, fpByLocalUid, syncedAll)

        fun resolvePcUid(song: Song): String? {
            val localUid = song.uid.toString()
            fpByLocalUid[localUid]
                ?.takeIf { isFingerprintCacheFresh(it, song.size, song.modifiedMs) }
                ?.audioFrameBlake3Hex
                ?.takeIf { it.length == 64 }
                ?.let { return it }
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

        // 1. Delta-only play counts with Last.fm scrobble watermark split
        val playDeltas = ArrayList<PlayDeltaPayload>()
        val pushedWatermarks = ArrayList<Pair<String, Int>>()
        for (song in songs) {
            val localUid = song.uid.toString()
            val snap = playCountRepository.getSnapshot(song)
            val alreadySynced = playCountRepository.getLanSyncedCount(localUid)
            val push =
                com.shawnshi.tonewise.playback.persist.PlayLedgerWatermarkMath.computeLanPushDelta(
                    com.shawnshi.tonewise.playback.persist.PlayLedgerWatermarkMath.WatermarkState(
                        playCount = snap.playCount,
                        lanSyncedCount = alreadySynced,
                        lastFmSyncedCount = playCountRepository.getLastFmSyncedCount(localUid),
                    )
                )
            if (push.totalDelta <= 0) continue

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
                    playCountDelta = push.totalDelta,
                    lastPlayedTsMs = snap.lastPlayedMs,
                    deviceId = deviceId,
                    scrobbledDelta = push.scrobbledDelta,
                    devicePlayCountTotal = snap.playCount,
                )
            )
            pushedWatermarks.add(localUid to snap.playCount)
        }

        // 2. Pending LWW mutations keyed by blake3 + ack key
        val batch = lwwStore.snapshotPendingMutations()
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
                handoffSlotsRawJson = sessionSlotStore.exportSlotsJson(),
                cancelToken = cancelToken,
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
            lwwStore.removeSyncedMutations(acknowledged)
        }

        var handoffMerged = false
        val rawSlotsJson = reportResp.handoffSlotsRawJson
        if (rawSlotsJson != null) {
            runCatching { sessionSlotStore.mergeRemoteJson(rawSlotsJson) }
                .onSuccess { handoffMerged = true }
                .onFailure { L.w(it, "Report handoff_slots merge failed") }
        }

        // Merge returned PC effective play counts & loved/unloved states back into mobile ledger
        // Defer unmatched PC UIDs until after post-sync musicRepository.requestIndex() resolves real Musikr UIDs
        val ledgerRes =
            applyRemoteCountsAndLoved(
                songs = songs,
                countsInput = reportResp.effectiveCounts,
                lovedInput = reportResp.lovedUids,
                unlovedInput = reportResp.unlovedUids,
                songByPcUid = songByPcUid,
                songsByUid = songsByUid,
            )
        synchronized(pendingRemoteLedgerLock) {
            pendingRemoteCounts.clear()
            pendingRemoteLoved.clear()
            pendingRemoteUnloved.clear()
            if (ledgerRes.unmatchedCounts.isNotEmpty() ||
                ledgerRes.unmatchedLoved.isNotEmpty() ||
                ledgerRes.unmatchedUnloved.isNotEmpty()
            ) {
                pendingRemoteCounts.putAll(ledgerRes.unmatchedCounts)
                pendingRemoteLoved.addAll(ledgerRes.unmatchedLoved)
                pendingRemoteUnloved.addAll(ledgerRes.unmatchedUnloved)
                pendingRemoteReconcileRemaining = 3
            } else {
                pendingRemoteReconcileRemaining = 0
            }
        }

        return ReportSyncResult(
            pushedCounts = playDeltas.size,
            pushedMutations = acknowledged.size,
            handoffMerged = handoffMerged,
        )
    }

    private suspend fun reconcilePendingOutboxPlaylists() {
        playlistReconcileMutex.withLock {
            if (pendingOutboxPlaylists.isEmpty()) return
            val lib = musicRepository.library ?: return
            val songRelIndex = PlaylistM3u8Helper.buildSongRelPathIndex(lib.songs)
            val metadataFallback = lazy {
                com.shawnshi.tonewise.music.AndroidPlaylistTrackMatcher.fromSongs(lib.songs)
            }
            for ((key, pending) in pendingOutboxPlaylists.entries.toList()) {
                val (matchedCount, totalRelCount) =
                    applySingleOutboxPlaylist(pending.playlist, songRelIndex, metadataFallback)
                val allMatched = totalRelCount == 0 || matchedCount >= totalRelCount
                if (allMatched) {
                    pendingOutboxPlaylists.remove(key)
                } else {
                    pending.remainingAttempts -= 1
                    if (pending.remainingAttempts <= 0) {
                        pendingOutboxPlaylists.remove(key)
                    }
                }
            }
        }
    }

    private suspend fun applySingleOutboxPlaylist(
        pl: OutboxPlaylistDto,
        songRelIndex: PlaylistM3u8Helper.SongRelPathIndex,
        metadataFallback: Lazy<com.shawnshi.tonewise.music.AndroidPlaylistTrackMatcher<Song>>,
    ): Pair<Int, Int> {
        val totalExpected = pl.relPaths.size
        var matchedCount = 0
        runCatching {
            val lib = musicRepository.library ?: return 0 to totalExpected
            if (pl.relPaths.isEmpty()) {
                val existing = lib.findPlaylistByName(pl.name)
                if (existing != null && existing.songs.isNotEmpty()) {
                    musicRepository.rewritePlaylist(existing, emptyList())
                }
                return 0 to 0
            }
            val matchedSongs =
                pl.relPaths.mapNotNull { rel ->
                    PlaylistM3u8Helper.findMatchingSongByRelPath(rel, songRelIndex)
                        ?: metadataFallback.value.matchByFilenameHint(rel.substringAfterLast('/').substringAfterLast('\\'))
                }
            matchedCount = matchedSongs.size
            if (matchedSongs.isEmpty()) return 0 to totalExpected
            val matchedUids = matchedSongs.map { it.uid }
            val existing = lib.findPlaylistByName(pl.name)
            val legacyName = legacyUnderscoredPlaylistFilename(pl.name)
            if (legacyName != pl.name) {
                val legacyPlaylist = lib.findPlaylistByName(legacyName)
                if (existing == null && legacyPlaylist != null) {
                    musicRepository.renamePlaylist(legacyPlaylist, pl.name)
                    val renamed = musicRepository.library?.findPlaylistByName(pl.name)
                    if (renamed != null && renamed.songs.map { it.uid } != matchedUids) {
                        musicRepository.rewritePlaylist(renamed, matchedSongs)
                    }
                    return matchedCount to totalExpected
                } else if (existing != null && legacyPlaylist != null) {
                    if (
                        legacyPlaylist.songs.isEmpty() ||
                            legacyPlaylist.songs.map { it.uid }.toSet() == matchedUids.toSet()
                    ) {
                        musicRepository.deletePlaylist(legacyPlaylist)
                    }
                }
            }
            if (existing == null) {
                musicRepository.createPlaylist(pl.name, matchedSongs)
            } else if (existing.songs.map { it.uid } != matchedUids) {
                musicRepository.rewritePlaylist(existing, matchedSongs)
            }
        }.onFailure { L.w(it, "Direct playlist apply skipped for ${pl.name}") }
        return matchedCount to totalExpected
    }

    private fun isFingerprintCacheFresh(
        cached: AudioFingerprintCacheEntity,
        size: Long,
        mtimeMs: Long,
    ): Boolean =
        cached.fileSize == size &&
            (cached.mtimeMs == 0L ||
                mtimeMs <= 0L ||
                kotlin.math.abs(cached.mtimeMs - mtimeMs) <= 2_000L)

    private fun cacheFingerprintAlias(song: Song, blake3Hex: String) {
        if (!isHex64(blake3Hex)) return
        val normHex = blake3Hex.lowercase()
        val localUid = song.uid.toString()
        val existing = syncDb.audioFingerprintDao().getByUid(localUid)
        val freshExisting =
            existing?.takeIf { isFingerprintCacheFresh(it, song.size, song.modifiedMs) }
        if (freshExisting != null &&
            freshExisting.audioFrameBlake3Hex.equals(normHex, ignoreCase = true)
        ) {
            return
        }
        syncDb.audioFingerprintDao().upsert(
            AudioFingerprintCacheEntity(
                trackUid = localUid,
                fileSize = song.size,
                audioFrameBlake3Hex = normHex,
                headerBlake3Hex = freshExisting?.headerBlake3Hex ?: "",
                headerSize = freshExisting?.headerSize ?: 0L,
                updatedAtMs = System.currentTimeMillis(),
                mtimeMs = song.modifiedMs,
            )
        )
    }

    private fun computeLocalAudioFingerprintSafe(rawUriOrPath: String): Pair<String, String>? =
        runCatching {
            val fp =
                if (rawUriOrPath.startsWith("content://")) {
                    context.contentResolver.openFileDescriptor(Uri.parse(rawUriOrPath), "r")?.use { pfd ->
                        TonewiseCore.audioFingerprint(pfd)
                    }
                } else {
                    TonewiseCore.audioFingerprintPath(rawUriOrPath)
                }
            fp?.let { it.headerBlake3Hex to it.audioFrameBlake3Hex }
        }.getOrNull()

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
        val ids = current.map { it.id }
        writeBackScope.launch {
            lwwStore.saveSceneOrderIds(ids)
        }
    }

    private fun loadCachedOrDefaultScenes(): List<SceneBanner> {
        val cached = File(context.filesDir, ".tonewise/scenes_5d.json")
        val raw =
            if (cached.isFile) {
                runCatching { cached.readText(Charsets.UTF_8) }.getOrNull()
            } else {
                null
            } ?: SceneRuleEvaluator.DEFAULT_SCENES_JSON
        return lwwStore.applySceneOrder(
            runCatching { SceneRuleEvaluator.parseScenesJson(raw) }.getOrDefault(emptyList())
        )
    }

    private fun sanitizeKey(uid: String): String =
        uid.replace(Regex("[^A-Za-z0-9._-]+"), "_").take(80)

    private fun isVpnActive(): Boolean {
        val cm = context.getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager
            ?: return false
        return cm.allNetworks.any { net ->
            val caps = cm.getNetworkCapabilities(net)
            caps != null && caps.hasTransport(NetworkCapabilities.TRANSPORT_VPN)
        }
    }

    private fun isHex64(s: String): Boolean =
        s.length == 64 && s.all { it in '0'..'9' || it in 'a'..'f' || it in 'A'..'F' }

    private fun normalizeBaseUrl(raw: String): String = normalizeBaseUrlStatic(raw)

    private fun httpGetString(
        url: String,
        token: String? = null,
        cancelToken: LanHttpClient.CancelToken? = null,
    ): String = LanHttpClient.getString(url = url, bearerToken = token, cancelToken = cancelToken)

    private fun httpGetBytes(
        url: String,
        token: String? = null,
        cancelToken: LanHttpClient.CancelToken? = null,
    ): ByteArray = LanHttpClient.getBytes(url = url, bearerToken = token, cancelToken = cancelToken)

    private fun httpPostJson(
        url: String,
        body: String,
        token: String? = null,
        cancelToken: LanHttpClient.CancelToken? = null,
    ): String =
        LanHttpClient.postJson(
            url = url,
            body = body,
            bearerToken = token,
            cancelToken = cancelToken,
            readTimeoutMs = 15_000,
        )

    data class M3u8Entry(
        val durationMs: Long,
        val title: String,
        val artists: List<String>,
        val pathComponents: Components,
    )

    companion object {
        fun resolveTokenForHost(
            pairedHost: PairedHostInfo?,
            targetBaseUrl: String,
            verifiedHostId: String? = null,
        ): String? {
            if (pairedHost == null) return null
            val token = pairedHost.token.takeIf { it.isNotBlank() } ?: return null
            val cleanTarget = normalizeBaseUrlStatic(targetBaseUrl)
            if (cleanTarget.isEmpty()) return null
            val cleanPaired = normalizeBaseUrlStatic(pairedHost.baseUrl)
            if (cleanTarget.equals(cleanPaired, ignoreCase = true)) {
                return token
            }
            val cleanVerifiedId = verifiedHostId?.trim().orEmpty()
            val pairedId = pairedHost.hostId.trim()
            if (cleanVerifiedId.isNotEmpty() &&
                pairedId.isNotEmpty() &&
                cleanVerifiedId.equals(pairedId, ignoreCase = true) &&
                LanHttpClient.isSafeLanOrHttpsUrl(cleanTarget)
            ) {
                return token
            }
            return null
        }

        internal fun normalizeBaseUrlStatic(raw: String): String {
            var trimmed = raw.trim().replace('：', ':').trimEnd('/')
            if (trimmed.isEmpty()) return ""
            if (
                !trimmed.startsWith("http://", ignoreCase = true) &&
                    !trimmed.startsWith("https://", ignoreCase = true)
            ) {
                trimmed = "http://$trimmed"
            }
            val uri = runCatching { URI(trimmed) }.getOrNull()
            val host = uri?.host?.takeIf { it.isNotBlank() }
            if (
                uri != null &&
                    uri.scheme?.equals("http", ignoreCase = true) == true &&
                    host != null &&
                    uri.port == -1
            ) {
                val formattedHost = if (host.contains(':') && !host.startsWith("[")) "[$host]" else host
                val path = uri.rawPath?.trimEnd('/').orEmpty()
                return "http://$formattedHost:45820$path"
            }
            return trimmed
        }

        internal fun formatConnectionError(
            baseUrl: String,
            error: Throwable,
            vpnActive: Boolean = false,
        ): String {
            val combinedMsg =
                generateSequence(error) { it.cause }
                    .mapNotNull { it.message }
                    .joinToString(" ")
            val isConnRefused =
                combinedMsg.contains("ECONNREFUSED", ignoreCase = true) ||
                    combinedMsg.contains("Connection refused", ignoreCase = true)
            if (isConnRefused) {
                return "无法连接 $baseUrl（45820 端口未监听）：请确认电脑端 Tonewise 已打开且局域网同步服务已启动"
            }
            val isReadTimeout =
                combinedMsg.contains("Read timed out", ignoreCase = true) ||
                    combinedMsg.contains("Read timedout", ignoreCase = true)
            if (isReadTimeout) {
                return "连接 $baseUrl 已建立，但电脑端响应超时（Read timed out）：请稍候重试，若歌单或同步队列较大请等待电脑端完成解析"
            }
            val isTimeoutOrUnreachable =
                error is SocketTimeoutException ||
                    generateSequence(error) { it.cause }.any { it is SocketTimeoutException } ||
                    combinedMsg.contains("ETIMEDOUT", ignoreCase = true) ||
                    combinedMsg.contains("EHOSTUNREACH", ignoreCase = true) ||
                    combinedMsg.contains("ENETUNREACH", ignoreCase = true) ||
                    combinedMsg.contains("timed out", ignoreCase = true)
            val isStreamResetOrConnectFailure =
                error is java.io.EOFException ||
                    generateSequence(error) { it.cause }.any {
                        it is java.io.EOFException || it is java.net.SocketException
                    } ||
                    combinedMsg.contains("unexpected end of stream", ignoreCase = true) ||
                    combinedMsg.contains("failed to connect", ignoreCase = true) ||
                    combinedMsg.contains("ECONNRESET", ignoreCase = true)
            if (vpnActive && (isStreamResetOrConnectFailure || isTimeoutOrUnreachable)) {
                return "检测到手机开启了全局 VPN（tun0）正在拦截局域网流量（$baseUrl）：请将 Tonewise（知律）加入 VPN 分应用绕行白名单，或关闭 VPN 后重试"
            }
            if (isTimeoutOrUnreachable) {
                return "连接 $baseUrl 超时或不可达：请确认手机与电脑在同一 Wi-Fi、未开启代理/VPN 拦截且电脑防火墙已放行 45820 端口"
            }
            val detail = error.message?.takeIf { it.isNotBlank() } ?: "请检查网络与电脑端 45820 端口"
            return "连接 $baseUrl 失败：$detail"
        }

        private fun selectPreferredResolvedHost(resolved: NsdServiceInfo): String? {
            val candidates =
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
                    resolved.hostAddresses
                } else {
                    @Suppress("DEPRECATION")
                    listOfNotNull(resolved.host)
                }
            return selectPreferredResolvedHostFromAddresses(candidates)
        }

        internal fun selectPreferredResolvedHostFromAddresses(addresses: List<InetAddress>): String? {
            val best =
                addresses
                    .asSequence()
                    .filter { addr ->
                        if (addr.isLoopbackAddress || addr.isLinkLocalAddress || addr.isAnyLocalAddress) {
                            return@filter false
                        }
                        if (addr is Inet4Address) {
                            val b = addr.address
                            val b0 = b[0].toInt() and 0xFF
                            val b1 = b[1].toInt() and 0xFF
                            // Exclude RFC 2544 / fake-IP VPN TUN ranges (198.18.0.0/15)
                            if (b0 == 198 && b1 in 18..19) return@filter false
                        }
                        true
                    }
                    .minByOrNull { addr ->
                        if (addr is Inet4Address) {
                            val b = addr.address
                            val b0 = b[0].toInt() and 0xFF
                            val b1 = b[1].toInt() and 0xFF
                            when {
                                b0 == 192 && b1 == 168 -> 0
                                b0 == 10 -> 1
                                b0 == 172 && b1 in 16..31 -> 2
                                else -> 3
                            }
                        } else {
                            10
                        }
                    } ?: return null
            val rawHost = best.hostAddress?.substringBefore('%')?.trim()?.takeIf { it.isNotEmpty() } ?: return null
            return if (rawHost.contains(':') && !rawHost.startsWith("[")) "[$rawHost]" else rawHost
        }

        fun translateLocalUidsToCanonical(
            localUids: List<String>,
            localToBlake3: Map<String, String>,
        ): List<String> =
            localUids.mapNotNull { raw ->
                val uid = raw.trim()
                if (uid.isEmpty()) return@mapNotNull null
                localToBlake3[uid]?.lowercase() ?: uid
            }

        fun <T> resolveCanonicalUidsToItems(
            uids: List<String>,
            blake3ToItem: Map<String, T>,
            localUidToItem: Map<String, T>,
        ): List<T> =
            uids.mapNotNull { raw ->
                val uid = raw.trim()
                if (uid.isEmpty()) return@mapNotNull null
                blake3ToItem[uid.lowercase()] ?: localUidToItem[uid]
            }

        fun resolveMoodClusterId(rawMood: String): String? =
            SceneRuleEvaluator.resolveMoodClusterId(rawMood)

        fun buildFocusFilterSceneJson(spec: FocusFilterSpec): String? =
            SceneRuleEvaluator.buildFocusFilterSceneJson(spec)

        fun buildDjFilterSceneJson(
            scene: String?,
            vocals: String?,
            energy: String?,
            excludeVocal: Boolean = false,
            tempoBucket: String? = null,
            moodCluster: String? = null,
            sortArc: String = "flat",
        ): String? =
            SceneRuleEvaluator.buildDjFilterSceneJson(
                scene = scene,
                vocals = vocals,
                energy = energy,
                excludeVocal = excludeVocal,
                tempoBucket = tempoBucket,
                moodCluster = moodCluster,
                sortArc = sortArc,
            )

        fun sanitizePlaylistFilename(raw: String): String =
            PlaylistM3u8Helper.sanitizePlaylistFilename(raw)

        fun legacyUnderscoredPlaylistFilename(raw: String): String =
            PlaylistM3u8Helper.legacyUnderscoredPlaylistFilename(raw)

        fun extractPlaylistNameFromM3u8(lines: List<String>, fallbackName: String): String =
            PlaylistM3u8Helper.extractPlaylistNameFromM3u8(lines, fallbackName)

        fun normalizePlaylistEntryForPc(raw: String): String =
            PlaylistM3u8Helper.normalizePlaylistEntryForPc(raw)

        fun formatM3u8Content(songs: List<Song>, playlistName: String? = null): String =
            PlaylistM3u8Helper.formatM3u8Content(songs, playlistName)

        fun formatM3u8Entries(entries: List<M3u8Entry>, playlistName: String? = null): String =
            PlaylistM3u8Helper.formatM3u8Entries(entries, playlistName)

        internal fun resolveEffectivePruneConfirmed(
            transport: SyncTransportMode,
            baseUrl: String,
            localConfirmed: Boolean,
            remoteConfirmed: Boolean,
        ): Boolean =
            localConfirmed ||
                (transport == SyncTransportMode.USB && LanHttpClient.isLoopbackUrl(baseUrl) && remoteConfirmed)
    }
}
