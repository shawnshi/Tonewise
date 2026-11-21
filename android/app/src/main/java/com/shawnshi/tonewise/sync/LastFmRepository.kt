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
import java.net.URLEncoder
import java.security.MessageDigest
import kotlin.math.abs
import javax.inject.Inject
import javax.inject.Singleton
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import com.shawnshi.tonewise.BuildConfig
import com.shawnshi.tonewise.music.MusicRepository
import com.shawnshi.tonewise.music.resolve
import com.shawnshi.tonewise.playback.persist.PlayCountRepository
import org.json.JSONArray
import org.json.JSONObject
import org.oxycblt.musikr.Song
import org.oxycblt.musikr.core.TonewiseCore
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
    val pendingLoveCount: Int = 0,
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

data class PendingLoveOp(
    val songUid: String,
    val artist: String,
    val track: String,
    val loved: Boolean,
    val updatedAtMs: Long,
)

/**
 * Manages Last.fm Web API v2.0 authentication, live Now Playing & Scrobble submission, Android
 * system scrobbler broadcast intents (`com.adam.aslfms`), Loved track push (`track.love` /
 * `track.unlove`), and bidirectional play-history sync.
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
    private val syncDb: TonewiseSyncDatabase by lazy { TonewiseSyncDatabase.getInstance(context) }
    private val ioScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    private val scrobbleFlushMutex = Mutex()
    private val loveFlushMutex = Mutex()
    private val syncMutex = Mutex()
    private val dbInitLock = Any()
    @Volatile private var dbLoaded = false
    private val pendingQueue = mutableListOf<ScrobbleEntry>()
    private val pendingLoveOps = mutableListOf<PendingLoveOp>()
    private val pulledCloudCounts = mutableMapOf<String, Int>()
    private val seenCloudStamps = LinkedHashSet<String>()
    @Volatile private var recentCloudPlaysCache: List<CloudPlayRecord> = emptyList()

    private val _state = MutableStateFlow(snapshotState("Ready"))
    val state: StateFlow<LastFmState> = _state

    init {
        ioScope.launch {
            ensureDbLoaded()
            val refreshed = synchronized(this@LastFmRepository) { snapshotState("Ready") }
            _state.value = refreshed
            triggerBackgroundFlushIfNeeded()
        }
    }

    private fun isMainThread(): Boolean {
        val mainLooper = runCatching { android.os.Looper.getMainLooper() }.getOrNull() ?: return false
        return mainLooper.thread === Thread.currentThread()
    }

    private fun ensureDbLoaded() {
        if (dbLoaded) return
        if (isMainThread()) return
        synchronized(dbInitLock) {
            if (dbLoaded) return
            syncDb.ensureLegacyMigrated(context)
            val loadedQueue = loadPendingQueue()
            val loadedLoves = loadPendingLoveOps()
            val loadedPulled = loadPulledCloudCounts()
            val loadedStamps = loadSeenCloudStamps()
            synchronized(this@LastFmRepository) {
                if (loadedQueue.isNotEmpty()) {
                    val existingKeys =
                        pendingQueue.mapTo(HashSet()) { "${it.songUid}@${it.timestampSec}" }
                    val merged =
                        loadedQueue.filter { "${it.songUid}@${it.timestampSec}" !in existingKeys } +
                            pendingQueue
                    pendingQueue.clear()
                    pendingQueue.addAll(merged)
                }
                if (loadedLoves.isNotEmpty()) {
                    for (op in loadedLoves) {
                        val key = normalizeTrackKey(op.artist, op.track)
                        if (pendingLoveOps.none { normalizeTrackKey(it.artist, it.track) == key }) {
                            pendingLoveOps.add(0, op)
                        }
                    }
                }
                for ((k, v) in loadedPulled) {
                    pulledCloudCounts[k] = maxOf(pulledCloudCounts[k] ?: 0, v)
                }
                if (loadedStamps.isNotEmpty()) {
                    val combined = LinkedHashSet<String>(loadedStamps.size + seenCloudStamps.size)
                    combined.addAll(loadedStamps)
                    combined.addAll(seenCloudStamps)
                    seenCloudStamps.clear()
                    seenCloudStamps.addAll(combined)
                }
            }
            dbLoaded = true
        }
    }

    fun triggerBackgroundFlushIfNeeded() {
        val current = _state.value
        val hasPending =
            synchronized(this) {
                pendingQueue.isNotEmpty() || pendingLoveOps.isNotEmpty()
            }
        if (current.apiEnabled && current.hasValidApiSession && hasPending) {
            ioScope.launch {
                flushPendingLoveOps()
                flushPendingScrobbles()
            }
        }
    }

    @Synchronized
    fun updateConfig(
        apiEnabled: Boolean,
        broadcastEnabled: Boolean,
        apiKey: String,
        sharedSecret: String,
        username: String,
        sessionKey: String,
    ) {
        val hadValidSession = _state.value.hasValidApiSession
        val cleanKey = apiKey.trim()
        val cleanSecret = sharedSecret.trim()
        val cleanUser = username.trim()
        val cleanSk = sessionKey.trim()
        val hasValidSession =
            cleanKey.isNotEmpty() && cleanSecret.isNotEmpty() && cleanSk.isNotEmpty()
        val effectiveApiEnabled =
            resolveAutoEnabledApiFlag(
                requestedApiEnabled = apiEnabled,
                hadValidSession = hadValidSession,
                hasValidSession = hasValidSession,
            )
        prefs.edit {
            putBoolean(KEY_API_ENABLED, effectiveApiEnabled)
            putBoolean(KEY_BROADCAST_ENABLED, broadcastEnabled)
            putString(KEY_API_KEY, cleanKey)
            putString(KEY_SHARED_SECRET, SecretBox.encrypt(cleanSecret))
            putString(KEY_USERNAME, cleanUser)
            putString(KEY_SESSION_KEY, SecretBox.encrypt(cleanSk))
        }
        val msg =
            when {
                effectiveApiEnabled && hasValidSession ->
                    "Last.fm API active (${cleanUser.ifEmpty { "session" }})"
                broadcastEnabled -> "Android Scrobble broadcast active"
                else -> "Last.fm sync paused"
            }
        _state.value = snapshotState(msg)
        if (effectiveApiEnabled && hasValidSession) {
            ioScope.launch {
                flushPendingLoveOps()
                flushPendingScrobbles()
            }
        }
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
            validateCredentialsFormat(cleanKey, cleanSecret)?.let { err ->
                _state.value = snapshotState(err)
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
                    val code = json.optInt("error", -1)
                    val errMsg = json.optString("message").ifBlank { "Auth failed" }
                    val hint =
                        if (code == 13) {
                            "Last.fm error 13: Invalid method signature — verify 32-char Shared Secret or use Browser Auth"
                        } else {
                            "Last.fm error${if (code > 0) " $code" else ""}: $errMsg"
                        }
                    _state.value = snapshotState(hint)
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
            } catch (ce: CancellationException) {
                throw ce
            } catch (e: Exception) {
                L.w(e, "Last.fm auth failed")
                _state.value = snapshotState("Last.fm auth failed: ${e.message}")
                false
            }
        }

    suspend fun startBrowserAuth(apiKey: String, sharedSecret: String): String? =
        withContext(Dispatchers.IO) {
            val cleanKey = apiKey.trim()
            val cleanSecret = sharedSecret.trim()
            if (cleanKey.isEmpty() || cleanSecret.isEmpty()) {
                _state.value = snapshotState("Enter API Key & Shared Secret first")
                return@withContext null
            }
            validateCredentialsFormat(cleanKey, cleanSecret)?.let { err ->
                _state.value = snapshotState(err)
                return@withContext null
            }
            try {
                val signed =
                    sortedMapOf(
                        "api_key" to cleanKey,
                        "method" to "auth.getToken",
                    )
                val sig = buildApiSignature(signed, cleanSecret)
                val url =
                    "$LASTFM_API_URL?method=auth.getToken&api_key=${URLEncoder.encode(cleanKey, "UTF-8")}&api_sig=$sig&format=json"
                val json = JSONObject(httpGetString(url))
                val token = json.optString("token").takeIf { it.isNotBlank() }
                if (token == null) {
                    val code = json.optInt("error", -1)
                    val msg = json.optString("message").ifBlank { "Failed to get auth token" }
                    _state.value =
                        snapshotState(
                            if (code == 13) {
                                "Last.fm error 13: Invalid method signature — check 32-char Shared Secret"
                            } else {
                                "Last.fm error${if (code > 0) " $code" else ""}: $msg"
                            }
                        )
                    return@withContext null
                }
                prefs.edit { putString(KEY_PENDING_AUTH_TOKEN, token) }
                _state.value =
                    snapshotState("Approve access in browser, then tap Complete Browser Auth")
                "https://www.last.fm/api/auth/?api_key=${URLEncoder.encode(cleanKey, "UTF-8")}&token=${URLEncoder.encode(token, "UTF-8")}"
            } catch (ce: CancellationException) {
                throw ce
            } catch (e: Exception) {
                L.w(e, "Last.fm getToken failed")
                _state.value = snapshotState("Last.fm getToken failed: ${e.message}")
                null
            }
        }

    suspend fun completeBrowserAuth(apiKey: String, sharedSecret: String): Boolean =
        withContext(Dispatchers.IO) {
            val cleanKey = apiKey.trim()
            val cleanSecret = sharedSecret.trim()
            val token = prefs.getString(KEY_PENDING_AUTH_TOKEN, null)?.trim().orEmpty()
            if (token.isEmpty()) {
                _state.value = snapshotState("Tap Browser Auth first to request a token")
                return@withContext false
            }
            validateCredentialsFormat(cleanKey, cleanSecret)?.let { err ->
                _state.value = snapshotState(err)
                return@withContext false
            }
            try {
                val signed =
                    sortedMapOf(
                        "api_key" to cleanKey,
                        "method" to "auth.getSession",
                        "token" to token,
                    )
                val sig = buildApiSignature(signed, cleanSecret)
                val url =
                    "$LASTFM_API_URL?method=auth.getSession&api_key=${URLEncoder.encode(cleanKey, "UTF-8")}&token=${URLEncoder.encode(token, "UTF-8")}&api_sig=$sig&format=json"
                val json = JSONObject(httpGetString(url))
                val sessionObj = json.optJSONObject("session")
                val sk = sessionObj?.optString("key")?.takeIf { it.isNotBlank() }
                if (sk == null) {
                    val code = json.optInt("error", -1)
                    val msg = json.optString("message").ifBlank { "Session not authorized yet" }
                    _state.value =
                        snapshotState("Last.fm error${if (code > 0) " $code" else ""}: $msg")
                    return@withContext false
                }
                val resolvedUser = sessionObj.optString("name").ifBlank { _state.value.username }
                prefs.edit { remove(KEY_PENDING_AUTH_TOKEN) }
                updateConfig(
                    apiEnabled = true,
                    broadcastEnabled = _state.value.broadcastEnabled,
                    apiKey = cleanKey,
                    sharedSecret = cleanSecret,
                    username = resolvedUser,
                    sessionKey = sk,
                )
                true
            } catch (ce: CancellationException) {
                throw ce
            } catch (e: Exception) {
                L.w(e, "Last.fm getSession failed")
                _state.value = snapshotState("Last.fm getSession failed: ${e.message}")
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

        if (!current.apiEnabled || !current.hasValidApiSession) {
            return
        }
        if (!shouldScrobbleTrack(song.durationMs)) {
            L.d("Skipping Last.fm Web API scrobble for short track (<30s): ${entry.track}")
            return
        }

        val uid = song.uid.toString()
        val queued =
            synchronized(this@LastFmRepository) {
                val key = normalizeTrackKey(entry.artist, entry.track)
                val tail = pendingQueue.lastOrNull()
                if (
                    tail != null &&
                        normalizeTrackKey(tail.artist, tail.track) == key &&
                        abs(tail.timestampSec - entry.timestampSec) <= 5L
                ) {
                    false
                } else {
                    val localTotal = playCountRepository.getPlayCount(song)
                    val prevSynced = playCountRepository.getLastFmSyncedCount(uid)
                    val nextSynced = minOf(localTotal, prevSynced + 1)
                    if (nextSynced > prevSynced) {
                        playCountRepository.markLastFmSyncedCount(uid, nextSynced)
                    }
                    pendingQueue.add(entry)
                    _state.value = snapshotState("Queued scrobble: ${entry.track}")
                    true
                }
            }
        if (queued) {
            ioScope.launch {
                ensureDbLoaded()
                synchronized(this@LastFmRepository) { savePendingQueue() }
                flushPendingLoveOps()
                flushPendingScrobbles()
            }
        }
    }

    fun onLovedChanged(song: Song, loved: Boolean) {
        val current = _state.value
        if (!current.apiEnabled || !current.hasValidApiSession) {
            return
        }
        val artist = extractPrimaryArtist(song)
        val track = song.name.raw
        if (artist.isBlank() || track.isBlank()) {
            return
        }
        val op =
            PendingLoveOp(
                songUid = song.uid.toString(),
                artist = artist,
                track = track,
                loved = loved,
                updatedAtMs = System.currentTimeMillis(),
            )
        ioScope.launch {
            ensureDbLoaded()
            synchronized(this@LastFmRepository) {
                upsertPendingLoveOp(pendingLoveOps, op)
                savePendingLoveOps()
                _state.value =
                    snapshotState(
                        if (loved) "Queued love: ${op.track}" else "Queued unlove: ${op.track}"
                    )
            }
            flushPendingLoveOps()
        }
    }

    /**
     * Bidirectional Last.fm ledger sync (A4):
     * 1. Flush any pending local love/unlove operations before cloud pull so local unloves are not
     *    overwritten by stale `user.getlovedtracks` state.
     * 2. Pull `user.getrecenttracks` & `user.getlovedtracks` when username + apiKey are configured,
     *    updating high-water-mark play counts (`max(local, pulled)`) and Loved status.
     * 3. Backfill any local Loved tracks missing from Last.fm cloud Loved tracks.
     * 4. Compute unsynced local play deltas, enforce Last.fm's 14-day scrobble window & ±120s cloud
     *    deduplication, and upload pending batches of 50.
     */
    suspend fun syncLocalPlayHistory(): Int =
        withContext(Dispatchers.IO) {
            syncMutex.withLock {
                ensureDbLoaded()
                val songs = musicRepository.library?.songs.orEmpty()
                val nowSec = System.currentTimeMillis() / 1000L
                val initial = _state.value

                var totalPushedLoves = 0
                if (initial.apiEnabled && initial.hasValidApiSession) {
                    totalPushedLoves += flushPendingLoveOps()
                }

                var pullOutcome = CloudPullOutcome(0, 0, emptySet(), lovedPullSucceeded = false)
                val current = _state.value
                if (current.apiKey.isNotBlank() && current.username.isNotBlank()) {
                    pullOutcome = pullCloudHistoryAndLoved(current, songs)
                }
                val pulledMatched = pullOutcome.pulledMatched
                val lovedMatched = pullOutcome.lovedMatched

                val afterPull = _state.value
                if (
                    afterPull.apiEnabled &&
                        afterPull.hasValidApiSession &&
                        pullOutcome.lovedPullSucceeded &&
                        songs.isNotEmpty()
                ) {
                    var backfilledLoves = 0
                    synchronized(this@LastFmRepository) {
                        val pendingKeys =
                            pendingLoveOps
                                .mapTo(HashSet()) { normalizeTrackKey(it.artist, it.track) }
                        for (song in songs) {
                            if (!playCountRepository.isLoved(song)) continue
                            val artist = extractPrimaryArtist(song)
                            val title = song.name.raw
                            if (artist.isBlank() || title.isBlank()) continue
                            val key = normalizeTrackKey(artist, title)
                            if (key !in pullOutcome.cloudLovedKeys && key !in pendingKeys) {
                                upsertPendingLoveOp(
                                    pendingLoveOps,
                                    PendingLoveOp(
                                        songUid = song.uid.toString(),
                                        artist = artist,
                                        track = title,
                                        loved = true,
                                        updatedAtMs = System.currentTimeMillis(),
                                    ),
                                )
                                backfilledLoves++
                            }
                        }
                        if (backfilledLoves > 0) {
                            savePendingLoveOps()
                        }
                    }
                    if (backfilledLoves > 0) {
                        totalPushedLoves += flushPendingLoveOps()
                    }
                }

                val newlyQueued = mutableListOf<ScrobbleEntry>()
                val afterLoved = _state.value
                synchronized(this@LastFmRepository) {
                    var syntheticOffsetSec = 30L
                    for (song in songs) {
                        val uid = song.uid.toString()
                        val totalLocal = playCountRepository.getPlayCount(song)
                        val already = playCountRepository.getLastFmSyncedCount(uid)
                        val delta = (totalLocal - already).coerceAtLeast(0)
                        if (delta <= 0) continue

                        val trackSpacing = maxOf(song.durationSecCompat(), 180L)
                        for (step in 0 until delta) {
                            val ts =
                                (nowSec - syntheticOffsetSec).coerceAtLeast(
                                    nowSec - MAX_BACKFILL_WINDOW_SEC
                                )
                            if (shouldScrobbleTrack(song.durationMs)) {
                                newlyQueued.add(song.toScrobbleEntry(ts))
                            }
                            syntheticOffsetSec += trackSpacing
                        }
                        if (afterLoved.hasValidApiSession || afterLoved.broadcastEnabled) {
                            playCountRepository.markLastFmSyncedCount(uid, totalLocal)
                        }
                    }
                    if (newlyQueued.isNotEmpty()) {
                        if (afterLoved.apiEnabled || afterLoved.hasValidApiSession) {
                            val filtered =
                                filterAndDedupPendingScrobbles(
                                    newlyQueued,
                                    recentCloudPlaysCache,
                                    nowSec,
                                )
                            if (filtered.eligible.isNotEmpty()) {
                                pendingQueue.addAll(filtered.eligible)
                                savePendingQueue()
                            }
                        }
                    }
                }

                if (afterLoved.apiEnabled || afterLoved.hasValidApiSession) {
                    if (!afterLoved.hasValidApiSession) {
                        _state.value =
                            snapshotState(
                                "Pulled $pulledMatched plays / $lovedMatched loved · ${pendingQueue.size} pending (configure Session Key to upload)"
                            )
                        return@withLock pulledMatched + newlyQueued.size
                    }
                    val uploaded = flushPendingScrobbles()
                    if (uploaded == 0 && (pulledMatched > 0 || lovedMatched > 0 || totalPushedLoves > 0)) {
                        _state.value =
                            snapshotState(
                                "Last.fm synced: ↑$totalPushedLoves loved · ↓$pulledMatched plays / $lovedMatched loved"
                            )
                    } else if (uploaded == 0 && totalPushedLoves == 0 && pulledMatched == 0 && lovedMatched == 0) {
                        _state.value = snapshotState("No new local or cloud plays or loved tracks to sync")
                    } else if (totalPushedLoves > 0) {
                        _state.value =
                            snapshotState(
                                "Last.fm synced: ↑$uploaded plays / $totalPushedLoves loved · ↓$pulledMatched plays / $lovedMatched loved"
                            )
                    }
                    pulledMatched + uploaded + totalPushedLoves
                } else if (afterLoved.broadcastEnabled && newlyQueued.isNotEmpty()) {
                    for (entry in newlyQueued) {
                        sendAslfmsBroadcast(ASLFMS_STATE_COMPLETE, entry)
                    }
                    _state.value =
                        snapshotState("Broadcast ${newlyQueued.size} local plays via Android Scrobbler")
                    newlyQueued.size
                } else if (pulledMatched > 0 || lovedMatched > 0 || totalPushedLoves > 0) {
                    _state.value =
                        snapshotState("Pulled $pulledMatched cloud plays, $lovedMatched loved")
                    pulledMatched + totalPushedLoves
                } else {
                    _state.value = snapshotState("No new local or cloud plays to sync")
                    0
                }
            }
        }

    private data class CloudPullOutcome(
        val pulledMatched: Int,
        val lovedMatched: Int,
        val cloudLovedKeys: Set<String>,
        val lovedPullSucceeded: Boolean,
    )

    private suspend fun pullCloudHistoryAndLoved(cfg: LastFmState, songs: Collection<Song>): CloudPullOutcome {
        return try {
            val songsByKey = HashMap<String, MutableList<Song>>()
            for (song in songs) {
                val primaryArtist = extractPrimaryArtist(song)
                val key = normalizeTrackKey(primaryArtist, song.name.raw)
                songsByKey.getOrPut(key) { mutableListOf() }.add(song)
            }

            val encodedUser = URLEncoder.encode(cfg.username, "UTF-8")
            val encodedKey = URLEncoder.encode(cfg.apiKey, "UTF-8")
            val fromCursor =
                syncDb.syncKvDao().getValue(KEY_LAST_PULL_FROM_SEC)?.toLongOrNull() ?: 0L
            val fromQuery = if (fromCursor > 0L) "&from=$fromCursor" else ""

            val recentPage1Raw =
                httpGetString(
                    "$LASTFM_API_URL?method=user.getrecenttracks&user=$encodedUser&api_key=$encodedKey&limit=200&page=1$fromQuery&format=json"
                )
            val recentTotalPages = parseTotalPages(recentPage1Raw, "recenttracks").coerceAtMost(MAX_RECENT_PAGES)
            val recentList = parseRecentTracksJson(recentPage1Raw).toMutableList()
            if (recentList.isNotEmpty() && recentTotalPages > 1) {
                for (page in 2..recentTotalPages) {
                    currentCoroutineContext().ensureActive()
                    val pageRaw =
                        httpGetString(
                            "$LASTFM_API_URL?method=user.getrecenttracks&user=$encodedUser&api_key=$encodedKey&limit=200&page=$page$fromQuery&format=json"
                        )
                    val pageItems = parseRecentTracksJson(pageRaw)
                    if (pageItems.isEmpty()) break
                    recentList.addAll(pageItems)
                }
            }
            if (recentList.isNotEmpty()) {
                recentCloudPlaysCache = (recentCloudPlaysCache + recentList).distinct().takeLast(2000)
            }

            var pulledMatched = 0
            synchronized(this@LastFmRepository) {
                val deltaByUid = HashMap<String, Int>()
                for (ct in recentList) {
                    val key = normalizeTrackKey(ct.artist, ct.title)
                    val matches = songsByKey[key] ?: continue
                    val primaryUid = matches.first().uid.toString()
                    val stamp = "$primaryUid@${ct.timestampSec}"
                    if (seenCloudStamps.add(stamp)) {
                        pulledMatched++
                        for (matchSong in matches) {
                            val mUid = matchSong.uid.toString()
                            deltaByUid[mUid] = (deltaByUid[mUid] ?: 0) + 1
                        }
                    }
                }
                if (deltaByUid.isNotEmpty()) {
                    for ((uid, delta) in deltaByUid) {
                        val updatedPulled = (pulledCloudCounts[uid] ?: 0) + delta
                        pulledCloudCounts[uid] = updatedPulled
                        playCountRepository.mergeHighWaterMarkCount(uid, updatedPulled)
                    }
                    savePulledCloudCounts()
                    saveSeenCloudStamps()
                }
                val maxPulledTs = recentList.maxOfOrNull { it.timestampSec } ?: 0L
                if (maxPulledTs > 0L) {
                    val nextFrom = maxOf(fromCursor, maxPulledTs + 1L)
                    syncDb.syncKvDao().putValue(SyncKvEntity(KEY_LAST_PULL_FROM_SEC, nextFrom.toString()))
                }
            }

            val lovedPage1Raw =
                httpGetString(
                    "$LASTFM_API_URL?method=user.getlovedtracks&user=$encodedUser&api_key=$encodedKey&limit=500&page=1&format=json"
                )
            val lovedTotalPages = parseTotalPages(lovedPage1Raw, "lovedtracks").coerceAtMost(MAX_LOVED_PAGES)
            val lovedList = parseLovedTracksJson(lovedPage1Raw).toMutableList()
            if (lovedList.isNotEmpty() && lovedTotalPages > 1) {
                for (page in 2..lovedTotalPages) {
                    currentCoroutineContext().ensureActive()
                    val pageRaw =
                        httpGetString(
                            "$LASTFM_API_URL?method=user.getlovedtracks&user=$encodedUser&api_key=$encodedKey&limit=500&page=$page&format=json"
                        )
                    val pageItems = parseLovedTracksJson(pageRaw)
                    if (pageItems.isEmpty()) break
                    lovedList.addAll(pageItems)
                }
            }

            val cloudLovedKeys =
                lovedList.mapTo(HashSet(lovedList.size)) { normalizeTrackKey(it.artist, it.title) }
            val pendingUnloveKeys =
                synchronized(this@LastFmRepository) {
                    pendingLoveOps
                        .filter { !it.loved }
                        .mapTo(HashSet()) { normalizeTrackKey(it.artist, it.track) }
                }
            var lovedMatched = 0
            for (loved in lovedList) {
                val key = normalizeTrackKey(loved.artist, loved.title)
                if (key in pendingUnloveKeys) continue
                val matches = songsByKey[key] ?: continue
                for (song in matches) {
                    if (!playCountRepository.isLoved(song)) {
                        playCountRepository.setLoved(song, true)
                    }
                    lovedMatched++
                }
            }
            CloudPullOutcome(
                pulledMatched = pulledMatched,
                lovedMatched = lovedMatched,
                cloudLovedKeys = cloudLovedKeys,
                lovedPullSucceeded = true,
            )
        } catch (ce: CancellationException) {
            throw ce
        } catch (e: Exception) {
            L.w(e, "Last.fm cloud pull failed")
            CloudPullOutcome(0, 0, emptySet(), lovedPullSucceeded = false)
        }
    }

    suspend fun flushPendingLoveOps(): Int =
        withContext(Dispatchers.IO) {
            loveFlushMutex.withLock {
                ensureDbLoaded()
                val cfg = _state.value
                if (!cfg.hasValidApiSession) return@withLock 0

                var totalSent = 0
                var droppedPoison = 0
                while (true) {
                    val op =
                        synchronized(this@LastFmRepository) { pendingLoveOps.firstOrNull() }
                            ?: break
                    val outcome = postLoveOp(cfg, op)
                    if (outcome == SubmitOutcome.RETRY_LATER) break

                    synchronized(this@LastFmRepository) {
                        val key = normalizeTrackKey(op.artist, op.track)
                        val idx =
                            pendingLoveOps.indexOfFirst {
                                normalizeTrackKey(it.artist, it.track) == key &&
                                    it.updatedAtMs == op.updatedAtMs
                            }
                        if (idx >= 0) {
                            pendingLoveOps.removeAt(idx)
                            savePendingLoveOps()
                        }
                    }
                    if (outcome == SubmitOutcome.SUCCEEDED) {
                        totalSent++
                    } else {
                        droppedPoison++
                    }
                }

                if (totalSent > 0 || droppedPoison > 0) {
                    synchronized(this@LastFmRepository) {
                        _state.value =
                            snapshotState(
                                "Synced $totalSent loved updates (${pendingLoveOps.size} pending" +
                                    if (droppedPoison > 0) " · dropped $droppedPoison invalid)" else ")"
                            )
                    }
                }
                totalSent
            }
        }

    suspend fun flushPendingScrobbles(): Int =
        withContext(Dispatchers.IO) {
            scrobbleFlushMutex.withLock {
                ensureDbLoaded()
                val cfg = _state.value
                if (!cfg.hasValidApiSession) return@withLock 0

                val nowSec = System.currentTimeMillis() / 1000L
                var expiredDropped = 0
                var dedupDropped = 0
                var poisonDropped = 0
                synchronized(this@LastFmRepository) {
                    val res =
                        filterAndDedupPendingScrobbles(pendingQueue, recentCloudPlaysCache, nowSec)
                    expiredDropped = res.expiredSkipped
                    dedupDropped = res.deduplicatedSkipped
                    if (expiredDropped > 0 || dedupDropped > 0) {
                        pendingQueue.clear()
                        pendingQueue.addAll(res.eligible)
                        savePendingQueue()
                    }
                }

                var totalSent = 0
                while (true) {
                    val batch: List<ScrobbleEntry> =
                        synchronized(this@LastFmRepository) {
                            if (pendingQueue.isEmpty()) return@synchronized emptyList()
                            pendingQueue.take(MAX_BATCH_SIZE)
                        }
                    if (batch.isEmpty()) break

                    val outcome = postScrobbleBatch(cfg, batch)
                    when (outcome) {
                        SubmitOutcome.SUCCEEDED -> {
                            synchronized(this@LastFmRepository) {
                                val removeCount = minOf(batch.size, pendingQueue.size)
                                repeat(removeCount) { pendingQueue.removeAt(0) }
                                savePendingQueue()
                            }
                            totalSent += batch.size
                        }
                        SubmitOutcome.DROP_PERMANENT -> {
                            if (batch.size == 1) {
                                L.w("Dropping poisoned Last.fm scrobble item: ${batch[0].artist} - ${batch[0].track}")
                                synchronized(this@LastFmRepository) {
                                    if (pendingQueue.isNotEmpty()) {
                                        pendingQueue.removeAt(0)
                                        savePendingQueue()
                                    }
                                }
                                poisonDropped++
                            } else {
                                // Isolate invalid entry inside batch so valid siblings still upload.
                                var handledCount = 0
                                var pausedMidBatch = false
                                for (single in batch) {
                                    when (postScrobbleBatch(cfg, listOf(single))) {
                                        SubmitOutcome.SUCCEEDED -> {
                                            handledCount++
                                            totalSent++
                                        }
                                        SubmitOutcome.DROP_PERMANENT -> {
                                            L.w("Dropping poisoned Last.fm scrobble item: ${single.artist} - ${single.track}")
                                            handledCount++
                                            poisonDropped++
                                        }
                                        SubmitOutcome.RETRY_LATER -> {
                                            pausedMidBatch = true
                                            break
                                        }
                                    }
                                }
                                if (handledCount > 0) {
                                    synchronized(this@LastFmRepository) {
                                        val removeCount = minOf(handledCount, pendingQueue.size)
                                        repeat(removeCount) { pendingQueue.removeAt(0) }
                                        savePendingQueue()
                                    }
                                }
                                if (pausedMidBatch) break
                            }
                        }
                        SubmitOutcome.RETRY_LATER -> break
                    }
                }

                if (totalSent > 0 || expiredDropped > 0 || dedupDropped > 0 || poisonDropped > 0) {
                    synchronized(this@LastFmRepository) {
                        _state.value =
                            snapshotState(
                                "Synced $totalSent scrobbles (${pendingQueue.size} pending · dedup $dedupDropped · expired ${expiredDropped + poisonDropped})"
                            )
                    }
                }
                totalSent
            }
        }

    private suspend fun submitNowPlaying(cfg: LastFmState, entry: ScrobbleEntry) {
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
            val raw = postForm(LASTFM_API_URL, body)
            val json = JSONObject(raw)
            if (json.has("error")) {
                val code = json.optInt("error", -1)
                val msg = json.optString("message", "API error $code")
                L.w("Last.fm NowPlaying error $code: $msg")
                if (code == 9 || code == 13) {
                    val hint =
                        when (code) {
                            13 ->
                                "Last.fm upload error 13: Invalid method signature — verify 32-char Shared Secret"
                            else ->
                                "Last.fm upload error 9: Session expired/invalid — re-authenticate via Browser Auth"
                        }
                    _state.value = snapshotState(hint)
                }
            }
        } catch (ce: CancellationException) {
            throw ce
        } catch (e: Exception) {
            L.d("NowPlaying update failed: ${e.message}")
        }
    }

    private suspend fun postLoveOp(cfg: LastFmState, op: PendingLoveOp): SubmitOutcome {
        return try {
            val body =
                buildLoveRequestParams(
                    apiKey = cfg.apiKey,
                    sharedSecret = cfg.sharedSecret,
                    sessionKey = cfg.sessionKey,
                    artist = op.artist,
                    track = op.track,
                    loved = op.loved,
                )
            val raw = postForm(LASTFM_API_URL, body)
            val json = JSONObject(raw)
            if (json.has("error")) {
                val code = json.optInt("error", -1)
                val msg = json.optString("message", "API error $code")
                val hint =
                    when (code) {
                        13 ->
                            "Last.fm love sync error 13: Invalid method signature — verify 32-char Shared Secret"
                        9 ->
                            "Last.fm love sync error 9: Session expired/invalid — re-authenticate via Browser Auth"
                        else -> "Last.fm love sync error${if (code > 0) " $code" else ""}: $msg"
                    }
                _state.value = snapshotState(hint)
                classifyLastFmErrorCode(code)
            } else {
                SubmitOutcome.SUCCEEDED
            }
        } catch (ce: CancellationException) {
            throw ce
        } catch (e: Exception) {
            L.w(e, "Last.fm love push failed")
            _state.value = snapshotState("Last.fm love push network error (kept pending): ${e.message}")
            SubmitOutcome.RETRY_LATER
        }
    }

    private suspend fun postScrobbleBatch(cfg: LastFmState, batch: List<ScrobbleEntry>): SubmitOutcome {
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
                val code = json.optInt("error", -1)
                val msg = json.optString("message", "API error $code")
                val hint =
                    when (code) {
                        13 -> "Last.fm upload error 13: Invalid method signature — verify 32-char Shared Secret"
                        9 -> "Last.fm upload error 9: Session expired/invalid — re-authenticate via Browser Auth"
                        else -> "Last.fm upload error${if (code > 0) " $code" else ""}: $msg"
                    }
                _state.value = snapshotState(hint)
                classifyLastFmErrorCode(code)
            } else {
                SubmitOutcome.SUCCEEDED
            }
        } catch (ce: CancellationException) {
            throw ce
        } catch (e: Exception) {
            L.w(e, "Last.fm batch scrobble failed")
            _state.value = snapshotState("Last.fm network error (kept pending): ${e.message}")
            SubmitOutcome.RETRY_LATER
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

    private suspend fun httpGetString(url: String): String =
        LanHttpClient.getStringForLastFm(url, "Tonewise/${BuildConfig.VERSION_NAME} Android")

    private suspend fun postForm(url: String, params: Map<String, String>): String =
        LanHttpClient.postFormForLastFm(url, params, "Tonewise/${BuildConfig.VERSION_NAME} Android")

    private fun extractPrimaryArtist(song: Song): String =
        song.artists
            .mapNotNull { (it.name as? Name.Known)?.raw }
            .firstOrNull { it.isNotBlank() }
            ?: "Unknown Artist"

    private fun Song.toScrobbleEntry(timestampSec: Long): ScrobbleEntry {
        val artistName = extractPrimaryArtist(this)
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

    private fun snapshotState(statusMessage: String): LastFmState {
        val apiKey = prefs.getString(KEY_API_KEY, "") ?: ""
        val storedSecret = prefs.getString(KEY_SHARED_SECRET, "") ?: ""
        val username = prefs.getString(KEY_USERNAME, "") ?: ""
        val storedSessionKey = prefs.getString(KEY_SESSION_KEY, "") ?: ""
        val sharedSecret = SecretBox.decrypt(storedSecret)
        val sessionKey = SecretBox.decrypt(storedSessionKey)
        val needsSecretUpgrade =
            (storedSecret.isNotEmpty() && !SecretBox.isEncrypted(storedSecret) && sharedSecret.isNotEmpty()) ||
                (storedSessionKey.isNotEmpty() && !SecretBox.isEncrypted(storedSessionKey) && sessionKey.isNotEmpty())
        if (needsSecretUpgrade) {
            runCatching {
                prefs.edit {
                    if (storedSecret.isNotEmpty() && !SecretBox.isEncrypted(storedSecret)) {
                        putString(KEY_SHARED_SECRET, SecretBox.encrypt(sharedSecret))
                    }
                    if (storedSessionKey.isNotEmpty() && !SecretBox.isEncrypted(storedSessionKey)) {
                        putString(KEY_SESSION_KEY, SecretBox.encrypt(sessionKey))
                    }
                }
            }
        }
        val hasValidSession =
            apiKey.isNotBlank() && sharedSecret.isNotBlank() && sessionKey.isNotBlank()
        val apiEnabled =
            if (!prefs.contains(KEY_API_ENABLED) && hasValidSession) {
                true
            } else {
                prefs.getBoolean(KEY_API_ENABLED, false)
            }
        return LastFmState(
            apiEnabled = apiEnabled,
            broadcastEnabled = prefs.getBoolean(KEY_BROADCAST_ENABLED, true),
            apiKey = apiKey,
            sharedSecret = sharedSecret,
            username = username,
            sessionKey = sessionKey,
            pendingCount = pendingQueue.size,
            syncedPlayCountTotal = playCountRepository.getTotalLastFmSyncedCount(),
            statusMessage = statusMessage,
            pendingLoveCount = pendingLoveOps.size,
        )
    }

    private fun savePendingLoveOps() {
        syncDb.syncKvDao()
            .putValue(SyncKvEntity(KEY_PENDING_LOVE_JSON, serializePendingLoveOps(pendingLoveOps)))
    }

    private fun loadPendingLoveOps(): List<PendingLoveOp> =
        runCatching {
                deserializePendingLoveOps(syncDb.syncKvDao().getValue(KEY_PENDING_LOVE_JSON))
            }
            .getOrDefault(emptyList())

    private fun savePendingQueue() {
        val dao = syncDb.lastFmLedgerDao()
        val entities =
            pendingQueue.map { e ->
                PendingScrobbleEntity(
                    songUid = e.songUid,
                    artist = e.artist,
                    track = e.track,
                    album = e.album,
                    durationSec = e.durationSec,
                    timestampSec = e.timestampSec,
                )
            }
        syncDb.runInTransaction {
            dao.clearPendingScrobbles()
            if (entities.isNotEmpty()) {
                dao.insertScrobbles(entities)
            }
        }
    }

    private fun loadPendingQueue(): List<ScrobbleEntry> =
        syncDb.lastFmLedgerDao().getAllPendingScrobbles().map { e ->
            ScrobbleEntry(
                songUid = e.songUid,
                artist = e.artist,
                track = e.track,
                album = e.album,
                durationSec = e.durationSec,
                timestampSec = e.timestampSec,
            )
        }

    private fun savePulledCloudCounts() {
        playCountRepository.setLastFmPulledCounts(pulledCloudCounts)
    }

    private fun loadPulledCloudCounts(): Map<String, Int> =
        syncDb.playLedgerDao()
            .getAll()
            .filter { it.pulledCloudCount > 0 }
            .associate { it.trackUid to it.pulledCloudCount }

    private fun saveSeenCloudStamps() {
        val dao = syncDb.lastFmLedgerDao()
        val now = System.currentTimeMillis()
        val entities =
            seenCloudStamps.toList().takeLast(2000).mapIndexed { idx, stamp ->
                SeenCloudStampEntity(stamp = stamp, createdAtMs = now + idx)
            }
        syncDb.runInTransaction {
            if (entities.isNotEmpty()) {
                dao.insertSeenStamps(entities)
            }
            dao.pruneOldSeenStamps()
        }
    }

    private fun loadSeenCloudStamps(): Set<String> =
        syncDb.lastFmLedgerDao().getAllSeenStamps().toCollection(LinkedHashSet())

    enum class SubmitOutcome {
        SUCCEEDED,
        RETRY_LATER,
        DROP_PERMANENT,
    }

    data class CloudPlayRecord(val artist: String, val title: String, val timestampSec: Long)

    data class CloudLovedRecord(val artist: String, val title: String)

    data class ScrobbleFilterResult(
        val eligible: List<ScrobbleEntry>,
        val expiredSkipped: Int,
        val deduplicatedSkipped: Int,
    )

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
        private const val KEY_PULLED_COUNTS_JSON = "pulled_counts_json"
        private const val KEY_SEEN_CLOUD_STAMPS_JSON = "seen_cloud_stamps_json"
        private const val KEY_LAST_PULL_FROM_SEC = "last_pull_from_sec"
        private const val KEY_PENDING_AUTH_TOKEN = "pending_auth_token"
        private const val KEY_PENDING_LOVE_JSON = "lastfm_pending_love_ops_v1"
        private const val MIN_SCROBBLE_DURATION_MS = 30_000L

        fun classifyLastFmErrorCode(errorCode: Int): SubmitOutcome =
            when (errorCode) {
                6, // Invalid parameters (e.g. malformed artist/track/timestamp)
                7  // Invalid resource specified
                -> SubmitOutcome.DROP_PERMANENT
                else -> SubmitOutcome.RETRY_LATER
            }

        fun shouldScrobbleTrack(durationMs: Long): Boolean = durationMs >= MIN_SCROBBLE_DURATION_MS

        fun resolveAutoEnabledApiFlag(
            requestedApiEnabled: Boolean,
            hadValidSession: Boolean,
            hasValidSession: Boolean,
        ): Boolean =
            if (!hadValidSession && hasValidSession && !requestedApiEnabled) {
                true
            } else {
                requestedApiEnabled
            }

        fun upsertPendingLoveOp(ops: MutableList<PendingLoveOp>, op: PendingLoveOp) {
            val key = normalizeTrackKey(op.artist, op.track)
            ops.removeAll { normalizeTrackKey(it.artist, it.track) == key }
            ops.add(op)
        }

        fun serializePendingLoveOps(ops: List<PendingLoveOp>): String =
            TonewiseCore.stringifyJson(
                ops.map { op ->
                    linkedMapOf(
                        "songUid" to op.songUid,
                        "artist" to op.artist,
                        "track" to op.track,
                        "loved" to op.loved,
                        "updatedAtMs" to op.updatedAtMs,
                    )
                }
            )

        fun deserializePendingLoveOps(raw: String?): List<PendingLoveOp> {
            if (raw.isNullOrBlank()) return emptyList()
            return runCatching {
                    @Suppress("UNCHECKED_CAST")
                    val arr = TonewiseCore.parseJson(raw) as? List<Any?> ?: return emptyList()
                    arr.mapNotNull { item ->
                        @Suppress("UNCHECKED_CAST")
                        val map = item as? Map<String, Any?> ?: return@mapNotNull null
                        val artist = map["artist"]?.toString()?.trim().orEmpty()
                        val track = map["track"]?.toString()?.trim().orEmpty()
                        val loved = map["loved"] as? Boolean ?: return@mapNotNull null
                        if (artist.isEmpty() || track.isEmpty()) return@mapNotNull null
                        PendingLoveOp(
                            songUid = map["songUid"]?.toString().orEmpty(),
                            artist = artist,
                            track = track,
                            loved = loved,
                            updatedAtMs = (map["updatedAtMs"] as? Number)?.toLong() ?: 0L,
                        )
                    }
                }
                .getOrDefault(emptyList())
        }

        fun buildLoveRequestParams(
            apiKey: String,
            sharedSecret: String,
            sessionKey: String,
            artist: String,
            track: String,
            loved: Boolean,
        ): Map<String, String> {
            val signed =
                sortedMapOf(
                    "api_key" to apiKey,
                    "artist" to artist,
                    "method" to if (loved) "track.love" else "track.unlove",
                    "sk" to sessionKey,
                    "track" to track,
                )
            val sig = buildApiSignature(signed, sharedSecret)
            return signed.toMutableMap().apply {
                put("api_sig", sig)
                put("format", "json")
            }
        }

        fun validateCredentialsFormat(apiKey: String, sharedSecret: String): String? {
            val k = apiKey.trim()
            val s = sharedSecret.trim()
            if (k.length != 32) {
                return "Invalid API Key length (${k.length} chars; expected 32-char hex from last.fm/api/account)"
            }
            if (s.length != 32) {
                return "Invalid Shared Secret length (${s.length} chars; expected 32-char hex from last.fm/api/account)"
            }
            return null
        }
        private const val LASTFM_API_URL = "https://ws.audioscrobbler.com/2.0/"
        private const val ACTION_ASLFMS_PLAYSTATE = "com.adam.aslfms.notify.playstatechanged"
        private const val ASLFMS_STATE_START = 0
        private const val ASLFMS_STATE_COMPLETE = 3
        private const val MAX_BATCH_SIZE = 50
        private const val MAX_BACKFILL_WINDOW_SEC = 13L * 24L * 3600L
        private const val LASTFM_WINDOW_SEC = 14L * 24L * 3600L
        private const val CLOUD_DEDUP_TOLERANCE_SEC = 120L
        private const val MAX_RECENT_PAGES = 15
        private const val MAX_LOVED_PAGES = 25

        private val WHITESPACE_REGEX = Regex("\\s+")

        fun normalizeTrackKey(artist: String, title: String): String {
            val normArtist = WHITESPACE_REGEX.replace(artist.trim().lowercase(), " ")
            val normTitle = WHITESPACE_REGEX.replace(title.trim().lowercase(), " ")
            return "$normArtist||$normTitle"
        }

        fun filterAndDedupPendingScrobbles(
            entries: List<ScrobbleEntry>,
            cloudPlays: List<CloudPlayRecord>,
            nowSec: Long,
        ): ScrobbleFilterResult {
            val cloudByKey =
                cloudPlays.groupBy { normalizeTrackKey(it.artist, it.title) }
            val eligible = ArrayList<ScrobbleEntry>(entries.size)
            var expired = 0
            var dedup = 0
            for (entry in entries) {
                if (nowSec - entry.timestampSec > LASTFM_WINDOW_SEC) {
                    expired++
                    continue
                }
                val key = normalizeTrackKey(entry.artist, entry.track)
                val matchedCloud =
                    cloudByKey[key]?.any { abs(it.timestampSec - entry.timestampSec) <= CLOUD_DEDUP_TOLERANCE_SEC } ==
                        true
                if (matchedCloud) {
                    dedup++
                    continue
                }
                eligible.add(entry)
            }
            return ScrobbleFilterResult(eligible, expired, dedup)
        }

        fun parseTotalPages(rawJson: String, rootKey: String): Int =
            runCatching {
                    @Suppress("UNCHECKED_CAST")
                    val root = TonewiseCore.parseJson(rawJson) as? Map<String, Any?> ?: return 1
                    @Suppress("UNCHECKED_CAST")
                    val container = root[rootKey] as? Map<String, Any?> ?: return 1
                    @Suppress("UNCHECKED_CAST")
                    val attr = container["@attr"] as? Map<String, Any?> ?: return 1
                    val raw = attr["totalPages"]
                    val parsed =
                        when (raw) {
                            is Number -> raw.toInt()
                            is String -> raw.trim().toIntOrNull()
                            else -> null
                        }
                    (parsed ?: 1).coerceAtLeast(1)
                }
                .getOrDefault(1)

        fun parseRecentTracksJson(rawJson: String): List<CloudPlayRecord> {
            return runCatching {
                    val root = JSONObject(rawJson)
                    val rt = root.optJSONObject("recenttracks") ?: return emptyList()
                    val arr =
                        when (val rawTrack = rt.opt("track")) {
                            is JSONArray -> rawTrack
                            is JSONObject -> JSONArray().put(rawTrack)
                            null -> return emptyList()
                            else -> return emptyList()
                        }
                    val out = ArrayList<CloudPlayRecord>(arr.length())
                    for (i in 0 until arr.length()) {
                        val tr = arr.optJSONObject(i) ?: continue
                        val attr = tr.optJSONObject("@attr")
                        if (attr?.optString("nowplaying", "").equals("true", ignoreCase = true)) {
                            continue
                        }
                        val uts =
                            tr.optJSONObject("date")?.optString("uts", "")?.toLongOrNull() ?: continue
                        val artist =
                            tr.optJSONObject("artist")?.optString("#text", "")?.trim() ?: ""
                        val title = tr.optString("name", "").trim()
                        if (artist.isNotEmpty() && title.isNotEmpty()) {
                            out.add(CloudPlayRecord(artist, title, uts))
                        }
                    }
                    out
                }
                .getOrDefault(emptyList())
        }

        fun parseLovedTracksJson(rawJson: String): List<CloudLovedRecord> {
            return runCatching {
                    val root = JSONObject(rawJson)
                    val lt = root.optJSONObject("lovedtracks") ?: return emptyList()
                    val arr =
                        when (val rawTrack = lt.opt("track")) {
                            is JSONArray -> rawTrack
                            is JSONObject -> JSONArray().put(rawTrack)
                            null -> return emptyList()
                            else -> return emptyList()
                        }
                    val out = ArrayList<CloudLovedRecord>(arr.length())
                    for (i in 0 until arr.length()) {
                        val tr = arr.optJSONObject(i) ?: continue
                        val artistObj = tr.optJSONObject("artist")
                        val artist =
                            (artistObj?.optString("name", "")?.takeIf { it.isNotBlank() }
                                    ?: artistObj?.optString("#text", ""))
                                ?.trim()
                                ?: ""
                        val title = tr.optString("name", "").trim()
                        if (artist.isNotEmpty() && title.isNotEmpty()) {
                            out.add(CloudLovedRecord(artist, title))
                        }
                    }
                    out
                }
                .getOrDefault(emptyList())
        }

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
