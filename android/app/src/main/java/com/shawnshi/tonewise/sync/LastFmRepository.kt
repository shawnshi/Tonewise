/*
 * Copyright (c) 2026 Tonewise Project
 * LastFmRepository.kt is part of Tonewise.
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
import android.content.Intent
import androidx.core.content.edit
import dagger.hilt.android.qualifiers.ApplicationContext
import java.net.HttpURLConnection
import java.net.URL
import java.net.URLEncoder
import java.security.MessageDigest
import javax.inject.Inject
import javax.inject.Singleton
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import com.shawnshi.tonewise.music.MusicRepository
import com.shawnshi.tonewise.music.resolve
import com.shawnshi.tonewise.playback.persist.PlayCountRepository
import org.json.JSONArray
import org.json.JSONObject
import org.oxycblt.musikr.Song
import org.oxycblt.musikr.tag.Name
import timber.log.Timber as L

data class LastFmState(
    val apiEnabled: Boolean = false,
    val broadcastEnabled: Boolean = true,
    val apiKey: String = "",
    val sharedSecret: String = "",
    val username: String = "",
    val sessionKey: String = "",
    val pendingCount: Int = 0,
    val syncedPlayCountTotal: Int = 0,
    val statusMessage: String = "Last.fm ready (system scrobble broadcast enabled)",
) {
    val hasValidApiSession: Boolean
        get() = apiKey.isNotBlank() && sharedSecret.isNotBlank() && sessionKey.isNotBlank()
}

data class ScrobbleEntry(
    val songUid: String,
    val artist: String,
    val track: String,
    val album: String,
    val durationSec: Int,
    val timestampSec: Long,
)

/**
 * Manages Last.fm Web API v2.0 authentication, live Now Playing & Scrobble submission, Android
 * system scrobbler broadcast intents (`com.adam.aslfms`), and bulk local play-history sync.
 */
