/*
 * Copyright (c) 2026 Tonewise Project
 * QuickplayPinStore.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.home.quickplay

import android.content.Context
import android.os.Looper
import com.shawnshi.tonewise.sync.SyncKvEntity
import com.shawnshi.tonewise.sync.TonewiseSyncDatabase
import dagger.hilt.android.qualifiers.ApplicationContext
import java.util.concurrent.Executors
import javax.inject.Inject
import javax.inject.Singleton
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.asCoroutineDispatcher
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

enum class QuickplayPinKind(val wire: String) {
    ALBUM("album"),
    ARTIST("artist"),
    PLAYLIST("playlist"),
    WORK("work"),
    SONG("song");

    companion object {
        fun fromWire(value: String?): QuickplayPinKind? =
            entries.firstOrNull { it.wire.equals(value?.trim(), ignoreCase = true) }
    }
}

data class QuickplayPinRecord(
    val kind: QuickplayPinKind,
    val id: String,
    val title: String,
    val subtitle: String,
    val pinnedAtMs: Long,
    val coverId: String = "",
)

/**
 * Pure Kotlin JSON codec for [QuickplayPinRecord] so unit tests execute cleanly on pure JVM
 * without relying on android.org.json runtime stubs.
 */
object QuickplayPinCodec {
    private const val MAX_PINS = 24

    fun serializePins(pins: List<QuickplayPinRecord>): String {
        val capped = pins.take(MAX_PINS)
        val sb = StringBuilder(capped.size * 96 + 4)
        sb.append('[')
        for ((index, pin) in capped.withIndex()) {
            if (index > 0) sb.append(',')
            sb.append('{')
            QuickplayJsonCodecSupport.appendJsonField(sb, "kind", pin.kind.wire, comma = false)
            QuickplayJsonCodecSupport.appendJsonField(sb, "id", pin.id, comma = true)
            QuickplayJsonCodecSupport.appendJsonField(sb, "title", pin.title, comma = true)
            QuickplayJsonCodecSupport.appendJsonField(sb, "subtitle", pin.subtitle, comma = true)
            if (pin.coverId.isNotEmpty()) {
                QuickplayJsonCodecSupport.appendJsonField(sb, "coverId", pin.coverId, comma = true)
            }
            sb.append(",\"pinnedAtMs\":").append(pin.pinnedAtMs)
            sb.append('}')
        }
        sb.append(']')
        return sb.toString()
    }

    fun deserializePins(raw: String?): List<QuickplayPinRecord> {
        if (raw.isNullOrBlank()) return emptyList()
        val trimmed = raw.trim()
        val objects = QuickplayJsonCodecSupport.splitArrayObjects(trimmed)
        if (objects.isEmpty()) return emptyList()
        val out = mutableListOf<QuickplayPinRecord>()
        for (obj in objects) {
            parseObject(obj)?.let(out::add)
            if (out.size >= MAX_PINS) break
        }
        return out
    }

    private fun parseObject(obj: String): QuickplayPinRecord? {
        val fields = QuickplayJsonCodecSupport.parseFlatObject(obj)
        val kind = QuickplayPinKind.fromWire(fields["kind"]) ?: return null
        val id = fields["id"]?.takeIf { it.isNotEmpty() } ?: return null
        val title = fields["title"].orEmpty()
        val subtitle = fields["subtitle"].orEmpty()
        val coverId = fields["coverId"].orEmpty()
        val pinnedAtMs = fields["pinnedAtMs"]?.toLongOrNull() ?: 0L
        return QuickplayPinRecord(
            kind = kind,
            id = id,
            title = title,
            subtitle = subtitle,
            pinnedAtMs = pinnedAtMs,
            coverId = coverId,
        )
    }
}

/**
 * Persists user-pinned Quickplay items inside existing [TonewiseSyncDatabase.syncKvDao]
 * so Room DB schema version 7 remains unchanged.
 */
