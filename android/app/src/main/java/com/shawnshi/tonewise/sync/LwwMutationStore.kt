/*
 * Copyright (c) 2026 Tonewise Project
 * LwwMutationStore.kt is part of Tonewise.
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
import android.os.Build
import android.os.Looper
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.Executors
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.asCoroutineDispatcher
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import org.oxycblt.musikr.core.TonewiseCore

/**
 * Room-backed store for LWW metadata overrides, pending out-of-band tag mutations,
 * LAN pairing metadata, scene ordering, and M3U8 3-way merge baselines.
 */
internal class LwwMutationStore(
    private val syncDbProvider: () -> TonewiseSyncDatabase,
    private val appContextProvider: (() -> Context)? = null,
) {
    data class OverrideRecord(val value: String, val updatedAtMs: Long)

    private val syncDb: TonewiseSyncDatabase
        get() = syncDbProvider()

    private val dbDispatcher =
        Executors.newSingleThreadExecutor { runnable ->
                Thread(runnable, "Tonewise-LwwStoreDb").apply { isDaemon = true }
            }
            .asCoroutineDispatcher()
    private val ioScope = CoroutineScope(SupervisorJob() + dbDispatcher)

    private val lock = Any()
    private val loadLock = Any()
    private val pendingMutationsList = mutableListOf<TagMutationRecord>()
    private val lwwOverrides = ConcurrentHashMap<String, OverrideRecord>()

    @Volatile private var loaded = false
    @Volatile private var cachedSceneOrderIds: List<String>? = null
    @Volatile private var cachedPairedHost: PairedHostInfo? = null
    @Volatile private var pairedHostLoaded = false
    @Volatile private var cachedStableDeviceId: String? = null

    @Volatile
    var lwwVersion: Long = 0L
        private set

    private val _pendingMutationCount = MutableStateFlow(0)
    val pendingMutationCount: StateFlow<Int>
        get() = _pendingMutationCount

    private fun isMainThread(): Boolean {
        val mainLooper = runCatching { Looper.getMainLooper() }.getOrNull() ?: return false
        return mainLooper.thread === Thread.currentThread()
    }

    fun warmUpAsync(context: Context? = null) {
        ioScope.launch { ensureLoadedOnBackground(context) }
    }

    fun ensureLoadedOnBackground(context: Context? = null) {
        if (loaded) return
        if (isMainThread()) {
            warmUpAsync(context)
            return
        }
        synchronized(loadLock) {
            if (loaded) return
            val ctx = context ?: appContextProvider?.invoke()
            if (ctx != null) {
                syncDb.ensureLegacyMigrated(ctx)
            }
            val dbOverrides = loadLwwOverridesFromDb()
            val dbPending = loadPendingMutationsFromDb()
            val loadedSceneOrder =
                if (cachedSceneOrderIds == null) readSceneOrderIdsFromDb() else null
            var loadedPairedHost: PairedHostInfo? = null
            if (!pairedHostLoaded) {
                loadedPairedHost = loadPairedHostFromDb()
            }
            if (cachedStableDeviceId == null) {
                cachedStableDeviceId = runCatching { getOrCreateStableDeviceId() }.getOrNull()
            }

            synchronized(lock) {
                var addedOverrides = false
                for ((k, rec) in dbOverrides) {
                    val prev = lwwOverrides[k]
                    if (prev == null || rec.updatedAtMs > prev.updatedAtMs) {
                        lwwOverrides[k] = rec
                        addedOverrides = true
                    }
                }
                if (addedOverrides) {
                    lwwVersion += 1
                }
                val inMemoryKeys = pendingMutationsList.mapTo(HashSet()) { it.trackUid to it.field }
                for (item in dbPending) {
                    if ((item.trackUid to item.field) !in inMemoryKeys) {
                        pendingMutationsList.add(item)
                    }
                }
                _pendingMutationCount.value = pendingMutationsList.size
            }
            if (loadedSceneOrder != null && cachedSceneOrderIds == null) {
                cachedSceneOrderIds = loadedSceneOrder
            }
            if (!pairedHostLoaded) {
                cachedPairedHost = loadedPairedHost
                pairedHostLoaded = true
            }
            loaded = true
        }
    }

    fun reloadTrackOverridesFromDb(uid: String) {
        if (!loaded) {
            ensureLoadedOnBackground()
        }
        val rows = syncDb.lwwMutationDao().getOverridesForUid(uid)
        if (rows.isEmpty()) return
        synchronized(lock) {
            for (row in rows) {
                val key = overrideKey(row.trackUid, row.field)
                val prev = lwwOverrides[key]
                if (prev == null || row.updatedAtMs >= prev.updatedAtMs) {
                    lwwOverrides[key] = OverrideRecord(row.value, row.updatedAtMs)
                }
            }
            lwwVersion += 1
        }
    }

    fun getLwwOverride(uid: String, field: String): String? {
        if (!loaded) {
            if (isMainThread()) warmUpAsync() else ensureLoadedOnBackground()
        }
        return lwwOverrides[overrideKey(uid, field)]?.value
    }

    fun setLwwOverrideInMemory(
        uid: String,
        field: String,
        value: String,
        updatedAtMs: Long = System.currentTimeMillis(),
    ) {
        lwwOverrides[overrideKey(uid, field)] = OverrideRecord(value, updatedAtMs)
    }

    fun hasPendingMutation(uid: String, field: String): Boolean {
        if (!loaded && !isMainThread()) {
            ensureLoadedOnBackground()
        }
        return synchronized(lock) {
            pendingMutationsList.any {
                it.trackUid == uid && it.field.equals(field, ignoreCase = true)
            }
        }
    }

    fun evictStaleOverridesForTrack(uid: String, syncedMtimeMs: Long): Int {
        if (uid.isBlank() || syncedMtimeMs <= 0L) return 0
        if (!loaded && !isMainThread()) {
            ensureLoadedOnBackground()
        }
        val prefix = "$uid||"
        val toEvict = ArrayList<String>()
        synchronized(lock) {
            val pendingFields =
                pendingMutationsList
                    .asSequence()
                    .filter { it.trackUid == uid }
                    .map { it.field.lowercase() }
                    .toHashSet()
            for ((k, rec) in lwwOverrides) {
                if (!k.startsWith(prefix)) continue
                val field = k.substring(prefix.length)
                if (field.lowercase() in pendingFields) continue
                if (rec.updatedAtMs < syncedMtimeMs) {
                    toEvict.add(field)
                }
            }
            if (toEvict.isNotEmpty()) {
                for (field in toEvict) {
                    lwwOverrides.remove(overrideKey(uid, field))
                }
                lwwVersion += 1
            }
        }
        if (toEvict.isNotEmpty()) {
            ioScope.launch {
                val dao = syncDb.lwwMutationDao()
                syncDb.runInTransaction {
                    for (field in toEvict) {
                        dao.deleteOverride(uid, field)
                    }
                }
            }
        }
        return toEvict.size
    }

    fun recordPendingMutation(
        uid: String,
        field: String,
        value: String,
        updatedAtMs: Long,
        deviceId: String,
    ) {
        setLwwOverrideInMemory(uid, field, value, updatedAtMs)
        val count =
            synchronized(lock) {
                pendingMutationsList.removeAll { it.trackUid == uid && it.field == field }
                pendingMutationsList.add(
                    TagMutationRecord(
                        trackUid = uid,
                        field = field,
                        value = value,
                        updatedAtMs = updatedAtMs,
                        deviceId = deviceId,
                    )
                )
                lwwVersion += 1
                pendingMutationsList.size
            }
        _pendingMutationCount.value = count
    }

    fun persistMutationEntities(
        overrideEntity: LwwOverrideEntity,
        mutationEntity: PendingTagMutationEntity,
    ) {
        ioScope.launch {
            syncDb.runInTransaction {
                syncDb.lwwMutationDao().upsertOverride(overrideEntity)
                syncDb.lwwMutationDao().upsertPendingMutation(mutationEntity)
            }
        }
    }

    fun snapshotPendingMutations(): List<TagMutationRecord> {
        if (!loaded && !isMainThread()) {
            ensureLoadedOnBackground()
        }
        return synchronized(lock) { pendingMutationsList.toList() }
    }

    fun removeSyncedMutations(synced: List<TagMutationRecord>) {
        if (synced.isEmpty()) return
        val snapshot: List<TagMutationRecord>
        val remaining: Int
        synchronized(lock) {
            pendingMutationsList.removeAll(synced.toSet())
            snapshot = pendingMutationsList.toList()
            remaining = snapshot.size
        }
        _pendingMutationCount.value = remaining
        ioScope.launch { writePendingMutationsSnapshotToDb(snapshot) }
    }

    fun loadPairedHost(): PairedHostInfo? {
        if (isMainThread()) {
            warmUpAsync()
            return cachedPairedHost
        }
        return loadPairedHostFromDb().also {
            cachedPairedHost = it
            pairedHostLoaded = true
        }
    }

    private fun loadPairedHostFromDb(): PairedHostInfo? {
        val kv = syncDb.syncKvDao()
        val url = kv.getValue(KEY_PAIRED_HOST_URL)?.takeIf { it.isNotBlank() } ?: return null
        val storedToken = kv.getValue(KEY_PAIRED_TOKEN)?.takeIf { it.isNotBlank() } ?: return null
        val token = SecretBox.decrypt(storedToken)
        if (token.isBlank()) return null
        if (!SecretBox.isEncrypted(storedToken)) {
            runCatching {
                kv.putValue(SyncKvEntity(KEY_PAIRED_TOKEN, SecretBox.encrypt(token)))
            }
        }
        val hostName = kv.getValue(KEY_PAIRED_HOST_NAME) ?: url
        val deviceId = kv.getValue(KEY_PAIRED_DEVICE_ID) ?: getOrCreateStableDeviceId()
        val hostId = kv.getValue(KEY_PAIRED_HOST_ID).orEmpty()
        return PairedHostInfo(url, hostName, deviceId, token, hostId)
    }

    fun savePairedHost(info: PairedHostInfo) {
        cachedPairedHost = info
        pairedHostLoaded = true
        ioScope.launch {
            val encryptedToken = SecretBox.encrypt(info.token)
            syncDb.runInTransaction {
                val kv = syncDb.syncKvDao()
                kv.putValue(SyncKvEntity(KEY_PAIRED_HOST_URL, info.baseUrl))
                kv.putValue(SyncKvEntity(KEY_PAIRED_HOST_NAME, info.hostName))
                kv.putValue(SyncKvEntity(KEY_PAIRED_DEVICE_ID, info.deviceId))
                kv.putValue(SyncKvEntity(KEY_PAIRED_TOKEN, encryptedToken))
                kv.putValue(SyncKvEntity(KEY_PAIRED_HOST_ID, info.hostId))
            }
        }
    }

    fun clearPairedHost() {
        cachedPairedHost = null
        pairedHostLoaded = true
        ioScope.launch {
            syncDb.runInTransaction {
                val kv = syncDb.syncKvDao()
                kv.putValue(SyncKvEntity(KEY_PAIRED_TOKEN, ""))
                kv.putValue(SyncKvEntity(KEY_PAIRED_HOST_ID, ""))
            }
        }
    }

    fun getCachedStableDeviceId(): String {
        cachedStableDeviceId?.let { return it }
        if (!isMainThread()) {
            return getOrCreateStableDeviceId()
        }
        return (Build.MODEL ?: "android").replace(Regex("[^A-Za-z0-9._-]+"), "-").take(20)
    }

    fun getOrCreateStableDeviceId(): String {
        cachedStableDeviceId?.let { return it }
        val kv = syncDb.syncKvDao()
        kv.getValue(KEY_STABLE_DEVICE_ID)?.takeIf { it.isNotBlank() }?.let {
            cachedStableDeviceId = it
            return it
        }
        val modelPrefix =
            (Build.MODEL ?: "android").replace(Regex("[^A-Za-z0-9._-]+"), "-").take(20)
        val generated = "${modelPrefix}_${UUID.randomUUID().toString().replace("-", "").take(10)}"
        kv.putValue(SyncKvEntity(KEY_STABLE_DEVICE_ID, generated))
        cachedStableDeviceId = generated
        return generated
    }

    fun loadPlaylistMergeBase(name: String): List<String> {
        val raw = syncDb.syncKvDao().getValue("m3u8_base_$name") ?: return emptyList()
        return runCatching {
                @Suppress("UNCHECKED_CAST")
                val arr = TonewiseCore.parseJson(raw) as? List<Any?> ?: return emptyList()
                arr.mapNotNull { it?.toString() }
            }
            .getOrDefault(emptyList())
    }

    fun savePlaylistMergeBase(name: String, lines: List<String>) {
        syncDb.syncKvDao()
            .putValue(SyncKvEntity("m3u8_base_$name", TonewiseCore.stringifyJson(lines)))
    }

    fun clearPlaylistMergeBase(name: String) {
        syncDb.syncKvDao().deleteKey("m3u8_base_$name")
    }

    fun loadManagedPlaylistNames(): Set<String> {
        val raw = syncDb.syncKvDao().getValue(KEY_MANAGED_PLAYLISTS) ?: return emptySet()
        return runCatching {
                @Suppress("UNCHECKED_CAST")
                val arr = TonewiseCore.parseJson(raw) as? List<Any?> ?: return emptySet()
                arr.mapNotNull { it?.toString()?.trim()?.takeIf { s -> s.isNotEmpty() } }.toSet()
            }
            .getOrDefault(emptySet())
    }

    fun saveManagedPlaylistNames(names: Set<String>) {
        val sorted = names.map { it.trim() }.filter { it.isNotEmpty() }.distinct().sorted()
        syncDb.syncKvDao().putValue(SyncKvEntity(KEY_MANAGED_PLAYLISTS, TonewiseCore.stringifyJson(sorted)))
    }

    fun saveSceneOrderIds(ids: List<String>) {
        val copy = ids.toList()
        cachedSceneOrderIds = copy
        ioScope.launch {
            syncDb.syncKvDao().putValue(SyncKvEntity(KEY_SCENE_ORDER, TonewiseCore.stringifyJson(copy)))
        }
    }

    fun loadSceneOrderIds(): List<String> {
        cachedSceneOrderIds?.let { return it }
        if (isMainThread()) {
            warmUpAsync()
            return emptyList()
        }
        return readSceneOrderIdsFromDb().also {
            if (cachedSceneOrderIds == null) cachedSceneOrderIds = it
        }
    }

    private fun readSceneOrderIdsFromDb(): List<String> {
        val raw = syncDb.syncKvDao().getValue(KEY_SCENE_ORDER) ?: return emptyList()
        return runCatching {
                @Suppress("UNCHECKED_CAST")
                val arr = TonewiseCore.parseJson(raw) as? List<Any?> ?: return emptyList()
                arr.mapNotNull { it?.toString() }
            }
            .getOrDefault(emptyList())
    }

    fun applySceneOrder(list: List<SceneBanner>): List<SceneBanner> {
        val savedIds = loadSceneOrderIds()
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

    private fun overrideKey(uid: String, field: String): String = "$uid||$field"

    private fun loadLwwOverridesFromDb(): Map<String, OverrideRecord> =
        syncDb.lwwMutationDao().getAllOverrides().associate {
            overrideKey(it.trackUid, it.field) to OverrideRecord(it.value, it.updatedAtMs)
        }

    private fun writePendingMutationsSnapshotToDb(snapshot: List<TagMutationRecord>) {
        val dao = syncDb.lwwMutationDao()
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

    private fun loadPendingMutationsFromDb(): List<TagMutationRecord> =
        syncDb.lwwMutationDao().getAllPendingMutations().map {
            TagMutationRecord(
                trackUid = it.trackUid,
                field = it.field,
                value = it.value,
                updatedAtMs = it.updatedAtMs,
                deviceId = it.deviceId,
            )
        }

    private companion object {
        const val KEY_SCENE_ORDER = "scene_order_ids"
        const val KEY_PAIRED_HOST_URL = "lan_v2_host_url"
        const val KEY_PAIRED_HOST_NAME = "lan_v2_host_name"
        const val KEY_PAIRED_DEVICE_ID = "lan_v2_device_id"
        const val KEY_PAIRED_TOKEN = "lan_v2_token"
        const val KEY_PAIRED_HOST_ID = "lan_v2_host_id"
        const val KEY_STABLE_DEVICE_ID = "lan_v2_stable_device_id"
        const val KEY_MANAGED_PLAYLISTS = "managed_outbox_playlists"
    }
}
