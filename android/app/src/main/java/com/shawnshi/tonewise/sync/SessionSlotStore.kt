/*
 * Copyright (c) 2026 Tonewise Project
 * SessionSlotStore.kt is part of Tonewise.
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
import android.os.Looper
import dagger.hilt.android.qualifiers.ApplicationContext
import java.util.concurrent.Executors
import javax.inject.Inject
import javax.inject.Singleton
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.asCoroutineDispatcher
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import org.oxycblt.musikr.core.RawJson
import org.oxycblt.musikr.core.TonewiseCore

data class SessionSlotItem(
    val slotIndex: Int,
    val name: String,
    val sceneId: String?,
    val updatedAtMs: Long,
    val queueTrackUids: List<String>,
    val currentIndex: Int,
    val positionMs: Long,
)

@Singleton
class SessionSlotStore @Inject constructor(@ApplicationContext private val context: Context) {
    private val syncDb: TonewiseSyncDatabase by lazy { TonewiseSyncDatabase.getInstance(context) }
    private val dbDispatcher =
        Executors.newSingleThreadExecutor { runnable ->
                Thread(runnable, "Tonewise-SessionSlotsDb").apply { isDaemon = true }
            }
            .asCoroutineDispatcher()
    private val ioScope = CoroutineScope(SupervisorJob() + dbDispatcher)
    private val stateLock = Any()
    @Volatile private var dbLoaded = false
    @Volatile private var slotsDirty = false
    @Volatile private var activeSceneDirty = false
    @Volatile private var activeSlotDirty = false

    private val _slots = MutableStateFlow<List<SessionSlotItem>>(emptyList())
    val slots: StateFlow<List<SessionSlotItem>> = _slots

    private val _activeSceneLabel = MutableStateFlow("Default")
    val activeSceneLabel: StateFlow<String> = _activeSceneLabel

    private val _activeSlotIndex = MutableStateFlow(1)
    val activeSlotIndex: StateFlow<Int> = _activeSlotIndex

    init {
        ioScope.launch { ensureLoaded() }
    }

    fun ensureLoaded() {
        if (dbLoaded) return
        if (isMainThread()) {
            ioScope.launch { ensureLoaded() }
            return
        }
        synchronized(stateLock) {
            if (dbLoaded) return
            syncDb.ensureLegacyMigrated(context)
            val kv = syncDb.syncKvDao()
            if (!slotsDirty) {
                val raw = kv.getValue(KEY_SLOTS_JSON)
                if (!raw.isNullOrBlank()) {
                    _slots.value = runCatching { parseSlots(raw) }.getOrDefault(emptyList())
                }
            }
            if (!activeSceneDirty) {
                val scene = kv.getValue(KEY_ACTIVE_SCENE)
                if (!scene.isNullOrBlank()) {
                    _activeSceneLabel.value = scene
                }
            }
            if (!activeSlotDirty) {
                val slotIdx = kv.getValue(KEY_ACTIVE_SLOT)?.toIntOrNull()?.coerceIn(1, 5)
                if (slotIdx != null) {
                    _activeSlotIndex.value = slotIdx
                }
            }
            dbLoaded = true
        }
    }

    fun setActiveScene(sceneLabel: String, slotIndex: Int = _activeSlotIndex.value) {
        val cleanLabel = sceneLabel.trim().ifEmpty { "Default" }
        val cleanSlot = slotIndex.coerceIn(1, 5)
        synchronized(stateLock) {
            activeSceneDirty = true
            activeSlotDirty = true
            _activeSceneLabel.value = cleanLabel
            _activeSlotIndex.value = cleanSlot
        }
        ioScope.launch {
            syncDb.ensureLegacyMigrated(context)
            syncDb.syncKvDao().putValue(SyncKvEntity(KEY_ACTIVE_SCENE, cleanLabel))
            syncDb.syncKvDao().putValue(SyncKvEntity(KEY_ACTIVE_SLOT, cleanSlot.toString()))
        }
    }

    fun saveSlot(slot: SessionSlotItem, activate: Boolean = true) {
        val merged =
            synchronized(stateLock) {
                _slots.value
                    .filterNot { it.slotIndex == slot.slotIndex }
                    .plus(slot)
                    .sortedBy { it.slotIndex }
            }
        persist(merged)
        if (activate) {
            setActiveScene(slot.name, slot.slotIndex)
        }
    }

    fun cycleNextPopulatedSlot(): SessionSlotItem? {
        val populated = _slots.value.filter { isSlotPopulated(it) }.sortedBy { it.slotIndex }
        if (populated.isEmpty()) return null
        val currentIdx = _activeSlotIndex.value
        val next = populated.firstOrNull { it.slotIndex > currentIdx } ?: populated.first()
        setActiveScene(next.name, next.slotIndex)
        return next
    }

    fun mergeRemoteJson(remoteJson: String): List<SessionSlotItem> {
        ensureLoaded()
        val merged =
            synchronized(stateLock) {
                val next = mergeSlotsJson(_slots.value, remoteJson)
                slotsDirty = true
                _slots.value = next
                next
            }
        ioScope.launch {
            syncDb.ensureLegacyMigrated(context)
            val serialized = serializeSlots(merged)
            syncDb.syncKvDao().putValue(SyncKvEntity(KEY_SLOTS_JSON, serialized))
        }
        return merged
    }

    fun exportSlotsJson(): String {
        ensureLoaded()
        return serializeSlots(_slots.value)
    }

    private fun persist(list: List<SessionSlotItem>) {
        synchronized(stateLock) {
            slotsDirty = true
            _slots.value = list
        }
        ioScope.launch {
            syncDb.ensureLegacyMigrated(context)
            val serialized = serializeSlots(list)
            syncDb.syncKvDao().putValue(SyncKvEntity(KEY_SLOTS_JSON, serialized))
        }
    }

    private fun isMainThread(): Boolean {
        val mainLooper = runCatching { Looper.getMainLooper() }.getOrNull() ?: return false
        return mainLooper.thread === Thread.currentThread()
    }

    companion object {
        private const val KEY_SLOTS_JSON = "session_slots_json"
        private const val KEY_ACTIVE_SCENE = "session_active_scene_label"
        private const val KEY_ACTIVE_SLOT = "session_active_slot_index"

        fun isSlotPopulated(slot: SessionSlotItem?): Boolean =
            slot != null && slot.queueTrackUids.isNotEmpty()

        fun serializeSlots(list: List<SessionSlotItem>): String =
            TonewiseCore.stringifyJson(
                list.map { s ->
                    linkedMapOf(
                        "slot_index" to s.slotIndex,
                        "name" to s.name,
                        "scene_id" to s.sceneId,
                        "updated_at_ms" to s.updatedAtMs,
                        "queue_track_uids" to s.queueTrackUids,
                        "current_index" to s.currentIndex,
                        "position_ms" to s.positionMs,
                    )
                }
            )

        fun parseSlots(json: String): List<SessionSlotItem> {
            val parsed = TonewiseCore.parseJson(json) as? List<*> ?: return emptyList()
            val out = ArrayList<SessionSlotItem>(parsed.size)
            for (item in parsed) {
                @Suppress("UNCHECKED_CAST")
                val o = item as? Map<String, Any?> ?: continue
                val rawUids = o["queue_track_uids"] as? List<*> ?: emptyList<Any?>()
                val uids = rawUids.mapNotNull { it?.toString()?.trim()?.takeIf { s -> s.isNotEmpty() } }
                val slotIndex = (o["slot_index"] as? Number)?.toInt() ?: continue
                out.add(
                    SessionSlotItem(
                        slotIndex = slotIndex,
                        name = o["name"]?.toString()?.takeIf { it.isNotEmpty() } ?: "Session #$slotIndex",
                        sceneId = o["scene_id"]?.toString()?.takeIf { it.isNotEmpty() && it != "null" },
                        updatedAtMs = (o["updated_at_ms"] as? Number)?.toLong() ?: 0L,
                        queueTrackUids = uids,
                        currentIndex = (o["current_index"] as? Number)?.toInt() ?: 0,
                        positionMs = (o["position_ms"] as? Number)?.toLong() ?: 0L,
                    )
                )
            }
            return out.sortedBy { it.slotIndex }
        }

        fun mergeSlotsJson(local: List<SessionSlotItem>, remoteJson: String): List<SessionSlotItem> {
            val trimmedRemote = remoteJson.trim().ifEmpty { "[]" }
            val req =
                TonewiseCore.stringifyJson(
                    linkedMapOf(
                        "local" to RawJson(serializeSlots(local)),
                        "remote" to RawJson(trimmedRemote),
                    )
                )
            val mergedJson = TonewiseCore.mergeSessionsJson(req)
            return parseSlots(mergedJson)
        }
    }
}
