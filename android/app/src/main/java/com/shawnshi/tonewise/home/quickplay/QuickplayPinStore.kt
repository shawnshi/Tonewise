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
            appendJsonField(sb, "kind", pin.kind.wire, comma = false)
            appendJsonField(sb, "id", pin.id, comma = true)
            appendJsonField(sb, "title", pin.title, comma = true)
            appendJsonField(sb, "subtitle", pin.subtitle, comma = true)
            sb.append(",\"pinnedAtMs\":").append(pin.pinnedAtMs)
            sb.append('}')
        }
        sb.append(']')
        return sb.toString()
    }

    fun deserializePins(raw: String?): List<QuickplayPinRecord> {
        if (raw.isNullOrBlank()) return emptyList()
        val trimmed = raw.trim()
        if (!trimmed.startsWith('[')) return emptyList()
        val out = mutableListOf<QuickplayPinRecord>()
        var idx = 1
        val len = trimmed.length
        while (idx < len) {
            while (idx < len && (trimmed[idx].isWhitespace() || trimmed[idx] == ',')) idx++
            if (idx >= len || trimmed[idx] == ']') break
            if (trimmed[idx] != '{') break
            val objStart = idx
            var inString = false
            var escaped = false
            var depth = 0
            while (idx < len) {
                val ch = trimmed[idx]
                if (escaped) {
                    escaped = false
                } else if (ch == '\\' && inString) {
                    escaped = true
                } else if (ch == '"') {
                    inString = !inString
                } else if (!inString) {
                    if (ch == '{') depth++
                    else if (ch == '}') {
                        depth--
                        if (depth == 0) {
                            idx++
                            break
                        }
                    }
                }
                idx++
            }
            if (depth == 0) {
                parseObject(trimmed.substring(objStart, idx))?.let(out::add)
            }
        }
        return out.take(MAX_PINS)
    }

    private fun parseObject(obj: String): QuickplayPinRecord? {
        val fields = mutableMapOf<String, String>()
        var i = 1
        val len = obj.length - 1
        while (i < len) {
            while (i < len && (obj[i].isWhitespace() || obj[i] == ',')) i++
            if (i >= len || obj[i] != '"') break
            val (key, afterKey) = readJsonString(obj, i) ?: break
            i = afterKey
            while (i < len && (obj[i].isWhitespace() || obj[i] == ':')) i++
            if (i >= len) break
            if (obj[i] == '"') {
                val (value, afterVal) = readJsonString(obj, i) ?: break
                fields[key] = value
                i = afterVal
            } else {
                val numStart = i
                while (i < len && obj[i] != ',' && obj[i] != '}') i++
                fields[key] = obj.substring(numStart, i).trim()
            }
        }
        val kind = QuickplayPinKind.fromWire(fields["kind"]) ?: return null
        val id = fields["id"]?.takeIf { it.isNotEmpty() } ?: return null
        val title = fields["title"].orEmpty()
        val subtitle = fields["subtitle"].orEmpty()
        val pinnedAtMs = fields["pinnedAtMs"]?.toLongOrNull() ?: 0L
        return QuickplayPinRecord(
            kind = kind,
            id = id,
            title = title,
            subtitle = subtitle,
            pinnedAtMs = pinnedAtMs,
        )
    }

    private fun readJsonString(src: String, startQuote: Int): Pair<String, Int>? {
        val sb = StringBuilder()
        var i = startQuote + 1
        val len = src.length
        while (i < len) {
            val ch = src[i++]
            when (ch) {
                '"' -> return sb.toString() to i
                '\\' -> {
                    if (i >= len) return null
                    when (val esc = src[i++]) {
                        '"' -> sb.append('"')
                        '\\' -> sb.append('\\')
                        '/' -> sb.append('/')
                        'b' -> sb.append('\b')
                        'f' -> sb.append('\u000C')
                        'n' -> sb.append('\n')
                        'r' -> sb.append('\r')
                        't' -> sb.append('\t')
                        'u' -> {
                            if (i + 4 > len) return null
                            val code = src.substring(i, i + 4).toIntOrNull(16) ?: return null
                            sb.append(code.toChar())
                            i += 4
                        }
                        else -> sb.append(esc)
                    }
                }
                else -> sb.append(ch)
            }
        }
        return null
    }

    private fun appendJsonField(sb: StringBuilder, key: String, value: String, comma: Boolean) {
        if (comma) sb.append(',')
        sb.append('"')
        appendEscaped(sb, key)
        sb.append("\":\"")
        appendEscaped(sb, value)
        sb.append('"')
    }

    private fun appendEscaped(sb: StringBuilder, value: String) {
        for (ch in value) {
            when (ch) {
                '"' -> sb.append("\\\"")
                '\\' -> sb.append("\\\\")
                '\n' -> sb.append("\\n")
                '\r' -> sb.append("\\r")
                '\t' -> sb.append("\\t")
                '\b' -> sb.append("\\b")
                '\u000C' -> sb.append("\\f")
                else -> if (ch.code < 0x20) sb.append("\\u%04x".format(ch.code)) else sb.append(ch)
            }
        }
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
                    )
                next = (listOf(item) + current).take(24)
                added = true
            }
            _pins.value = next
        }
        persistAsync(next)
        return added
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