@Singleton
class LastFmRepository
@Inject
constructor(
    @ApplicationContext private val context: Context,
    private val playCountRepository: PlayCountRepository,
    private val musicRepository: MusicRepository,
) {
    private val prefs = context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
    private val ioScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    private val pendingQueue = loadPendingQueue().toMutableList()
    private val syncedCounts = loadSyncedCounts().toMutableMap()

    private val _state = MutableStateFlow(snapshotState("Ready"))
    val state: StateFlow<LastFmState> = _state

    @Synchronized
    fun updateConfig(
        apiEnabled: Boolean,
        broadcastEnabled: Boolean,
        apiKey: String,
        sharedSecret: String,
        username: String,
        sessionKey: String,
    ) {
        val cleanKey = apiKey.trim()
        val cleanSecret = sharedSecret.trim()
        val cleanUser = username.trim()
        val cleanSk = sessionKey.trim()
        prefs.edit {
            putBoolean(KEY_API_ENABLED, apiEnabled)
            putBoolean(KEY_BROADCAST_ENABLED, broadcastEnabled)
            putString(KEY_API_KEY, cleanKey)
            putString(KEY_SHARED_SECRET, cleanSecret)
            putString(KEY_USERNAME, cleanUser)
            putString(KEY_SESSION_KEY, cleanSk)
        }
        val msg =
            when {
                apiEnabled && cleanKey.isNotEmpty() && cleanSecret.isNotEmpty() && cleanSk.isNotEmpty() ->
                    "Last.fm API active (${cleanUser.ifEmpty { "session" }})"
                broadcastEnabled -> "Android Scrobble broadcast active"
                else -> "Last.fm sync paused"
            }
        _state.value = snapshotState(msg)
    }

    suspend fun authenticateMobileSession(
        apiKey: String,
        sharedSecret: String,
        username: String,
        password: String,
    ): Boolean =
        withContext(Dispatchers.IO) {
            val cleanKey = apiKey.trim()
            val cleanSecret = sharedSecret.trim()
            val cleanUser = username.trim()
            if (cleanKey.isEmpty() || cleanSecret.isEmpty() || cleanUser.isEmpty() || password.isEmpty()) {
                _state.value = snapshotState("Enter API Key, Shared Secret, Username & Password")
                return@withContext false
            }

            _state.value = snapshotState("Authenticating with Last.fm...")
            try {
                val signedParams =
                    sortedMapOf(
                        "api_key" to cleanKey,
                        "method" to "auth.getMobileSession",
                        "password" to password,
                        "username" to cleanUser,
                    )
                val sig = buildApiSignature(signedParams, cleanSecret)
                val postParams = signedParams.toMutableMap().apply {
                    put("api_sig", sig)
                    put("format", "json")
                }
                val raw = postForm(LASTFM_API_URL, postParams)
                val json = JSONObject(raw)
                val sessionObj = json.optJSONObject("session")
                val sk = sessionObj?.optString("key")?.takeIf { it.isNotBlank() }
                if (sk == null) {
                    val errMsg = json.optString("message").ifBlank { "Auth failed" }
                    _state.value = snapshotState("Last.fm error: $errMsg")
                    return@withContext false
                }
                val resolvedUser = sessionObj.optString("name").ifBlank { cleanUser }
                updateConfig(
                    apiEnabled = true,
                    broadcastEnabled = _state.value.broadcastEnabled,
                    apiKey = cleanKey,
                    sharedSecret = cleanSecret,
                    username = resolvedUser,
                    sessionKey = sk,
                )
                true
            } catch (e: Exception) {
                L.w(e, "Last.fm auth failed")
                _state.value = snapshotState("Last.fm auth failed: ${e.message}")
                false
            }
        }

    fun onNowPlaying(song: Song) {
        val current = _state.value
        val entry = song.toScrobbleEntry(System.currentTimeMillis() / 1000L)
        if (current.broadcastEnabled) {
            sendAslfmsBroadcast(ASLFMS_STATE_START, entry)
        }
        if (current.apiEnabled && current.hasValidApiSession) {
            ioScope.launch { submitNowPlaying(current, entry) }
        }
    }

    fun onSongPlayed(song: Song, timestampSec: Long = System.currentTimeMillis() / 1000L) {
        val current = _state.value
        val entry = song.toScrobbleEntry(timestampSec)
        if (current.broadcastEnabled) {
            sendAslfmsBroadcast(ASLFMS_STATE_COMPLETE, entry)
        }

        synchronized(this) {
            val uid = song.uid.toString()
            val localTotal = playCountRepository.getPlayCount(song)
            syncedCounts[uid] = maxOf(syncedCounts[uid] ?: 0, localTotal)
            saveSyncedCounts()

            if (current.apiEnabled) {
                pendingQueue.add(entry)
                savePendingQueue()
                _state.value = snapshotState("Queued scrobble: ${entry.track}")
            } else {
                _state.value = snapshotState(current.statusMessage)
            }
        }

        if (current.apiEnabled && current.hasValidApiSession) {
            ioScope.launch { flushPendingScrobbles() }
        }
    }

    /**
     * Computes unsynced delta counts between [PlayCountRepository] and `syncedCounts`, enqueues them,
     * and submits pending scrobbles in Last.fm batches of 50 (or emits system broadcasts if API
     * sync is disabled).
     */
    suspend fun syncLocalPlayHistory(): Int =
        withContext(Dispatchers.IO) {
            val songs = musicRepository.library?.songs.orEmpty()
            val nowSec = System.currentTimeMillis() / 1000L
            val newlyQueued = mutableListOf<ScrobbleEntry>()
            val current = _state.value

            synchronized(this@LastFmRepository) {
                var syntheticOffsetSec = 30L
                for (song in songs) {
                    val totalLocal = playCountRepository.getPlayCount(song)
                    val already = syncedCounts[song.uid.toString()] ?: 0
                    val delta = (totalLocal - already).coerceAtLeast(0)
                    if (delta <= 0) continue

                    val trackSpacing = maxOf(song.durationSecCompat(), 180L)
                    for (step in 0 until delta) {
                        val ts = (nowSec - syntheticOffsetSec).coerceAtLeast(nowSec - MAX_BACKFILL_WINDOW_SEC)
                        newlyQueued.add(song.toScrobbleEntry(ts))
                        syntheticOffsetSec += trackSpacing
                    }
                    syncedCounts[song.uid.toString()] = totalLocal
                }
                if (newlyQueued.isNotEmpty()) {
                    saveSyncedCounts()
                    if (current.apiEnabled || current.hasValidApiSession) {
                        pendingQueue.addAll(newlyQueued)
                        savePendingQueue()
                    }
                }
            }

            if (current.apiEnabled || current.hasValidApiSession) {
                if (!current.hasValidApiSession) {
                    _state.value =
                        snapshotState(
                            "Queued ${pendingQueue.size} local plays; configure Last.fm session to upload"
                        )
                    return@withContext newlyQueued.size
                }
                return@withContext flushPendingScrobbles()
            } else if (current.broadcastEnabled && newlyQueued.isNotEmpty()) {
                for (entry in newlyQueued) {
                    sendAslfmsBroadcast(ASLFMS_STATE_COMPLETE, entry)
                }
                _state.value =
                    snapshotState("Broadcast ${newlyQueued.size} local plays via Android Scrobbler")
                return@withContext newlyQueued.size
            } else {
                _state.value = snapshotState("No new local plays to sync")
                return@withContext 0
            }
        }

    suspend fun flushPendingScrobbles(): Int =
        withContext(Dispatchers.IO) {
            val cfg = _state.value
            if (!cfg.hasValidApiSession) return@withContext 0

            var totalSent = 0
            while (true) {
                val batch: List<ScrobbleEntry> =
                    synchronized(this@LastFmRepository) {
                        if (pendingQueue.isEmpty()) return@synchronized emptyList()
                        pendingQueue.take(MAX_BATCH_SIZE)
                    }
                if (batch.isEmpty()) break

                val ok = postScrobbleBatch(cfg, batch)
                if (!ok) break

                synchronized(this@LastFmRepository) {
                    val removeCount = minOf(batch.size, pendingQueue.size)
                    repeat(removeCount) { pendingQueue.removeAt(0) }
                    savePendingQueue()
                }
                totalSent += batch.size
            }

            if (totalSent > 0) {
                _state.value =
                    snapshotState("Synced $totalSent scrobbles (${pendingQueue.size} remaining)")
            }
            totalSent
        }

    private fun submitNowPlaying(cfg: LastFmState, entry: ScrobbleEntry) {
        try {
            val signed =
                sortedMapOf(
                    "api_key" to cfg.apiKey,
                    "artist" to entry.artist,
                    "method" to "track.updateNowPlaying",
                    "sk" to cfg.sessionKey,
                    "track" to entry.track,
                ).apply {
                    if (entry.album.isNotEmpty()) put("album", entry.album)
                    if (entry.durationSec > 0) put("duration", entry.durationSec.toString())
                }
            val sig = buildApiSignature(signed, cfg.sharedSecret)
            val body = signed.toMutableMap().apply {
                put("api_sig", sig)
                put("format", "json")
            }
            postForm(LASTFM_API_URL, body)
        } catch (e: Exception) {
            L.d("NowPlaying update failed: ${e.message}")
        }
    }

    private fun postScrobbleBatch(cfg: LastFmState, batch: List<ScrobbleEntry>): Boolean {
        return try {
            val signed =
                sortedMapOf(
                    "api_key" to cfg.apiKey,
                    "method" to "track.scrobble",
                    "sk" to cfg.sessionKey,
                )
            batch.forEachIndexed { idx, entry ->
                signed["artist[$idx]"] = entry.artist
                signed["track[$idx]"] = entry.track
                signed["timestamp[$idx]"] = entry.timestampSec.toString()
                if (entry.album.isNotEmpty()) {
                    signed["album[$idx]"] = entry.album
                }
                if (entry.durationSec > 0) {
                    signed["duration[$idx]"] = entry.durationSec.toString()
                }
            }
            val sig = buildApiSignature(signed, cfg.sharedSecret)
            val body = signed.toMutableMap().apply {
                put("api_sig", sig)
                put("format", "json")
            }
            val raw = postForm(LASTFM_API_URL, body)
            val json = JSONObject(raw)
            if (json.has("error")) {
                val msg = json.optString("message", "API error ${json.optInt("error")}")
                _state.value = snapshotState("Last.fm upload error: $msg")
                false
            } else {
                true
            }
        } catch (e: Exception) {
            L.w(e, "Last.fm batch scrobble failed")
            _state.value = snapshotState("Last.fm network error (kept pending): ${e.message}")
            false
        }
    }

    private fun sendAslfmsBroadcast(scrobbleState: Int, entry: ScrobbleEntry) {
        runCatching {
            val intent =
                Intent(ACTION_ASLFMS_PLAYSTATE).apply {
                    putExtra("state", scrobbleState)
                    putExtra("app-name", "Tonewise")
                    putExtra("app-package", context.packageName)
                    putExtra("artist", entry.artist)
                    putExtra("track", entry.track)
                    putExtra("album", entry.album)
                    putExtra("duration", entry.durationSec)
                }
            context.sendBroadcast(intent)
        }
    }

    private fun postForm(url: String, params: Map<String, String>): String {
        val encoded =
            params.entries.joinToString("&") { (k, v) ->
                "${URLEncoder.encode(k, "UTF-8")}=${URLEncoder.encode(v, "UTF-8")}"
            }
        val conn =
            (URL(url).openConnection() as HttpURLConnection).apply {
                connectTimeout = 8_000
                readTimeout = 15_000
                requestMethod = "POST"
                doOutput = true
                setRequestProperty("Content-Type", "application/x-www-form-urlencoded; charset=utf-8")
                setRequestProperty("User-Agent", "Tonewise/4.1.5 Android")
            }
        return try {
            conn.outputStream.use { it.write(encoded.toByteArray(Charsets.UTF_8)) }
            val stream =
                if (conn.responseCode in 200..299) conn.inputStream else (conn.errorStream ?: conn.inputStream)
            String(stream.readBytes(), Charsets.UTF_8)
        } finally {
            conn.disconnect()
        }
    }

    private fun Song.toScrobbleEntry(timestampSec: Long): ScrobbleEntry {
        val artistName =
            artists
                .mapNotNull { (it.name as? Name.Known)?.raw }
                .firstOrNull { it.isNotBlank() }
                ?: "Unknown Artist"
        val albumName = (album.name as? Name.Known)?.raw ?: album.name.resolve(context)
        return ScrobbleEntry(
            songUid = uid.toString(),
            artist = artistName,
            track = name.raw,
            album = albumName,
            durationSec = durationSecCompat().toInt(),
            timestampSec = timestampSec,
        )
    }

    private fun Song.durationSecCompat(): Long = (durationMs / 1000L).coerceAtLeast(30L)

    private fun snapshotState(statusMessage: String): LastFmState =
        LastFmState(
            apiEnabled = prefs.getBoolean(KEY_API_ENABLED, false),
            broadcastEnabled = prefs.getBoolean(KEY_BROADCAST_ENABLED, true),
            apiKey = prefs.getString(KEY_API_KEY, "") ?: "",
            sharedSecret = prefs.getString(KEY_SHARED_SECRET, "") ?: "",
            username = prefs.getString(KEY_USERNAME, "") ?: "",
            sessionKey = prefs.getString(KEY_SESSION_KEY, "") ?: "",
            pendingCount = pendingQueue.size,
            syncedPlayCountTotal = syncedCounts.values.sum(),
            statusMessage = statusMessage,
        )

    private fun savePendingQueue() {
        val arr = JSONArray()
        for (e in pendingQueue) {
            arr.put(
                JSONObject()
                    .put("uid", e.songUid)
                    .put("artist", e.artist)
                    .put("track", e.track)
                    .put("album", e.album)
                    .put("duration", e.durationSec)
                    .put("ts", e.timestampSec)
            )
        }
        prefs.edit { putString(KEY_PENDING_JSON, arr.toString()) }
    }

    private fun loadPendingQueue(): List<ScrobbleEntry> {
        val raw = prefs.getString(KEY_PENDING_JSON, null) ?: return emptyList()
        return runCatching {
                val arr = JSONArray(raw)
                List(arr.length()) { i ->
                    val o = arr.getJSONObject(i)
                    ScrobbleEntry(
                        songUid = o.getString("uid"),
                        artist = o.getString("artist"),
                        track = o.getString("track"),
                        album = o.optString("album", ""),
                        durationSec = o.optInt("duration", 0),
                        timestampSec = o.getLong("ts"),
                    )
                }
            }
            .getOrDefault(emptyList())
    }

    private fun saveSyncedCounts() {
        val obj = JSONObject()
        for ((k, v) in syncedCounts) {
            obj.put(k, v)
        }
        prefs.edit { putString(KEY_SYNCED_COUNTS_JSON, obj.toString()) }
    }

    private fun loadSyncedCounts(): Map<String, Int> {
        val raw = prefs.getString(KEY_SYNCED_COUNTS_JSON, null) ?: return emptyMap()
        return runCatching {
                val obj = JSONObject(raw)
                val map = HashMap<String, Int>(obj.length())
                val keys = obj.keys()
                while (keys.hasNext()) {
                    val key = keys.next()
                    map[key] = obj.optInt(key, 0)
                }
                map
            }
            .getOrDefault(emptyMap())
    }

    companion object {
        private const val PREFS_NAME = "tonewise_lastfm"
        private const val KEY_API_ENABLED = "api_enabled"
        private const val KEY_BROADCAST_ENABLED = "broadcast_enabled"
        private const val KEY_API_KEY = "api_key"
        private const val KEY_SHARED_SECRET = "shared_secret"
        private const val KEY_USERNAME = "username"
        private const val KEY_SESSION_KEY = "session_key"
        private const val KEY_PENDING_JSON = "pending_scrobbles_json"
        private const val KEY_SYNCED_COUNTS_JSON = "synced_counts_json"
        private const val LASTFM_API_URL = "https://ws.audioscrobbler.com/2.0/"
        private const val ACTION_ASLFMS_PLAYSTATE = "com.adam.aslfms.notify.playstatechanged"
        private const val ASLFMS_STATE_START = 0
        private const val ASLFMS_STATE_COMPLETE = 3
        private const val MAX_BATCH_SIZE = 50
        private const val MAX_BACKFILL_WINDOW_SEC = 13L * 24L * 3600L

        /**
         * Builds the official Last.fm Web API v2.0 `api_sig`:
         * MD5 of sorted UTF-8 `<key><value>` pairs (excluding `format` and `callback`) + shared secret.
         */
        fun buildApiSignature(params: Map<String, String>, secret: String): String {
            val base = buildString {
                params.entries
                    .filter { it.key != "format" && it.key != "callback" }
                    .sortedBy { it.key }
                    .forEach { (k, v) ->
                        append(k)
                        append(v)
                    }
                append(secret)
            }
            val digest = MessageDigest.getInstance("MD5").digest(base.toByteArray(Charsets.UTF_8))
            return digest.joinToString("") { "%02x".format(it) }
        }
    }
}
