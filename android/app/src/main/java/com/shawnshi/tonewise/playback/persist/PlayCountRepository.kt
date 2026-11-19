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
import com.shawnshi.tonewise.sync.TonewiseSyncDatabase
import com.shawnshi.tonewise.sync.TrackPlayLedgerEntity
import dagger.hilt.android.qualifiers.ApplicationContext
import java.util.concurrent.ConcurrentHashMap
import javax.inject.Inject
import javax.inject.Singleton
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import org.oxycblt.musikr.Song

data class PlayLedgerSnapshot(
    val playCount: Int = 0,
    val lastPlayedMs: Long = 0L,
    val loved: Boolean = false,
)

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

    fun getLastPlayedMs(song: Song): Long

    fun isLoved(song: Song): Boolean

    fun isLovedByUid(songUid: String): Boolean

    fun setLoved(song: Song, loved: Boolean)

    fun setLovedByUid(songUid: String, loved: Boolean)
}

@Singleton
class PlayCountRepositoryImpl
@Inject
constructor(@ApplicationContext private val context: Context) : PlayCountRepository {
    private val syncDb: TonewiseSyncDatabase by lazy { TonewiseSyncDatabase.getInstance(context) }
    private val ledgerCache = ConcurrentHashMap<String, TrackPlayLedgerEntity>()
    private val cacheInitLock = Any()
    @Volatile private var cacheLoaded = false
    private val ioScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    private val _updates = MutableStateFlow(0L)
    override val updates: StateFlow<Long> = _updates

    private fun ensureCacheLoaded() {
        if (cacheLoaded) return
        synchronized(cacheInitLock) {
            if (cacheLoaded) return
            val all = syncDb.playLedgerDao().getAll()
            for (entity in all) {
                ledgerCache[entity.trackUid] = entity
                staticPlayCountCache[entity.trackUid] = entity.playCount
            }
            staticCacheLoaded = true
            cacheLoaded = true
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
        val current = ledgerCache[key] ?: TrackPlayLedgerEntity(trackUid = key)
        val updated = current.copy(playCount = current.playCount + 1, lastPlayedMs = System.currentTimeMillis())
        ledgerCache[key] = updated
        staticPlayCountCache[key] = updated.playCount
        _updates.value += 1
        ioScope.launch { syncDb.playLedgerDao().upsert(updated) }
        return updated.playCount
    }

    @Synchronized
    override fun mergeHighWaterMarkCount(songUid: String, remoteCount: Int): Int {
        ensureCacheLoaded()
        val current = ledgerCache[songUid] ?: TrackPlayLedgerEntity(trackUid = songUid)
        val merged = maxOf(current.playCount, remoteCount.coerceAtLeast(0))
        if (merged > current.playCount) {
            val updated = current.copy(playCount = merged)
            ledgerCache[songUid] = updated
            staticPlayCountCache[songUid] = merged
            _updates.value += 1
            ioScope.launch { syncDb.playLedgerDao().upsert(updated) }
        }
        return merged
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
            ioScope.launch { syncDb.playLedgerDao().upsert(updated) }
        }
    }

    @Synchronized
    override fun mergeRemotePlayCountAndSync(songUid: String, remoteCount: Int): Int {
        ensureCacheLoaded()
        val cleanRemote = remoteCount.coerceAtLeast(0)
        val current = ledgerCache[songUid] ?: TrackPlayLedgerEntity(trackUid = songUid)
        val mergedCount = maxOf(current.playCount, cleanRemote)
        val nextLanSynced = maxOf(current.lanSyncedCount, cleanRemote)
        if (mergedCount != current.playCount || nextLanSynced != current.lanSyncedCount) {
            val updated = current.copy(playCount = mergedCount, lanSyncedCount = nextLanSynced)
            ledgerCache[songUid] = updated
            staticPlayCountCache[songUid] = mergedCount
            if (mergedCount != current.playCount) {
                _updates.value += 1
            }
            ioScope.launch { syncDb.playLedgerDao().upsert(updated) }
        }
        return mergedCount
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
        val current = ledgerCache[songUid] ?: TrackPlayLedgerEntity(trackUid = songUid)
        if (current.loved != loved) {
            val updated = current.copy(loved = loved)
            ledgerCache[songUid] = updated
            _updates.value += 1
            ioScope.launch { syncDb.playLedgerDao().upsert(updated) }
        }
    }

    companion object {
        private val staticPlayCountCache = ConcurrentHashMap<String, Int>()
        private val staticInitLock = Any()
        @Volatile private var staticCacheLoaded = false

        private fun ensureStaticCacheLoaded(context: Context) {
            if (staticCacheLoaded) return
            synchronized(staticInitLock) {
                if (staticCacheLoaded) return
                val all = TonewiseSyncDatabase.getInstance(context).playLedgerDao().getAll()
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
