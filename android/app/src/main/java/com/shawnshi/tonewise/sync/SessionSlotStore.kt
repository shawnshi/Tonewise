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
import dagger.hilt.android.qualifiers.ApplicationContext
import javax.inject.Inject
import javax.inject.Singleton
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import org.json.JSONArray
import org.json.JSONObject
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
class SessionSlotStore @Inject constructor(@ApplicationContext context: Context) {
    private val syncDb: TonewiseSyncDatabase by lazy { TonewiseSyncDatabase.getInstance(context) }
    private val ioScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    private val _slots = MutableStateFlow(loadSlots())
    val slots: StateFlow<List<SessionSlotItem>> = _slots

    private val _activeSceneLabel =
        MutableStateFlow(syncDb.syncKvDao().getValue(KEY_ACTIVE_SCENE) ?: "Default")
    val activeSceneLabel: StateFlow<String> = _activeSceneLabel

    private val _activeSlotIndex =
        MutableStateFlow(
            (syncDb.syncKvDao().getValue(KEY_ACTIVE_SLOT)?.toIntOrNull() ?: 1).coerceIn(1, 5)
        )
    val activeSlotIndex: StateFlow<Int> = _activeSlotIndex

    fun setActiveScene(sceneLabel: String, slotIndex: Int = _activeSlotIndex.value) {
        val cleanLabel = sceneLabel.trim().ifEmpty { "Default" }
        val cleanSlot = slotIndex.coerceIn(1, 5)
        _activeSceneLabel.value = cleanLabel
        _activeSlotIndex.value = cleanSlot
        ioScope.launch {
            syncDb.syncKvDao().putValue(SyncKvEntity(KEY_ACTIVE_SCENE, cleanLabel))
            syncDb.syncKvDao().putValue(SyncKvEntity(KEY_ACTIVE_SLOT, cleanSlot.toString()))
        }
    }

    fun saveSlot(slot: SessionSlotItem) {
        val merged =
            _slots.value
                .filterNot { it.slotIndex == slot.slotIndex }
                .plus(slot)
                .sortedBy { it.slotIndex }
        persist(merged)
        setActiveScene(slot.name, slot.slotIndex)
    }

    fun cycleNextPopulatedSlot(): SessionSlotItem? {
        val populated = _slots.value.filter { it.queueTrackUids.isNotEmpty() }.sortedBy { it.slotIndex }
        if (populated.isEmpty()) return null
        val currentIdx = _activeSlotIndex.value
        val next = populated.firstOrNull { it.slotIndex > currentIdx } ?: populated.first()
        setActiveScene(next.name, next.slotIndex)
        return next
    }

    fun mergeRemoteJson(remoteJson: String): List<SessionSlotItem> {
        val localArr = toJsonArray(_slots.value)
        val req =
            JSONObject()
                .put("local", localArr)
                .put("remote", JSONArray(remoteJson))
                .toString()
        val mergedJson = TonewiseCore.mergeSessionsJson(req)
        val merged = parseSlots(mergedJson)
        persist(merged)
        return merged
    }

    fun exportSlotsJson(): String = toJsonArray(_slots.value).toString()

    private fun persist(list: List<SessionSlotItem>) {
        _slots.value = list
        ioScope.launch {
            val serialized = toJsonArray(list).toString()
            syncDb.syncKvDao().putValue(SyncKvEntity(KEY_SLOTS_JSON, serialized))
        }
    }

    private fun loadSlots(): List<SessionSlotItem> {
        val raw = syncDb.syncKvDao().getValue(KEY_SLOTS_JSON) ?: return emptyList()
        return runCatching { parseSlots(raw) }.getOrDefault(emptyList())
    }

    private fun toJsonArray(list: List<SessionSlotItem>): JSONArray {
        val arr = JSONArray()
        for (s in list) {
            val obj =
                JSONObject()
                    .put("slot_index", s.slotIndex)
                    .put("name", s.name)
                    .put("scene_id", s.sceneId)
                    .put("updated_at_ms", s.updatedAtMs)
                    .put("queue_track_uids", JSONArray(s.queueTrackUids))
                    .put("current_index", s.currentIndex)
                    .put("position_ms", s.positionMs)
            arr.put(obj)
        }
        return arr
    }

    private fun parseSlots(json: String): List<SessionSlotItem> {
        val arr = JSONArray(json)
        val out = ArrayList<SessionSlotItem>(arr.length())
        for (i in 0 until arr.length()) {
            val o = arr.getJSONObject(i)
            val uidsArr = o.optJSONArray("queue_track_uids") ?: JSONArray()
            val uids = List(uidsArr.length()) { idx -> uidsArr.getString(idx) }
            out.add(
                SessionSlotItem(
                    slotIndex = o.getInt("slot_index"),
                    name = o.getString("name"),
                    sceneId = o.optString("scene_id").takeIf { it.isNotEmpty() && it != "null" },
                    updatedAtMs = o.getLong("updated_at_ms"),
                    queueTrackUids = uids,
                    currentIndex = o.optInt("current_index", 0),
                    positionMs = o.optLong("position_ms", 0L),
                )
            )
        }
        return out.sortedBy { it.slotIndex }
    }

    private companion object {
        const val KEY_SLOTS_JSON = "session_slots_json"
        const val KEY_ACTIVE_SCENE = "session_active_scene_label"
        const val KEY_ACTIVE_SLOT = "session_active_slot_index"
    }
}