@Singleton
class QuickplayPinStore @Inject constructor(@ApplicationContext private val context: Context) {
    private val syncDb: TonewiseSyncDatabase by lazy { TonewiseSyncDatabase.getInstance(context) }
    private val dbDispatcher =
        Executors.newSingleThreadExecutor { runnable ->
                Thread(runnable, "Tonewise-QuickplayPinsDb").apply { isDaemon = true }
            }
            .asCoroutineDispatcher()
    private val ioScope = CoroutineScope(SupervisorJob() + dbDispatcher)
    private val stateLock = Any()
    @Volatile private var dbLoaded = false
    @Volatile private var dirty = false

    private val _pins = MutableStateFlow<List<QuickplayPinRecord>>(emptyList())
    val pins: StateFlow<List<QuickplayPinRecord>> = _pins.asStateFlow()

    init {
        ioScope.launch { ensureLoaded() }
    }

    fun ensureLoaded() {
        if (dbLoaded) return
        if (Looper.myLooper() == Looper.getMainLooper()) {
            ioScope.launch { ensureLoaded() }
            return
        }
        synchronized(stateLock) {
            if (dbLoaded) return
            syncDb.ensureLegacyMigrated(context)
            if (!dirty) {
                val raw = syncDb.syncKvDao().getValue(KEY_QUICKPLAY_PINS)
                _pins.value = QuickplayPinCodec.deserializePins(raw)
            }
            dbLoaded = true
        }
    }

    fun isPinned(kind: QuickplayPinKind, id: String): Boolean =
        _pins.value.any { it.kind == kind && it.id == id }

    /**
     * Toggles [record] pin state and returns true if now pinned, false if removed.
     */
    fun togglePin(
        kind: QuickplayPinKind,
        id: String,
        title: String,
        subtitle: String,
        coverId: String = "",
        nowMs: Long = System.currentTimeMillis(),
    ): Boolean {
        val added: Boolean
        val next: List<QuickplayPinRecord>
        synchronized(stateLock) {
            dirty = true
            val current = _pins.value
            val exists = current.any { it.kind == kind && it.id == id }
            if (exists) {
                next = current.filterNot { it.kind == kind && it.id == id }
                added = false
            } else {
                val item =
                    QuickplayPinRecord(
                        kind = kind,
                        id = id,
                        title = title,
                        subtitle = subtitle,
                        pinnedAtMs = nowMs,
                        coverId = coverId,
                    )
                next = (listOf(item) + current).take(24)
                added = true
            }
            _pins.value = next
        }
        persistAsync(next)
        return added
    }

    fun backfillCoverIds(resolved: Map<Pair<QuickplayPinKind, String>, String>) {
        if (resolved.isEmpty()) return
        val updated: List<QuickplayPinRecord>?
        synchronized(stateLock) {
            val current = _pins.value
            var changed = false
            val next =
                current.map { pin ->
                    val resolvedCover = resolved[pin.kind to pin.id]
                    if (!resolvedCover.isNullOrEmpty() && resolvedCover != pin.coverId) {
                        changed = true
                        pin.copy(coverId = resolvedCover)
                    } else {
                        pin
                    }
                }
            if (changed) {
                dirty = true
                _pins.value = next
                updated = next
            } else {
                updated = null
            }
        }
        if (updated != null) {
            persistAsync(updated)
        }
    }

    fun removePin(kind: QuickplayPinKind, id: String) {
        val next: List<QuickplayPinRecord>
        synchronized(stateLock) {
            dirty = true
            next = _pins.value.filterNot { it.kind == kind && it.id == id }
            _pins.value = next
        }
        persistAsync(next)
    }

    private fun persistAsync(pins: List<QuickplayPinRecord>) {
        val json = QuickplayPinCodec.serializePins(pins)
        ioScope.launch {
            syncDb.ensureLegacyMigrated(context)
            syncDb.syncKvDao().putValue(SyncKvEntity(KEY_QUICKPLAY_PINS, json))
        }
    }

    companion object {
        const val KEY_QUICKPLAY_PINS = "quickplay_pins_json"
    }
}
