/*
 * Copyright (c) 2026 Tonewise Project
 * PlayCountRepository.kt is part of Tonewise.
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
 
package com.shawnshi.tonewise.playback.persist

import android.content.Context
import android.os.Looper
import com.shawnshi.tonewise.sync.TonewiseSyncDatabase
import com.shawnshi.tonewise.sync.TrackPlayLedgerEntity
import dagger.hilt.android.qualifiers.ApplicationContext
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.Executors
import javax.inject.Inject
import javax.inject.Singleton
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.asCoroutineDispatcher
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import org.oxycblt.musikr.Song

data class PlayLedgerSnapshot(
    val playCount: Int = 0,
    val lastPlayedMs: Long = 0L,
    val loved: Boolean = false,
)

object PlayLedgerWatermarkMath {
    data class WatermarkState(
        val playCount: Int = 0,
        val lanSyncedCount: Int = 0,
        val lastFmSyncedCount: Int = 0,
    )

    data class LanPushDelta(
        val totalDelta: Int,
        val scrobbledDelta: Int,
        val offlineDelta: Int,
    )

    fun mergeLanRemoteCount(state: WatermarkState, remoteCount: Int): WatermarkState {
        val clean = remoteCount.coerceAtLeast(0)
        return WatermarkState(
            playCount = maxOf(state.playCount, clean),
            lanSyncedCount = maxOf(state.lanSyncedCount, clean),
            lastFmSyncedCount = maxOf(state.lastFmSyncedCount, clean),
        )
    }

    fun mergeCloudRemoteCount(state: WatermarkState, cloudCount: Int): WatermarkState {
        val clean = cloudCount.coerceAtLeast(0)
        return WatermarkState(
            playCount = maxOf(state.playCount, clean),
            lanSyncedCount = maxOf(state.lanSyncedCount, clean),
            lastFmSyncedCount = maxOf(state.lastFmSyncedCount, clean),
        )
    }

    fun onLocalPlayScrobbled(state: WatermarkState): WatermarkState =
        state.copy(lastFmSyncedCount = minOf(state.playCount, state.lastFmSyncedCount + 1))

    fun computeLanPushDelta(state: WatermarkState): LanPushDelta {
        val totalDelta = (state.playCount - state.lanSyncedCount).coerceAtLeast(0)
        val scrobbledFloor = minOf(state.playCount, state.lastFmSyncedCount)
        val scrobbledDelta = (scrobbledFloor - state.lanSyncedCount).coerceIn(0, totalDelta)
        val offlineDelta = totalDelta - scrobbledDelta
        return LanPushDelta(
            totalDelta = totalDelta,
            scrobbledDelta = scrobbledDelta,
            offlineDelta = offlineDelta,
        )
    }
}

interface PlayCountRepository {
    val updates: StateFlow<Long>

    fun getSnapshot(song: Song): PlayLedgerSnapshot

    fun getSnapshotByUid(songUid: String): PlayLedgerSnapshot

    fun getPlayCount(song: Song): Int

    fun getPlayCountByUid(songUid: String): Int

    fun incrementPlayCount(song: Song): Int

    fun mergeHighWaterMarkCount(songUid: String, remoteCount: Int): Int

    fun getLanSyncedCount(songUid: String): Int

    fun markLanSyncedCount(songUid: String, syncedCount: Int)

    fun mergeRemotePlayCountAndSync(songUid: String, remoteCount: Int): Int

    fun getLastFmSyncedCount(songUid: String): Int = 0

    fun markLastFmSyncedCount(songUid: String, syncedCount: Int) {}

    fun getTotalLastFmSyncedCount(): Int = 0

    fun getLastPlayedMs(song: Song): Long

    fun isLoved(song: Song): Boolean

    fun isLovedByUid(songUid: String): Boolean

    fun setLoved(song: Song, loved: Boolean)

    fun setLovedByUid(songUid: String, loved: Boolean)

    fun setLastFmSyncedCounts(counts: Map<String, Int>) {}

    fun setLastFmPulledCounts(counts: Map<String, Int>) {}

    fun reloadTrackFromDb(songUid: String) {}
}

@Singleton
class PlayCountRepositoryImpl
@Inject
constructor(@ApplicationContext private val context: Context) : PlayCountRepository {
    private val syncDb: TonewiseSyncDatabase by lazy { TonewiseSyncDatabase.getInstance(context) }
    private val ledgerCache = ConcurrentHashMap<String, TrackPlayLedgerEntity>()
    private val cacheInitLock = Any()
    @Volatile private var cacheLoaded = false
    private val dbDispatcher =
        Executors.newSingleThreadExecutor { runnable ->
                Thread(runnable, "Tonewise-PlayLedgerDb").apply { isDaemon = true }
            }
            .asCoroutineDispatcher()
    private val ioScope = CoroutineScope(SupervisorJob() + dbDispatcher)

    private val _updates = MutableStateFlow(0L)
    override val updates: StateFlow<Long> = _updates

    init {
        ioScope.launch { ensureCacheLoaded() }
    }

    private fun ensureCacheLoaded() {
        if (cacheLoaded) return
        if (isMainThread()) {
            ioScope.launch { ensureCacheLoaded() }
            return
        }
        synchronized(cacheInitLock) {
            if (cacheLoaded) return
            syncDb.ensureLegacyMigrated(context)
            val all = syncDb.playLedgerDao().getAll()
            for (entity in all) {
                ledgerCache[entity.trackUid] = entity
                staticPlayCountCache[entity.trackUid] = entity.playCount
            }
            staticCacheLoaded = true
            cacheLoaded = true
            if (all.isNotEmpty()) {
                _updates.value += 1
            }
        }
    }

    override fun getSnapshot(song: Song): PlayLedgerSnapshot =
        getSnapshotByUid(song.uid.toString())

    override fun getSnapshotByUid(songUid: String): PlayLedgerSnapshot {
        ensureCacheLoaded()
        val entity = ledgerCache[songUid] ?: return PlayLedgerSnapshot()
        return PlayLedgerSnapshot(
            playCount = entity.playCount,
            lastPlayedMs = entity.lastPlayedMs,
            loved = entity.loved,
        )
    }

    override fun getPlayCount(song: Song): Int = getPlayCountByUid(song.uid.toString())

    override fun getPlayCountByUid(songUid: String): Int {
        ensureCacheLoaded()
        return ledgerCache[songUid]?.playCount ?: 0
    }

    @Synchronized
    override fun incrementPlayCount(song: Song): Int {
        ensureCacheLoaded()
        val key = song.uid.toString()
        if (!cacheLoaded) {
            val optimistic = (ledgerCache[key]?.playCount ?: 0) + 1
            ioScope.launch {
                ensureCacheLoaded()
                synchronized(this@PlayCountRepositoryImpl) {
                    val base = ledgerCache[key] ?: TrackPlayLedgerEntity(trackUid = key)
                    val updated =
                        base.copy(
                            playCount = base.playCount + 1,
                            lastPlayedMs = System.currentTimeMillis(),
                        )
                    ledgerCache[key] = updated
                    staticPlayCountCache[key] = updated.playCount
                    val dao = syncDb.playLedgerDao()
                    dao.insertIfAbsent(TrackPlayLedgerEntity(trackUid = key))
                    dao.updatePlayCount(key, updated.playCount, updated.lastPlayedMs)
                }
                _updates.value += 1
            }
            return optimistic
        }
        val current = ledgerCache[key] ?: TrackPlayLedgerEntity(trackUid = key)
        val updated = current.copy(playCount = current.playCount + 1, lastPlayedMs = System.currentTimeMillis())
        ledgerCache[key] = updated
        staticPlayCountCache[key] = updated.playCount
        _updates.value += 1
        ioScope.launch {
            val dao = syncDb.playLedgerDao()
            dao.insertIfAbsent(TrackPlayLedgerEntity(trackUid = key))
            dao.updatePlayCount(key, updated.playCount, updated.lastPlayedMs)
        }
        return updated.playCount
    }

    @Synchronized
    override fun reloadTrackFromDb(songUid: String) {
        ensureCacheLoaded()
        val row = syncDb.playLedgerDao().getByUid(songUid)
        if (row != null) {
            ledgerCache[songUid] = row
            staticPlayCountCache[songUid] = row.playCount
            _updates.value += 1
        }
    }

    @Synchronized
    override fun mergeHighWaterMarkCount(songUid: String, remoteCount: Int): Int {
        ensureCacheLoaded()
        val current = ledgerCache[songUid] ?: TrackPlayLedgerEntity(trackUid = songUid)
        val merged =
            PlayLedgerWatermarkMath.mergeCloudRemoteCount(
                PlayLedgerWatermarkMath.WatermarkState(
                    playCount = current.playCount,
                    lanSyncedCount = current.lanSyncedCount,
                    lastFmSyncedCount = current.syncedCount,
                ),
                remoteCount,
            )
        if (merged.playCount != current.playCount ||
            merged.lanSyncedCount != current.lanSyncedCount ||
            merged.lastFmSyncedCount != current.syncedCount
        ) {
            val updated =
                current.copy(
                    playCount = merged.playCount,
                    lanSyncedCount = merged.lanSyncedCount,
                    syncedCount = merged.lastFmSyncedCount,
                )
            ledgerCache[songUid] = updated
            staticPlayCountCache[songUid] = merged.playCount
            if (merged.playCount != current.playCount) {
                _updates.value += 1
            }
            ioScope.launch {
                val dao = syncDb.playLedgerDao()
                dao.insertIfAbsent(TrackPlayLedgerEntity(trackUid = songUid))
                dao.updatePlayCountAndSyncWatermarks(
                    songUid,
                    merged.playCount,
                    merged.lanSyncedCount,
                    merged.lastFmSyncedCount,
                )
            }
        }
        return merged.playCount
    }

    override fun getLanSyncedCount(songUid: String): Int {
        ensureCacheLoaded()
        return ledgerCache[songUid]?.lanSyncedCount ?: 0
    }

    @Synchronized
    override fun markLanSyncedCount(songUid: String, syncedCount: Int) {
        ensureCacheLoaded()
        val clean = syncedCount.coerceAtLeast(0)
        val current = ledgerCache[songUid] ?: TrackPlayLedgerEntity(trackUid = songUid)
        if (clean > current.lanSyncedCount) {
            val updated = current.copy(lanSyncedCount = clean)
            ledgerCache[songUid] = updated
            ioScope.launch {
                val dao = syncDb.playLedgerDao()
                dao.insertIfAbsent(TrackPlayLedgerEntity(trackUid = songUid))
                dao.updateLanSyncedCount(songUid, clean)
            }
        }
    }

    @Synchronized
    override fun mergeRemotePlayCountAndSync(songUid: String, remoteCount: Int): Int {
        ensureCacheLoaded()
        val current = ledgerCache[songUid] ?: TrackPlayLedgerEntity(trackUid = songUid)
        val merged =
            PlayLedgerWatermarkMath.mergeLanRemoteCount(
                PlayLedgerWatermarkMath.WatermarkState(
                    playCount = current.playCount,
                    lanSyncedCount = current.lanSyncedCount,
                    lastFmSyncedCount = current.syncedCount,
                ),
                remoteCount,
            )
        if (merged.playCount != current.playCount ||
            merged.lanSyncedCount != current.lanSyncedCount ||
            merged.lastFmSyncedCount != current.syncedCount
        ) {
            val updated =
                current.copy(
                    playCount = merged.playCount,
                    lanSyncedCount = merged.lanSyncedCount,
                    syncedCount = merged.lastFmSyncedCount,
                )
            ledgerCache[songUid] = updated
            staticPlayCountCache[songUid] = merged.playCount
            if (merged.playCount != current.playCount) {
                _updates.value += 1
            }
            ioScope.launch {
                val dao = syncDb.playLedgerDao()
                dao.insertIfAbsent(TrackPlayLedgerEntity(trackUid = songUid))
                dao.updatePlayCountAndSyncWatermarks(
                    songUid,
                    merged.playCount,
                    merged.lanSyncedCount,
                    merged.lastFmSyncedCount,
                )
            }
        }
        return merged.playCount
    }

    override fun getLastFmSyncedCount(songUid: String): Int {
        ensureCacheLoaded()
        return ledgerCache[songUid]?.syncedCount ?: 0
    }

    @Synchronized
    override fun markLastFmSyncedCount(songUid: String, syncedCount: Int) {
        ensureCacheLoaded()
        val clean = syncedCount.coerceAtLeast(0)
        val current = ledgerCache[songUid] ?: TrackPlayLedgerEntity(trackUid = songUid)
        if (clean > current.syncedCount) {
            ledgerCache[songUid] = current.copy(syncedCount = clean)
            ioScope.launch {
                val dao = syncDb.playLedgerDao()
                dao.insertIfAbsent(TrackPlayLedgerEntity(trackUid = songUid))
                dao.updateSyncedCount(songUid, clean)
            }
        }
    }

    override fun getTotalLastFmSyncedCount(): Int {
        ensureCacheLoaded()
        return ledgerCache.values.sumOf { it.syncedCount }
    }

    override fun getLastPlayedMs(song: Song): Long {
        ensureCacheLoaded()
        return ledgerCache[song.uid.toString()]?.lastPlayedMs ?: 0L
    }

    override fun isLoved(song: Song): Boolean = isLovedByUid(song.uid.toString())

    override fun isLovedByUid(songUid: String): Boolean {
        ensureCacheLoaded()
        return ledgerCache[songUid]?.loved ?: false
    }

    @Synchronized
    override fun setLoved(song: Song, loved: Boolean) {
        setLovedByUid(song.uid.toString(), loved)
    }

    @Synchronized
    override fun setLovedByUid(songUid: String, loved: Boolean) {
        ensureCacheLoaded()
        if (!cacheLoaded) {
            ioScope.launch {
                ensureCacheLoaded()
                synchronized(this@PlayCountRepositoryImpl) {
                    val base = ledgerCache[songUid] ?: TrackPlayLedgerEntity(trackUid = songUid)
                    if (base.loved != loved) {
                        ledgerCache[songUid] = base.copy(loved = loved)
                        val dao = syncDb.playLedgerDao()
                        dao.insertIfAbsent(TrackPlayLedgerEntity(trackUid = songUid))
                        dao.updateLoved(songUid, loved)
                    }
                }
                _updates.value += 1
            }
            return
        }
        val current = ledgerCache[songUid] ?: TrackPlayLedgerEntity(trackUid = songUid)
        if (current.loved != loved) {
            val updated = current.copy(loved = loved)
            ledgerCache[songUid] = updated
            _updates.value += 1
            ioScope.launch {
                val dao = syncDb.playLedgerDao()
                dao.insertIfAbsent(TrackPlayLedgerEntity(trackUid = songUid))
                dao.updateLoved(songUid, loved)
            }
        }
    }

    @Synchronized
    override fun setLastFmSyncedCounts(counts: Map<String, Int>) {
        if (counts.isEmpty()) return
        ensureCacheLoaded()
        val changed = ArrayList<Pair<String, Int>>()
        for ((uid, rawCnt) in counts) {
            val cnt = rawCnt.coerceAtLeast(0)
            val current = ledgerCache[uid] ?: TrackPlayLedgerEntity(trackUid = uid)
            if (current.syncedCount != cnt) {
                ledgerCache[uid] = current.copy(syncedCount = cnt)
                changed.add(uid to cnt)
            }
        }
        if (changed.isNotEmpty()) {
            ioScope.launch {
                val dao = syncDb.playLedgerDao()
                syncDb.runInTransaction {
                    for ((uid, cnt) in changed) {
                        dao.insertIfAbsent(TrackPlayLedgerEntity(trackUid = uid))
                        dao.updateSyncedCount(uid, cnt)
                    }
                }
            }
        }
    }

    @Synchronized
    override fun setLastFmPulledCounts(counts: Map<String, Int>) {
        if (counts.isEmpty()) return
        ensureCacheLoaded()
        val changed = ArrayList<Pair<String, Int>>()
        for ((uid, rawCnt) in counts) {
            val cnt = rawCnt.coerceAtLeast(0)
            val current = ledgerCache[uid] ?: TrackPlayLedgerEntity(trackUid = uid)
            if (current.pulledCloudCount != cnt) {
                ledgerCache[uid] = current.copy(pulledCloudCount = cnt)
                changed.add(uid to cnt)
            }
        }
        if (changed.isNotEmpty()) {
            ioScope.launch {
                val dao = syncDb.playLedgerDao()
                syncDb.runInTransaction {
                    for ((uid, cnt) in changed) {
                        dao.insertIfAbsent(TrackPlayLedgerEntity(trackUid = uid))
                        dao.updatePulledCloudCount(uid, cnt)
                    }
                }
            }
        }
    }

    companion object {
        private val staticPlayCountCache = ConcurrentHashMap<String, Int>()
        private val staticInitLock = Any()
        @Volatile private var staticCacheLoaded = false
        private val staticWarmupExecutor =
            Executors.newSingleThreadExecutor { runnable ->
                Thread(runnable, "Tonewise-StaticPlayCache").apply { isDaemon = true }
            }

        private fun isMainThread(): Boolean {
            val mainLooper = runCatching { Looper.getMainLooper() }.getOrNull() ?: return false
            return mainLooper.thread === Thread.currentThread()
        }

        private fun ensureStaticCacheLoaded(context: Context) {
            if (staticCacheLoaded) return
            if (isMainThread()) {
                val appContext = context.applicationContext
                staticWarmupExecutor.execute { ensureStaticCacheLoaded(appContext) }
                return
            }
            synchronized(staticInitLock) {
                if (staticCacheLoaded) return
                val db = TonewiseSyncDatabase.getInstance(context)
                db.ensureLegacyMigrated(context)
                val all = db.playLedgerDao().getAll()
                for (entity in all) {
                    staticPlayCountCache[entity.trackUid] = entity.playCount
                }
                staticCacheLoaded = true
            }
        }

        fun getPlayCount(context: Context, song: Song): Int {
            val uid = song.uid.toString()
            if (staticCacheLoaded) {
                return staticPlayCountCache[uid] ?: 0
            }
            ensureStaticCacheLoaded(context)
            return staticPlayCountCache[uid] ?: 0
        }
    }
}
